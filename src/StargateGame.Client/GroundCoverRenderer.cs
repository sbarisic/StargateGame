using System.Diagnostics;
using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Building;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public GroundCoverSettings GroundCoverSettings
    {
        get;
        set;
    } = new();
    sealed record GrassResident(Texture? Records, int Count, int Texels, float[] Ranks, AxisAlignedBoundingBox Bounds, float Born, float MaximumRank, Texture? Surface = null, Vector4[]? PatchRanges = null) : IDisposable
    {
        public void Dispose() { Records?.Dispose(); Surface?.Dispose(); }
    }
    readonly Dictionary<GrassTile, GrassResident> grassTiles = [];
    readonly Dictionary<GrassTile, (int Revision, float MaximumRank, Task<GroundCover.InstanceData> Task)> grassJobs = [];
    readonly List<(GrassTile Tile, float Distance)> coverWanted = [];
    readonly HashSet<GrassTile> coverKeep = [];
    Vector2 coverWantedEye;
    int coverWantedRange = -1;
    readonly Dictionary<int, Mesh3D> grassTemplates = [];
    public int GrassFarTiles
    {
        get;
        private set;
    }
    public int GrassClumps
    {
        get;
        private set;
    }
    readonly Dictionary<ChunkCoordinate, (int Revision, Task<byte[]> Task)> meadowJobs = [];
    readonly HashSet<ChunkCoordinate> meadowReady = [];
    Texture? meadowMask;
    Vector3 grassEye;
    public int GrassDetailedTiles
    {
        get;
        private set;
    }
    public int GrassSimpleTiles
    {
        get;
        private set;
    }
    public long GrassUploadBytes
    {
        get;
        private set;
    }
    readonly Dictionary<ChunkCoordinate, CoverExclusion[]> coverScenery = [];
    CancellationTokenSource grassCancellation = new();
    CoverExclusion[] coverBuildings = [];
    WorldChanges? coverWorldState;
    GroundCoverSettings? previousCoverSettings;
    Texture? grassBladeColor, grassBladeNormal;
    ShaderProgram? grassShader;
    int grassRevision;
    readonly Stopwatch coverClock = Stopwatch.StartNew();
    public int GrassTiles => grassTiles.Count;
    public int GrassDensity => GroundCoverSettings.EffectiveBudget((int)Settings.Quality).Density;
    public int GrassTriangles
    {
        get;
        private set;
    }
    public int GrassPending => grassJobs.Count;
    public long GrassWorldBytes => GrassBytes - GrassVolumeBytes - grassTemplates.Keys.Sum(k => (long)(k == 0 ? 336 : k == 1 ? 72 : 6) * 12);
    public long GrassBytes => GrassVolumeBytes + grassTiles.Values.Count(t => t.Surface is not null) * 33L * 33 * 16 + grassTiles.Values.Sum(t => t.Records is null ? 0 : (long)t.Texels * 16) + (meadowMask is null ? 0 : WorldDomain.Size * WorldDomain.Size) + grassTemplates.Keys.Sum(k => (long)(k == 0 ? 336 : k == 1 ? 72 : 6) * 12);
    public double GrassCpuMilliseconds
    {
        get;
        private set;
    }
    public double GrassGpuMilliseconds => gpuTimer.GrassMilliseconds;
    void ClearGroundCover()
    {
        grassCancellation.Cancel();
        grassCancellation.Dispose();
        grassCancellation = new();
        foreach (var tile in grassTiles.Values)
            tile.Dispose();
        grassTiles.Clear();
        grassJobs.Clear();
        coverWanted.Clear(); coverKeep.Clear(); coverWantedRange = -1;
        coverScenery.Clear();
        coverWorldState = null;
        coverBuildings = [];
        grassRevision++;
        Array.Clear(GrassLodCounts);
        GrassTriangles = 0;
        GrassUploadBytes = 0;
        meadowJobs.Clear();
        meadowReady.Clear();
        meadowMask?.Dispose();
        meadowMask = null;
    }
    void RefreshCoverExclusions()
    {
        if (ReferenceEquals(coverWorldState, buildingState))
            return;
        var next = buildingState.Pieces.Select(p => BuildingGeometry.Bounds(BuildingGeometry.Mesh(buildingState, p)))
            .Select(b => new CoverExclusion(b.Min, b.Max)).Distinct().ToArray();
        var changed = coverBuildings.Except(next).Concat(next.Except(coverBuildings)).ToList();
        if (coverWorldState is not null && !coverWorldState.Removed.SequenceEqual(buildingState.Removed))
        {
            foreach (var value in grassTiles.Values)
                value.Dispose();
            grassTiles.Clear();
            coverScenery.Clear();
        }
        foreach (var tile in grassTiles.Keys.ToArray())
            if (changed.Any(e => e.Max.X >= tile.X * 8 && e.Min.X < tile.X * 8 + 8 && e.Max.Z >= tile.Z * 8 && e.Min.Z < tile.Z * 8 + 8))
            {
                grassTiles[tile].Dispose();
                grassTiles.Remove(tile);
            }
        coverBuildings = next;
        coverWorldState = buildingState;
        grassRevision++;
        meadowReady.Clear();
    }
    CoverExclusion[]? CoverObstacles(GrassTile tile, int size = 8)
    {
        if (terrainStore is null)
            return null;
        var owner = ChunkCoordinate.At(tile.X * 8, tile.Z * 8);
        List<CoverExclusion> result = [];
        foreach (var c in owner.Around(1).Where(c => c.Valid))
        {
            if (!terrainStore.TryGetChunk(c, out var chunk))
                return null;
            if (!coverScenery.TryGetValue(c, out var exclusions))
            {
                List<CoverExclusion> values = [];
                foreach (var rock in RockScenery.Resolve(chunk, World.Definition.Preset).Where(r => !removedScenery.Contains(("rock", r.Id))))
                    values.Add(new(rock.Min, rock.Max));
                foreach (var tree in TreeScenery.Resolve(chunk, World.Definition.Preset, World.Definition.Fixture).Where(t => !removedScenery.Contains(("tree", t.Id))))
                {
                    // Root meshes spread many metres from the trunk. Their AABB
                    // clears every gap between roots, producing huge bare squares.
                    // Reserve the trunk only; the shared canopy field controls
                    // understorey density and solid root geometry occludes grass.
                    var trunk = tree.Transform.Translation;
                    float radius = Math.Clamp((tree.Max.Y - tree.Min.Y) * .025f, .35f, 1f);
                    values.Add(new(new(trunk.X - radius, tree.Min.Y - .2f, trunk.Z - radius),
                        new(trunk.X + radius, tree.Max.Y, trunk.Z + radius)));
                }
                coverScenery[c] = exclusions = values.ToArray();
            }
            result.AddRange(exclusions.Where(e => e.Max.X >= tile.X * 8 - .2f && e.Min.X < tile.X * 8 + size + .2f && e.Max.Z >= tile.Z * 8 - .2f && e.Min.Z < tile.Z * 8 + size + .2f));
        }
        result.AddRange(coverBuildings.Where(e => e.Max.X >= tile.X * 8 - .2f && e.Min.X < tile.X * 8 + size + .2f && e.Max.Z >= tile.Z * 8 - .2f && e.Min.Z < tile.Z * 8 + size + .2f));
        return result.ToArray();
    }
    void UpdateGroundCover(Vector3 eye, double usedMilliseconds)
    {
        grassEye = eye;
        var watch = Stopwatch.StartNew();
        try
        {
            PrepareGroundCover(eye, usedMilliseconds);
        }
        finally
        {
            GrassCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
        }
    }
    void PrepareGroundCover(Vector3 eye, double usedMilliseconds)
    {
        if (!GroundCoverSettings.Enabled || !terrainGrass.Any(value => value > 0) || terrainStore is null)
        {
            if (grassTiles.Count + grassJobs.Count > 0)
                ClearGroundCover();
            return;
        }
        if (previousCoverSettings != GroundCoverSettings)
        {
            ClearGroundCover();
            previousCoverSettings = GroundCoverSettings;
        }
        PrepareGrassVolumes();
        RefreshCoverExclusions();
        // Terrain and foliage share the surface field; no separate meadow-colour mask.
        var (range, density) = GroundCoverSettings.EffectiveBudget((int)Settings.Quality);
        var focus = GrassTile.At(eye.X, eye.Z);
        var eye2 = new Vector2(eye.X, eye.Z);
        // Keep the small streaming margin, but avoid allocating and sorting
        // thousands of identical requests every stationary render frame.
        if (coverWantedRange != range || Vector2.DistanceSquared(coverWantedEye, eye2) >= 4)
        {
            coverWanted.Clear(); coverKeep.Clear(); coverWantedEye = eye2; coverWantedRange = range;
            int radius = (range + 15) / 8;
            for (int z = focus.Z - radius; z <= focus.Z + radius; z++)
                for (int x = focus.X - radius; x <= focus.X + radius; x++)
            {
                var tile = new GrassTile(x, z);
                float distance = Vector2.Distance(tile.Centre, eye2);
                if (distance < range + 12 && ChunkCoordinate.At(x * 8, z * 8).Valid)
                {
                    coverWanted.Add((tile, distance)); coverKeep.Add(tile);
                }
            }
            coverWanted.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }
        var keep = coverKeep;
        foreach (var tile in grassTiles.Keys.Where(t => !keep.Contains(t)).ToArray())
        {
            grassTiles[tile].Dispose();
            grassTiles.Remove(tile);
        }
        foreach (var item in coverWanted)
        {
            var tile = item.Tile;
            float distance = Math.Max(0, item.Distance - 6);

            // Prepare the dense records before the player enters their visibility band.
            // Promotion preserves candidate IDs and roots; no distance-based rerolling.
            float maximumRank = GroundCoverSettings.Hybrid || distance < 40 ? 1f : .25f;
            if (grassJobs.ContainsKey(tile) || grassTiles.TryGetValue(tile, out var residentTile) && residentTile.MaximumRank >= maximumRank)
                continue;
            if (grassJobs.Count >= 4)
                break;
            if (!terrainStore.TryGetChunk(ChunkCoordinate.At(tile.X * 8, tile.Z * 8), out var chunk))
                continue;
            var exclusions = CoverObstacles(tile);
            if (exclusions is null || !surfaceMaps.TryGetValue(chunk.Coordinate, out var ecology))
                continue;
            var definition = World.Definition;
            var settings = GroundCoverSettings;
            var token = grassCancellation.Token;
            bool volumeOnly = settings.Hybrid && grassVolumes is not null && hybridGrassShader is not null && hybridMeshShader is not null;
            grassJobs[tile] = (grassRevision, maximumRank, Task.Run(() => GroundCover.Instances(tile, chunk, definition, settings, exclusions, token, ecology, maximumRank, volumeOnly), token));
        }
        var clock = Stopwatch.StartNew();
        int published = 0;
        foreach (var tile in grassJobs.Keys.OrderBy(t => Vector2.DistanceSquared(t.Centre, eye2)).ToArray())
        {
            var job = grassJobs[tile];
            if (!job.Task.IsCompleted)
                continue;
            // One small ready tile must make progress even while moving/streaming.
            if (published > 0 && clock.Elapsed.TotalMilliseconds + usedMilliseconds >= 2)
                break;
            grassJobs.Remove(tile);
            if (job.Task.IsCanceled || !keep.Contains(tile) || job.Revision != grassRevision)
                continue;
            if (job.Task.IsFaulted)
                throw new InvalidOperationException("Ground cover preparation failed.", job.Task.Exception);
            var data = job.Task.Result;
            Texture? records = null, surface = null;
            try
            {
                if (data.Records.Length > 0 && data.Count > 0)
                {
                    records = graphics.CreateTexture(new TextureDescriptor(128, data.Records.Length / 128, TextureFormat.RGBA32Float));
                    records.Write<Vector4>(data.Records, TextureDataFormat.RGBA32Float);
                    GrassUploadBytes += data.Records.Length * 16;
                }
                if (data.Surface is not null)
                {
                    surface = graphics.CreateTexture(new TextureDescriptor(33, 33, TextureFormat.RGBA32Float, sampling: new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge)));
                    surface.Write<Vector4>(data.Surface, TextureDataFormat.RGBA32Float);
                }
                float born = (float)coverClock.Elapsed.TotalSeconds;
                if (grassTiles.Remove(tile, out var old))
                {
                    born = old.Born;
                    old.Dispose();
                }
                float[] ranks = records is null ? [] : Enumerable.Range(0, data.Count).Select(i => data.Records[i * 6 + 1].Y).ToArray();
                grassTiles[tile] = new(records, data.Count, data.Records.Length, ranks, new(data.Min, data.Max), born, job.MaximumRank, surface, data.Surface is null ? null : GrassRendering.PatchRanges(data.Surface));
                published++;
            }
            catch
            {
                records?.Dispose(); surface?.Dispose();
                throw;
            }
        }
    }
    void PrepareMeadowMask(Vector3 eye, double usedMilliseconds)
    {
        GrassUploadBytes = 0;
        if (meadowMask is null)
        {
            meadowMask = graphics.CreateTexture(new TextureDescriptor(WorldDomain.Size, WorldDomain.Size, TextureFormat.R8Unorm, sampling: new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge)));
            meadowMask.Write<byte>(new byte[WorldDomain.Size * WorldDomain.Size], TextureDataFormat.R8Unorm);
        }
        foreach (var chunk in terrainStore!.Chunks.OrderBy(c => Vector2.DistanceSquared(new(c.Coordinate.X * 64 + 32, c.Coordinate.Z * 64 + 32), new(eye.X, eye.Z))))
        {
            var c = chunk.Coordinate;
            if (meadowReady.Contains(c) || meadowJobs.ContainsKey(c))
                continue;
            if (meadowJobs.Count >= 2)
                break;
            var exclusions = CoverObstacles(new(c.X * 8, c.Z * 8), 64);
            if (exclusions is null)
                continue;
            var token = grassCancellation.Token;
            var planet = World.Definition;
            meadowJobs[c] = (grassRevision, Task.Run(() => GroundCover.CoverageMap(chunk, planet, exclusions, token), token));
        }
        var clock = Stopwatch.StartNew();
        foreach (var c in meadowJobs.Keys.ToArray())
        {
            var job = meadowJobs[c];
            if (!job.Task.IsCompleted || clock.Elapsed.TotalMilliseconds + usedMilliseconds >= 1)
                continue;
            meadowJobs.Remove(c);
            if (job.Task.IsCanceled || job.Revision != grassRevision)
                continue;
            meadowMask.Write<byte>(job.Task.GetAwaiter().GetResult(), TextureDataFormat.R8Unorm, new TextureRegion(c.X * 64 + WorldDomain.HalfSize, c.Z * 64 + WorldDomain.HalfSize, 64, 64));
            meadowReady.Add(c);
            GrassUploadBytes += 4096;
        }
    }
    Mesh3D GrassTemplate(int lod)
    {
        if (grassTemplates.TryGetValue(lod, out var result))
            return result;
        result = Own(graphics.CreateMesh3D());
        result.SetVertices(new Vector3[lod == 0 ? 336 : lod == 1 ? 72 : 6]);
        grassTemplates.Add(lod, result);
        return result;
    }
    void MeadowUniforms(ShaderProgram program)
    {
        program.SetUniform("uMeadowMask", 23);
        program.SetUniform("uMeadowEnabled", GroundCoverSettings.Enabled && terrainGrass.Any(value => value > 0) ? 1 : 0);
        program.SetUniform("uMeadowPatchStrength", GroundCoverSettings.PatchStrength);
        program.SetUniform("uMeadowRange", (float)GroundCoverSettings.EffectiveBudget((int)Settings.Quality).Range);
        program.SetUniform("uMeadowTime", (float)weatherFrame.Time);
        program.SetUniform("uMeadowWind", weatherFrame.Wind);
    }
    IDisposable BindMeadow() => (GroundCoverSettings.Enabled ? meadowMask ?? white : white).Bind(23);

    void DrawGroundCover(RenderPass pass, ViewFrustum frustum, Camera camera, Matrix4x4 inverse)
    {
        Array.Clear(GrassLodCounts);
        GrassTriangles = 0;
        GrassDetailedTiles = 0;
        GrassSimpleTiles = 0;
        GrassFarTiles = 0;
        GrassClumps = 0;
        GrassVolumePatches = 0;
        if (grassTiles.Count == 0)
            return;
        if (DrawHybridGrass(pass, camera, inverse, frustum)) return;
        using var meshTiming = gpuTimer.Measure(pass, "grass-mesh");
        grassShader ??= RequestOptionalProgram("grass.vert", "grass.frag");
        if (grassShader is null) return;
        grassBladeColor ??= LoadTerrainTexture("grass-blades.png", "vegetation");
        grassBladeNormal ??= LoadTerrainTexture("grass-blades-normal.png", "vegetation");
        grassShader.SetUniform("uBladeNormal", 5);
        using var bladeNormal = grassBladeNormal.Bind(5);
        grassShader.SetUniform("uShadowMaps", new[] { 9, 9, 9 });
        grassShader.SetUniform("uDynamicShadowMaps", new[] { 9, 9, 9 });
        var (range, density) = GroundCoverSettings.EffectiveBudget((int)Settings.Quality);
        int detail = GroundCoverSettings.DetailRange((int)Settings.Quality), middle = GroundCoverSettings.MiddleRange((int)Settings.Quality);
        Lighting(grassShader);
        GateLightingUniforms(grassShader);
        MeadowUniforms(grassShader);
        using var meadow = BindMeadow();
        using var pbr = BindTerrainPbr(grassShader);
        grassShader.SetUniform("uHeightRange", new Vector2(GroundCoverSettings.MinHeight, GroundCoverSettings.MaxHeight));
        grassShader.SetUniform("uRange", (float)range);
        grassShader.SetUniform("uDensity", density / 24f);
        grassShader.SetUniform("uDetailRange", (float)detail);
        grassShader.SetUniform("uMiddleRange", (float)middle);
        grassShader.SetUniform("uRecords", 1);
        grassShader.SetUniform("uGrassTerrainBlend", new Vector2(GroundCoverSettings.RootTerrainBlend, GroundCoverSettings.BodyTerrainBlend));
        grassShader.SetUniform("uShadowSurfaceBias", .01f);
        using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
        using (shadows.CurrentFrame.Bind(grassShader, 9))
            foreach (var tile in grassTiles.Values)
            {
                if (tile.Records is null || !frustum.Intersects(tile.Bounds))
                    continue;
                grassShader.SetUniform("uGrowth", Math.Clamp(((float)coverClock.Elapsed.TotalSeconds - tile.Born) / .4f, 0, 1));
                using var records = tile.Records.Bind(1);
                var center = (tile.Bounds.Min + tile.Bounds.Max) * .5f;
                float distance = Vector2.Distance(new(center.X, center.Z), new(grassEye.X, grassEye.Z));
                float rankLimit = density / 24f * GroundCoverSettings.DistanceDensity(Math.Max(0, distance - 6), range) + .025f;
                int active = Array.BinarySearch(tile.Ranks, rankLimit);
                if (active < 0)
                    active = ~active;
                if (active == 0)
                    continue;
                GrassClumps += active;
                for (int lod = 0; lod < 3; lod++)
                {
                    if (lod == 0 && distance - 6 > detail + 3 || lod == 1 && (distance + 6 < detail - 3 || distance - 6 > middle + 4) || lod == 2 && distance + 6 < middle - 4)
                        continue;
                    grassShader.SetUniform("uLod", lod);
                    pass.DrawMeshInstances(GrassTemplate(lod), active, grassBladeColor, grassShader);
                    DrawCalls++;
                    if (lod == 0)
                        GrassDetailedTiles++;
                    else if (lod == 1)
                        GrassSimpleTiles++;
                    else
                        GrassFarTiles++;
                    GrassTriangles += active * (lod == 0 ? 112 : lod == 1 ? 24 : 2);
                }
            }
    }
}
