using System.Diagnostics;
using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    int countedWaterChunks = -1, waterBodyCount;
    Guid countedWaterLocation;
    public int WaterBodyCount
    {
        get
        {
            if (terrainStore is null)
                return 0;
            if (countedWaterChunks != terrainStore.Count || countedWaterLocation != World.Definition.LocationId)
            {
                HashSet<int> ids = [];
                foreach (var chunk in terrainStore.Chunks)
                    foreach (int id in chunk.WaterBodies.Span)
                        if (id > 0)
                            ids.Add(id);
                countedWaterChunks = terrainStore.Count;
                countedWaterLocation = World.Definition.LocationId;
                waterBodyCount = ids.Count;
            }
            return waterBodyCount;
        }
    }

    TerrainStore? terrainStore;
    GenerationService? meshGeneration;
    sealed record Resident(int Step, Mesh3D Terrain, Mesh3D? Water, int Vertices, AxisAlignedBoundingBox Bounds, IReadOnlyList<RockInstance> Rocks, IReadOnlyList<TreeInstance> Trees, bool Far = false) : IDisposable
    {
        public void Dispose()
        {
            Terrain.Dispose();
            Water?.Dispose();
        }
    }
    readonly Dictionary<ChunkCoordinate, Resident> resident = [];
    readonly Dictionary<ulong, string> treeLods = [];
    readonly Dictionary<ChunkCoordinate, (int Step, Task<TerrainMeshData> Task)> pendingMeshes = [];
    public int ResidentMeshes => resident.Count;
    public int ResidentRocks => resident.Values.Sum(r => r.Rocks.Count);
    public int ResidentTrees => resident.Values.Sum(r => r.Trees.Count);
    public int PendingMeshes => pendingMeshes.Count;
    // Resident chunks already have drawable scenery fallbacks. Higher tree LODs
    // and unrelated building jobs must not extend the arrival transition.
    public bool ArrivalRegionReady(Vector3 position) => ChunkCoordinate.At(position.X, position.Z).Around(1)
        .Where(c => c.Valid).All(c => resident.TryGetValue(c, out var mesh) && !mesh.Far);
    public long ApproximateGpuBytes => TerrainMaterialBytes + FoliageBytes + DustBytes + WeatherParticleBytes + GrassBytes + ModelGpuBytes + resident.Values.Sum(r => (long)r.Vertices * 36);
    public double LastUploadMilliseconds
    {
        get;
        private set;
    }
    Vector3 streamingEye = new(float.PositiveInfinity), lodEye = new(float.PositiveInfinity);
    int streamingCount = -1, streamingFarCount = -1, streamingRadius;
    (ChunkCoordinate Coordinate, float Distance)[] streamOrder = [];
    IReadOnlyDictionary<FarTileKey, FarTile> farScene = new Dictionary<FarTileKey, FarTile>();
    int detailRadius = 384;
    public double MeshPreparationMilliseconds => meshGeneration?.MeshMilliseconds ?? 0;
    public int FarMeshCount => resident.Values.Count(r => r.Far);
    public void SupplyStreaming(ClientSession session)
    {
        detailRadius = Settings.Quality switch
        {
            GraphicsQuality.Low => 256,
            GraphicsQuality.High => 512,
            _ => 384
        };
        session.Streaming = new(detailRadius);
        farScene = session.FarScene;
        SupplyDestination(session);
    }
    IEnumerable<bool> UploadResident(ChunkCoordinate coordinate, TerrainMeshData data, IReadOnlyList<RockInstance> rocks, IReadOnlyList<TreeInstance> trees, int step, bool far = false)
    {
        Mesh3D? ground = null, water = null;
        bool complete = false;
        try
        {
            ground = graphics.CreateMesh3D();
            var colors = TerrainColors(data);
            foreach (bool upload in ground.UploadGeometry(data.Positions, data.Normals, data.Ecology.Length==data.Positions.Length?data.Ecology:new Vector2[data.Positions.Length], colors: colors))
                yield return upload;
            if (data.WaterPositions.Length > 0)
            {
                water = graphics.CreateMesh3D();
                foreach (bool upload in water.UploadGeometry(data.WaterPositions, TerrainMesher.WaterAttributes(data), data.WaterFlow))
                    yield return upload;
            }
            var bounds = AxisAlignedBoundingBox.FromPoints(data.Positions.Concat(data.WaterPositions));
            foreach (var r in rocks)
                bounds = bounds.Include(r.Min).Include(r.Max);
            foreach (var t in trees)
                bounds = bounds.Include(t.Min - new Vector3(.6f)).Include(t.Max + new Vector3(.6f));
            ReplaceResident(coordinate, new(step, ground, water, data.Positions.Length + data.WaterPositions.Length, bounds, rocks, trees, far));
            complete = true;
        }
        finally
        {
            if (!complete) { ground?.Dispose(); water?.Dispose(); }
        }
    }
    IEnumerator<bool>? terrainUpload;
    ChunkCoordinate uploadingCoordinate;
    bool uploadingFar;
    void PumpTerrainUpload(Stopwatch budget, double budgetMs)
    {
        while (terrainUpload is not null && budget.Elapsed.TotalMilliseconds < budgetMs)
        {
            if (!terrainUpload.MoveNext())
            {
                terrainUpload.Dispose();
                terrainUpload = null;
                break;
            }
            if (!terrainUpload.Current) break;
        }
    }
    void ReplaceResident(ChunkCoordinate c, Resident next)
    {
        if (resident.Remove(c, out var old))
        {
            old.Dispose();
            if (!old.Far)
                meshGeneration?.ReleaseMesh(c, old.Step);
        }
        resident[c] = next;
        shadows.InvalidateStaticCaster(next.Bounds);
        InvalidateGateShadows(next.Bounds);
        InvalidateGroundMist(next.Bounds);
    }
    public void UpdateStreaming(Vector3 eye, double budgetMs = 2, bool preparing = false)
    {
        PollDevelopmentAssets();
        if (frameNumber % 60 == 0) MaintainModelResidency();
        if (meshGeneration is null || terrainStore is null)
        {
            PumpModels(budgetMs);
            if (!preparing) PumpOptionalProgram(Math.Max(0, budgetMs - ModelUploadMilliseconds));
            return;
        }
        if (Vector3.DistanceSquared(streamingEye, eye) >= 4 || streamingCount != terrainStore.Count || streamingFarCount != farScene.Count || streamingRadius != detailRadius)
        {
            streamingEye = eye;
            streamingCount = terrainStore.Count;
            streamingFarCount = farScene.Count;
            streamingRadius = detailRadius;
            streamOrder = ChunkCoordinate.All.Select(c => (c, StreamingSettings.Distance(c, eye))).OrderBy(p => p.Item2).ToArray();
        }
        foreach (var item in streamOrder)
        {
            var c = item.Coordinate;
            if (item.Distance > detailRadius + 64)
                continue;
            // Grass roots sample the canonical metre grid. Coarsening terrain
            // inside their range exposes floating grass slabs on uneven slopes.
            int grassRange = GroundCoverSettings.Enabled ? GroundCoverSettings.EffectiveBudget((int)Settings.Quality).Range : 0;
            int step = item.Distance < Math.Max(128, grassRange + 16) ? 1 : item.Distance < 256 ? 2 : 4;
            if (terrainUpload is not null && c == uploadingCoordinate || !terrainStore.TryGetChunk(c, out _) || pendingMeshes.ContainsKey(c) || resident.TryGetValue(c, out var current) && !current.Far && current.Step == step)
                continue;
            if (pendingMeshes.Count >= 12)
                break;
            pendingMeshes[c] = (step, meshGeneration.RequestMeshAsync(c, step));
        }
        var budget = Stopwatch.StartNew();
        if (!preparing) WarmTerrainPalette(World.Definition.Preset, Math.Min(.5, budgetMs));
        PumpModels(Math.Min(1, Math.Max(0, budgetMs - budget.Elapsed.TotalMilliseconds)));
        if (terrainUpload is not null && !uploadingFar && StreamingSettings.Distance(uploadingCoordinate, eye) > detailRadius + 128)
        {
            terrainUpload.Dispose();
            terrainUpload = null;
        }
        PumpTerrainUpload(budget, budgetMs);
        foreach (var item in streamOrder)
        {
            if (terrainUpload is not null || budget.Elapsed.TotalMilliseconds >= budgetMs) break;
            var c = item.Coordinate;
            if (pendingMeshes.TryGetValue(c, out var pending) && pending.Task.IsCompleted)
            {

                if (!pending.Task.IsCompletedSuccessfully)
                {
                    pendingMeshes.Remove(c);
                    _ = pending.Task.Exception;
                    meshGeneration.ReleaseMesh(c, pending.Step);
                    continue;
                }
                if (item.Distance > detailRadius + 64 || !terrainStore.TryGetChunk(c, out var canonical))
                {
                    pendingMeshes.Remove(c);
                    meshGeneration.ReleaseMesh(c, pending.Step);
                    continue;
                }
                var rocks = RockScenery.Resolve(canonical, World.Definition.Preset);
                var trees = TreeScenery.Resolve(canonical, World.Definition.Preset, World.Definition.Fixture);
                if (!RequestScenery(rocks, trees))
                    continue;
                pendingMeshes.Remove(c);
                uploadingFar = false;
                uploadingCoordinate = c;
                terrainUpload = UploadResident(c, pending.Task.Result, rocks, trees, pending.Step).GetEnumerator();
            }
            PumpTerrainUpload(budget, budgetMs);
            if (budget.Elapsed.TotalMilliseconds >= budgetMs)
                break;
        }
        var desiredFar=FarTileKey.Cover(eye,detailRadius).ToHashSet();
        foreach(var pair in farScene.OrderBy(p => p.Key.Distance(eye)))
        {
            if(terrainUpload is not null || budget.Elapsed.TotalMilliseconds >= budgetMs) break;
            if(!desiredFar.Contains(pair.Key))continue;
            var c=pair.Key.RenderKey;
            if(resident.ContainsKey(c)) continue;
            uploadingCoordinate=c; uploadingFar=true;
            terrainUpload=UploadResident(c,pair.Value.Mesh,[],[],8<<pair.Key.Level,true).GetEnumerator();
            PumpTerrainUpload(budget,budgetMs);
        }
        UpdateTerrainCoverage(eye);
        LastUploadMilliseconds = budget.Elapsed.TotalMilliseconds;
        if (LastUploadMilliseconds > budgetMs) StartupTrace.Mark("upload-admission-overrun", LastUploadMilliseconds);
        if (preparing)
            return;
        RefreshCoverExclusions();
        PrepareSurfaceField(eye, LastUploadMilliseconds);
        PrepareFoliage(eye, LastUploadMilliseconds + SurfaceCpuMilliseconds);
        UpdateGroundCover(eye, LastUploadMilliseconds + SurfaceCpuMilliseconds + FoliageCpuMilliseconds);
        PumpOptionalProgram(Math.Max(0, budgetMs - budget.Elapsed.TotalMilliseconds));
        WarmDestination(Math.Min(.5, Math.Max(0, budgetMs - budget.Elapsed.TotalMilliseconds)));
    }
    public IEnumerable<DebugLine> PlacementDebug(Vector3 eye)
    {
        if (terrainStore is null)
            yield break;
        foreach (var chunk in terrainStore.Chunks.Where(c => Math.Abs(c.Coordinate.X * 64 + 32 - eye.X) < 164 && Math.Abs(c.Coordinate.Z * 64 + 32 - eye.Z) < 164))
            foreach (var candidate in chunk.Placements)
            {
                if (Vector3.DistanceSquared(candidate.Position, eye) > 100 * 100 || DebugWorld?.Changes.IsRemoved(candidate.Category, candidate.Id) == true)
                    continue;
                var p = candidate.Position + new Vector3(0, .1f, 0);
                var color = candidate.Category == "tree" ? new Vector3(.2f, 1, .3f) : new Vector3(.8f, .6f, .3f);
                yield return new(p, p + Vector3.UnitY * 2, color);
                yield return new(p - Vector3.UnitX, p + Vector3.UnitX, color);
                yield return new(p - Vector3.UnitZ, p + Vector3.UnitZ, color);
            }
    }
    void DisposeStreaming()
    {
        terrainUpload?.Dispose();
        terrainUpload = null;
        ResetVisibility();
        visibilityPool.Clear();
        ClearAtmosphere();
        meshGeneration?.Dispose();
        meshGeneration = null;
        pendingMeshes.Clear();
        foreach (var r in resident.Values)
            r.Dispose();
        resident.Clear();
        treeLods.Clear();
        streamingCount = -1;
        streamingFarCount = -1;
    }
}
