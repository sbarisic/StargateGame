using System.Diagnostics;
using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    DestinationCache? warmCache;
    Guid promotedCache;
    sealed class WarmUpload(ChunkCoordinate coordinate, TerrainMeshData data, long bytes)
    {
        public ChunkCoordinate Coordinate = coordinate;
        public TerrainMeshData Data = data;
        public long Bytes = bytes;
        public Mesh3D? Ground, Water;
        public Vector3[] Positions = [], Normals = [], WaterPositions = [];
        public Vector2[] Flow = [];
        public uint[] Elements = [], WaterElements = [];
        public Color[] Colors = [];
        public Vector2[] UVs = [];
        public Vector3[] WaterNormals = [];
        public IReadOnlyList<RockInstance> Rocks = [];
        public IReadOnlyList<TreeInstance> Trees = [];
        public AxisAlignedBoundingBox Bounds;
        public int Stage;
        public void Dispose()
        {
            Ground?.Dispose();
            Water?.Dispose();
        }
    }
    WarmUpload? warmUpload;
    Task<WarmUpload>? warmPreparation;
    DestinationCache? warmPreparationOwner;
    long warmPreparationBytes;
    void ReleaseWarmPreparation()
    {
        if (warmPreparationOwner is { } owner)
            owner.Bytes -= warmPreparationBytes;
        warmPreparationOwner = null;
        warmPreparationBytes = 0;
        if (warmPreparation is { } task)
            _ = task.ContinueWith(t =>
            {
                _ = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        warmPreparation = null;
    }
    double uploadStepEstimate = .04;

    readonly Dictionary<ChunkCoordinate, Resident> warmResidents = [];
    public long PreloadGpuBytes
    {
        get;
        private set;
    }
    public double PreloadUploadMilliseconds
    {
        get;
        private set;
    }
    public double PreloadUploadPeakMilliseconds
    {
        get;
        private set;
    }
    public long PreloadPeakGpuBytes
    {
        get;
        private set;
    }
    public int PreloadMeshes => warmResidents.Count;
    void ClearDestinationResources()
    {
        ClearWarmPalette();
        ReleaseWarmPreparation();
        warmUpload?.Dispose();
        warmUpload = null;
        foreach (var r in warmResidents.Values)
            r.Dispose();
        warmResidents.Clear();
        warmCache = null;
        PreloadGpuBytes = 0;
    }
    void SupplyDestination(ClientSession session)
    {
        if (warmCache is { } old && old.Begin.LocationId == World.Definition.LocationId)
        {
            if (old.Begin.ContentRevision == session.Changes.Revision)
                foreach (var (c, r) in warmResidents)
                    ReplaceResident(c, r);
            else
                foreach (var r in warmResidents.Values)
                    r.Dispose();
            ReleaseWarmPreparation();
            warmUpload?.Dispose();
            warmUpload = null;
            warmResidents.Clear();
            warmCache = null;
            PreloadGpuBytes = 0;
        }
        if (session.PromotedDestination is { } promoted && promoted.Begin.PreloadId != promotedCache && promoted.Begin.LocationId == World.Definition.LocationId)
        {
            promotedCache = promoted.Begin.PreloadId;
            foreach (var (c, mesh) in promoted.Meshes)
                if (!resident.ContainsKey(c) && !pendingMeshes.ContainsKey(c))
                    pendingMeshes[c] = (1, Task.FromResult(mesh));
            session.ReleasePromotedDestination();
        }
        var cache = session.Destination;
        if (cache?.Begin.LocationId == World.Definition.LocationId)
            cache = null;
        if (!ReferenceEquals(warmCache, cache))
        {
            ClearDestinationResources();
            warmCache = cache;
        }
    }
    void WarmDestination(double budgetMs)
    {
        PreloadUploadMilliseconds = 0;
        var paletteClock = Stopwatch.StartNew();
        if (warmCache is { } destination)
            WarmTerrainPalette(destination.Begin.Planet!.Preset, Math.Min(.25, budgetMs * .5));
        double paletteMilliseconds = paletteClock.Elapsed.TotalMilliseconds;
        budgetMs = Math.Max(0, budgetMs - paletteMilliseconds);
        PreloadUploadMilliseconds = paletteMilliseconds;
        PreloadUploadPeakMilliseconds = Math.Max(PreloadUploadPeakMilliseconds, PreloadUploadMilliseconds);
        if (budgetMs <= 0 || warmCache is not { Stage: PreloadStage.Ready } cache)
            return;
        var watch = Stopwatch.StartNew();
        // Upload one vertex attribute at a time. Calls are indivisible; do not admit
        // another call unless its measured cost fits the remaining shared budget.
        while (watch.Elapsed.TotalMilliseconds + uploadStepEstimate < budgetMs)
        {
            if (warmUpload is null)
            {
                if (warmPreparation is null)
                {
                    var next = cache.Meshes.FirstOrDefault(p => !warmResidents.ContainsKey(p.Key));
                    if (next.Value is null)
                        break;
                    var mesh = next.Value;
                    long bytes = (long)(mesh.Positions.Length + mesh.WaterPositions.Length) * 48;
                    long cpu = 4L * 1024 * 1024 + (long)(mesh.Positions.Length + mesh.WaterPositions.Length) * 24;
                    if (PreloadGpuBytes + bytes > StreamingSettings.PreloadGpuLimit || cache.Bytes + cpu > StreamingSettings.PreloadCpuLimit)
                        break;
                    warmPreparationOwner = cache;
                    warmPreparationBytes = cpu;
                    cache.Bytes += cpu;
                    warmPreparation = Task.Run(() =>
                    {
                        cache.Store.TryGetChunk(next.Key, out var canonical);
                        var unique = new Dictionary<(Vector3 P, Vector3 N, Vector4 M), uint>();
                        var elements = new uint[mesh.Positions.Length];
                        for (int i = 0; i < elements.Length; i++)
                        {
                            var v = (mesh.Positions[i], mesh.Normals[i], mesh.Materials[i]);
                            if (!unique.TryGetValue(v, out uint index))
                            {
                                index = (uint)unique.Count;
                                unique.Add(v, index);
                            }
                            elements[i] = index;
                        }
                        var water = new Dictionary<(Vector3 P, Vector2 F, Vector3 D), uint>();
                        var waterElements = new uint[mesh.WaterPositions.Length];
                        var waterAttributes = TerrainMesher.WaterAttributes(mesh);
                        for (int i = 0; i < waterElements.Length; i++)
                        {
                            var v = (mesh.WaterPositions[i], mesh.WaterFlow[i], waterAttributes[i]);
                            if (!water.TryGetValue(v, out uint index))
                            {
                                index = (uint)water.Count;
                                water.Add(v, index);
                            }
                            waterElements[i] = index;
                        }
                        var result = new WarmUpload(next.Key, mesh, bytes)
                        {
                            Positions = unique.Keys.Select(v => v.P).ToArray(),
                            Normals = unique.Keys.Select(v => v.N).ToArray(),
                            Elements = elements,
                            Colors = unique.Keys.Select(v => new Color((byte)(v.M.X * 255), (byte)(v.M.Y * 255), (byte)(v.M.Z * 255), (byte)(v.M.W * 255))).ToArray(),
                            UVs = new Vector2[unique.Count],
                            WaterNormals = water.Keys.Select(v => v.D).ToArray(),
                            WaterPositions = water.Keys.Select(v => v.P).ToArray(),
                            Flow = water.Keys.Select(v => v.F).ToArray(),
                            WaterElements = waterElements,
                            Rocks = RockScenery.Resolve(canonical!, cache.Begin.Planet!.Preset),
                            Trees = TreeScenery.Resolve(canonical!, cache.Begin.Planet.Preset, cache.Begin.Planet.Fixture),
                            Bounds = AxisAlignedBoundingBox.FromPoints(mesh.Positions.Concat(mesh.WaterPositions))
                        };
                        foreach (var rock in result.Rocks)
                            result.Bounds = result.Bounds.Include(rock.Min).Include(rock.Max);
                        foreach (var tree in result.Trees)
                            result.Bounds = result.Bounds.Include(tree.Min).Include(tree.Max);
                        return result;
                    });
                    break;
                }
                if (!warmPreparation.IsCompleted)
                    break;
                if (!warmPreparation.IsCompletedSuccessfully)
                {
                    ReleaseWarmPreparation();
                    break;
                }
                warmUpload = warmPreparation.Result;
                PreloadGpuBytes += warmUpload.Bytes;
            }
            var u = warmUpload;
            var data = u.Data;
            var step = Stopwatch.StartNew();
            switch (u.Stage++)
            {
                case 0:
                    u.Ground = graphics.CreateMesh3D();
                    break;
                case 1:
                    u.Ground!.SetVertices(u.Positions);
                    break;
                case 2:
                    u.Ground!.SetNormals(u.Normals);
                    break;
                case 3:
                    u.Ground!.SetColors(u.Colors);
                    break;
                case 4:
                    u.Ground!.SetUVs(u.UVs);
                    break;
                case 5:
                    u.Ground!.SetElements(u.Elements);
                    if (u.WaterPositions.Length == 0)
                        u.Stage = 11;
                    break;
                case 6:
                    u.Water = graphics.CreateMesh3D();
                    break;
                case 7:
                    u.Water!.SetVertices(u.WaterPositions);
                    break;
                case 8:
                    u.Water!.SetNormals(u.WaterNormals);
                    break;
                case 9:
                    u.Water!.SetUVs(u.Flow);
                    break;
                case 10:
                    u.Water!.SetElements(u.WaterElements);
                    break;
                default:
                    warmResidents.Add(u.Coordinate, new(1, u.Ground!, u.Water, data.Positions.Length + data.WaterPositions.Length, u.Bounds, u.Rocks, u.Trees));
                    ReleaseWarmPreparation();
                    warmUpload = null;
                    break;
            }
            uploadStepEstimate = Math.Clamp(step.Elapsed.TotalMilliseconds * 1.2, .02, .45);
            break; // One indivisible graphics call per frame leaves headroom for the driver.
        }
        PreloadUploadMilliseconds = paletteMilliseconds + watch.Elapsed.TotalMilliseconds;
        PreloadUploadPeakMilliseconds = Math.Max(PreloadUploadPeakMilliseconds, PreloadUploadMilliseconds);
        PreloadPeakGpuBytes = Math.Max(PreloadPeakGpuBytes, PreloadGpuBytes);
    }
}
