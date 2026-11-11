using System.Collections.Concurrent;
using System.Diagnostics;
using Runevision.Common;
using Runevision.LayerProcGen;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

/// <summary>Isolates planet contexts within the pinned framework's process-wide manager.</summary>
public sealed class GenerationService : IDisposable
{
    static readonly object Gate = new();
    static LayerManager? manager;
    internal static readonly ConcurrentDictionary<int, GenerationService> Contexts = new();
    static int nextSlot;
    readonly int slot;
    readonly TopLayerDependency region;
    readonly ConcurrentDictionary<ChunkCoordinate, TaskCompletionSource<TerrainChunkData>> requests = new();
    readonly List<TopLayerDependency> dependencies = [];
    readonly TaskCompletionSource<PlanetPlan> planReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly ConcurrentDictionary<(ChunkCoordinate, int), TaskCompletionSource<TerrainMeshData>> meshes = new();
    readonly Dictionary<(ChunkCoordinate, int), TopLayerDependency> meshDependencies = [];
    readonly bool clientData;
    readonly SettlementSettings settlementSettings;
    readonly PlanetGenerationSettings generationSettings;
    readonly CancellationTokenSource lifetime = new();
    readonly string regionalCache;
    readonly Action<string>? progress;
    public CancellationToken Cancellation => lifetime.Token;
    public string Phase { get; private set; } = "Landforms";
    readonly Stopwatch time = Stopwatch.StartNew();
    bool disposed;
    public int Seed
    {
        get;
    }
    public int Version
    {
        get;
    }
    public string Preset
    {
        get;
    }
    public TerrainStore Store
    {
        get;
    }
    public Task<PlanetPlan> Plan => planReady.Task;
    long meshTicks, chunkTicks;
    public double MeshMilliseconds => Interlocked.Read(ref meshTicks) * 1000d / Stopwatch.Frequency;
    public double ChunkGenerationMilliseconds => Interlocked.Read(ref chunkTicks) * 1000d / Stopwatch.Frequency;
    public double GenerationMilliseconds
    {
        get;
        private set;
    }
    public static int ActiveContexts => Contexts.Count;
    public int Pending => requests.Count(p => !p.Value.Task.IsCompleted);
    public GenerationService(int seed, string preset, string? cacheRoot = null, TerrainStore? canonical = null, bool parallel = true, int version = PlanetSettings.Version, SettlementSettings? settlements = null, PlanetGenerationSettings? generation = null, Action<string>? progress = null)
    {
        if (!PlanetSettings.Presets.Contains(preset))
            throw new ArgumentException("Unknown preset.");
        settlementSettings = settlements ?? new();
        settlementSettings.Validate();
        generationSettings = generation ?? new();
        generationSettings.Validate();
        this.progress = progress;
        regionalCache = Path.Combine(cacheRoot ?? Environment.GetEnvironmentVariable("STARGATEGAME_TERRAIN_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame", "terrain-cache"), "regional");
        Version = version;
        Seed = seed;
        Preset = preset;
        Store = canonical ?? new(PlanetSettings.Hash(seed, preset, version, settlementSettings, generationSettings), cacheRoot);
        clientData = canonical is not null;
        lock (Gate)
        {
            manager ??= new LayerManager(parallel);
            DebugToggle.Create(">Logging/Generation Stats").SetEnabled(false);
            slot = nextSlot++;
            Contexts[slot] = this;
            region = new(RegionLayer.instance, new Point(1, 1));
            region.SetFocus(new Point(slot * WorldDomain.ContextStride + WorldDomain.HalfSize, WorldDomain.HalfSize));
        }
    }
    public async Task<TerrainChunkData> RequestAsync(ChunkCoordinate coordinate, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!coordinate.Valid)
            throw new ArgumentOutOfRangeException(nameof(coordinate));
        if (Store.TryLoad(coordinate, out var cached))
            return cached;
        TaskCompletionSource<TerrainChunkData> source;
        lock (Gate)
        {
            if (!requests.TryGetValue(coordinate, out source!))
            {
                source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                requests[coordinate] = source;
                var dep = new TopLayerDependency(TerrainLayer.instance, new Point(1, 1));
                dependencies.Add(dep);
                dep.SetFocus(new Point(slot * WorldDomain.ContextStride + (coordinate.X + WorldDomain.HalfChunks) * 64 + 32, (coordinate.Z + WorldDomain.HalfChunks) * 64 + 32));
            }
        }
        return await source.Task.WaitAsync(cancellation);
    }
    internal void MakePlan()
    {
        if (clientData)
            return;
        try
        {
            planReady.TrySetResult(new PlanetPlan(Seed, Preset, Version, settlementSettings, generationSettings, lifetime.Token, phase => { Phase = phase; progress?.Invoke(phase); }, regionalCache));
            GenerationMilliseconds = time.Elapsed.TotalMilliseconds;
        }
        catch (Exception e)
        {
            planReady.TrySetException(e);
        }
    }
    internal void MakeChunk(ChunkCoordinate c)
    {
        if (clientData)
            return;
        if (!requests.TryGetValue(c, out var result))
            return;
        try
        {
            var watch = Stopwatch.StartNew();
            var data = planReady.Task.GetAwaiter().GetResult().BuildChunk(c);
            Interlocked.Add(ref chunkTicks, watch.ElapsedTicks);
            Store.Put(data);
            result.TrySetResult(data);
        }
        catch (Exception e)
        {
            result.TrySetException(e);
        }
    }
    public Task<TerrainMeshData> RequestMeshAsync(ChunkCoordinate c, int step)
    {
        if (step is not (1 or 2 or 4 or 8) || !c.Valid || !Store.TryGetChunk(c, out _))
            throw new ArgumentException("Canonical mesh data is not ready.");
        lock (Gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var key = (c, step);
            if (!meshes.TryGetValue(key, out var result))
            {
                result = new(TaskCreationOptions.RunContinuationsAsynchronously);
                meshes[key] = result;
            }
            if (!meshDependencies.ContainsKey(key))
            {
                AbstractChunkBasedDataLayer layer = step switch
                {
                    1 => Mesh1.instance,
                    2 => Mesh2.instance,
                    4 => Mesh4.instance,
                    _ => Mesh8.instance
                };
                var dep = new TopLayerDependency(layer, new Point(1, 1));
                meshDependencies[key] = dep;
                dep.SetFocus(new Point(slot * WorldDomain.ContextStride + (c.X + WorldDomain.HalfChunks) * 64 + 32, (c.Z + WorldDomain.HalfChunks) * 64 + 32));
            }
            return result.Task;
        }
    }
    public void ReleaseMesh(ChunkCoordinate c, int step)
    {
        lock (Gate)
        {
            if (meshDependencies.Remove((c, step), out var dep))
                dep.isActive = false;
        }
    }
    internal void MakeMesh(ChunkCoordinate c, int step, bool destroy)
    {
        if (destroy)
        {
            meshes.TryRemove((c, step), out _);
            return;
        }
        var source = meshes.GetOrAdd((c, step), _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            if (!Store.TryGetChunk(c, out var data))
                throw new InvalidOperationException("Missing canonical mesh data.");
            var watch = Stopwatch.StartNew();
            source.TrySetResult(TerrainMesher.Build(data, step));
            Interlocked.Add(ref meshTicks, watch.ElapsedTicks);
        }
        catch (Exception e)
        {
            source.TrySetException(e);
        }
    }
    public void ReleaseExcept(IReadOnlySet<ChunkCoordinate> retained)
    {
        // Framework dependencies are the lifetime owners; canonical data remains
        // retained by collision interest plus active requests, independently of world area.
        lock (Gate)
        {
            foreach (var dep in dependencies.ToArray())
            {
                var c = new ChunkCoordinate((dep.focus.x - slot * WorldDomain.ContextStride) / 64 - WorldDomain.HalfChunks, dep.focus.y / 64 - WorldDomain.HalfChunks);
                if (!retained.Contains(c) && requests.TryGetValue(c, out var task) && task.Task.IsCompleted)
                {
                    dep.isActive = false;
                    dependencies.Remove(dep);
                    requests.TryRemove(c, out _);
                }
            }
        }
        Store.Retain(retained.Concat(requests.Where(p=>!p.Value.Task.IsCompleted).Select(p=>p.Key)).ToHashSet());
    }
    public void Dispose()
    {
        lifetime.Cancel();
        lock (Gate)
        {
            if (disposed)
                return;
            disposed = true;
            region.isActive = false;
            foreach (var d in dependencies)
                d.isActive = false;
            foreach (var d in meshDependencies.Values)
                d.isActive = false;
            foreach (var m in meshes.Values)
                m.TrySetCanceled();
            planReady.TrySetCanceled();
            foreach (var r in requests.Values)
                r.TrySetCanceled();
            Contexts.TryRemove(slot, out _);
            if (Contexts.IsEmpty)
            {
                manager!.Unload();
                manager = null;
                nextSlot = 0;
            }
        }
    }
    public sealed class RegionLayer : ChunkBasedDataLayer<RegionLayer, RegionChunk>
    {
        public override int chunkW => WorldDomain.Size;
        public override int chunkH => WorldDomain.Size;
    }
    public sealed class RegionChunk : LayerChunk<RegionLayer, RegionChunk>
    {
        public override void Create(int level, bool destroy)
        {
            if (!destroy && Contexts.TryGetValue(index.x / 2, out var ctx))
                ctx.MakePlan();
        }
    }
    public sealed class TerrainLayer : ChunkBasedDataLayer<TerrainLayer, TerrainChunk>
    {
        public override int chunkW => 64;
        public override int chunkH => 64;
        public TerrainLayer() : base(32, 32, 64)
        {
            AddLayerDependency(new LayerDependency(RegionLayer.instance, 0));
        }
    }
    public sealed class TerrainChunk : LayerChunk<TerrainLayer, TerrainChunk>
    {
        public override void Create(int level, bool destroy)
        {
            if (destroy)
                return;
            int slot = index.x / (WorldDomain.ChunkCount * 2);
            if (Contexts.TryGetValue(slot, out var ctx))
                ctx.MakeChunk(new(index.x - slot * (WorldDomain.ChunkCount * 2) - WorldDomain.HalfChunks, index.y - WorldDomain.HalfChunks));
        }
    }
    public sealed class Mesh1 : ChunkBasedDataLayer<Mesh1, MeshChunk1>
    {
        public override int chunkW => 64;
        public override int chunkH => 64;
        public Mesh1() : base(32, 32, 64)
        {
            AddLayerDependency(new LayerDependency(TerrainLayer.instance, 0));
        }
    }
    public sealed class MeshChunk1 : LayerChunk<Mesh1, MeshChunk1>
    {
        public override void Create(int level, bool destroy)
        {
            int slot = index.x / (WorldDomain.ChunkCount * 2);
            if (Contexts.TryGetValue(slot, out var ctx))
                ctx.MakeMesh(new(index.x - slot * (WorldDomain.ChunkCount * 2) - WorldDomain.HalfChunks, index.y - WorldDomain.HalfChunks), 1, destroy);
        }
    }
    public sealed class Mesh2 : ChunkBasedDataLayer<Mesh2, MeshChunk2>
    {
        public override int chunkW => 64;
        public override int chunkH => 64;
        public Mesh2() : base(32, 32, 64)
        {
            AddLayerDependency(new LayerDependency(TerrainLayer.instance, 0));
        }
    }
    public sealed class MeshChunk2 : LayerChunk<Mesh2, MeshChunk2>
    {
        public override void Create(int level, bool destroy)
        {
            int slot = index.x / (WorldDomain.ChunkCount * 2);
            if (Contexts.TryGetValue(slot, out var ctx))
                ctx.MakeMesh(new(index.x - slot * (WorldDomain.ChunkCount * 2) - WorldDomain.HalfChunks, index.y - WorldDomain.HalfChunks), 2, destroy);
        }
    }
    public sealed class Mesh4 : ChunkBasedDataLayer<Mesh4, MeshChunk4>
    {
        public override int chunkW => 64;
        public override int chunkH => 64;
        public Mesh4() : base(32, 32, 64)
        {
            AddLayerDependency(new LayerDependency(TerrainLayer.instance, 0));
        }
    }
    public sealed class MeshChunk4 : LayerChunk<Mesh4, MeshChunk4>
    {
        public override void Create(int level, bool destroy)
        {
            int slot = index.x / (WorldDomain.ChunkCount * 2);
            if (Contexts.TryGetValue(slot, out var ctx))
                ctx.MakeMesh(new(index.x - slot * (WorldDomain.ChunkCount * 2) - WorldDomain.HalfChunks, index.y - WorldDomain.HalfChunks), 4, destroy);
        }
    }
    public sealed class Mesh8 : ChunkBasedDataLayer<Mesh8, MeshChunk8>
    {
        public override int chunkW => 64;
        public override int chunkH => 64;
        public Mesh8() : base(32, 32, 64)
        {
            AddLayerDependency(new LayerDependency(TerrainLayer.instance, 0));
        }
    }
    public sealed class MeshChunk8 : LayerChunk<Mesh8, MeshChunk8>
    {
        public override void Create(int level, bool destroy)
        {
            int slot = index.x / (WorldDomain.ChunkCount * 2);
            if (Contexts.TryGetValue(slot, out var ctx))
                ctx.MakeMesh(new(index.x - slot * (WorldDomain.ChunkCount * 2) - WorldDomain.HalfChunks, index.y - WorldDomain.HalfChunks), 8, destroy);
        }
    }
}
