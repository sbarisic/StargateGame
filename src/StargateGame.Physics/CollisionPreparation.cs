using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using JoltPhysicsSharp;
using StargateGame.Building;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    public sealed record CollisionInput(Vector3[] Vertices, IndexedTriangle[] Indices)
    {
        public long Bytes => (long)Vertices.Length * 12 + (long)Indices.Length * 20;
    }
    static readonly object inputGate = new();
    static readonly Dictionary<string, Task<CollisionInput>> terrainInputs = [];
    static readonly SemaphoreSlim inputWorkers = new(2);
    static readonly ConditionalWeakTable<WorldChanges, ConcurrentDictionary<ChunkCoordinate, Task<(uint Layer, CollisionInput Input)[]>>> buildingInputs = new();
    readonly Dictionary<ChunkCoordinate, (Shape Shape, string Key)> retainedTerrain = [];
    public int CollisionCacheHits
    {
        get;
        private set;
    }
    public int RetainedCollisionShapes => retainedTerrain.Count;
    public double LongestCollisionCookMilliseconds
    {
        get;
        private set;
    }
    public double CollisionInputMilliseconds
    {
        get;
        private set;
    }
    static string TerrainInputKey(TerrainChunkData chunk, string preset, WorldChanges state, string? fixture = null) => ChunkCodec.Cached(chunk).Hash + ":" + preset + ":" + fixture + ":" +
        string.Join(',', state.Removed.Where(r => r.Chunk == chunk.Coordinate).OrderBy(r => r.Category).ThenBy(r => r.Id).Select(r => r.Category + r.Id));
    public static Task<CollisionInput> PrepareCollisionInput(TerrainChunkData chunk, string preset, WorldChanges state, string? fixture = null)
    {
        string key = TerrainInputKey(chunk, preset, state, fixture);
        lock (inputGate)
        {
            if (terrainInputs.TryGetValue(key, out var cached))
                return cached;
            long bytes = terrainInputs.Values.Where(t => t.IsCompletedSuccessfully).Sum(t => t.Result.Bytes);
            foreach (var old in terrainInputs.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
            {
                if (bytes < 64 * 1024 * 1024 && terrainInputs.Count < 128)
                    break;
                if (terrainInputs.Remove(old, out var task) && task.IsCompletedSuccessfully)
                    bytes -= task.Result.Bytes;
            }
            return terrainInputs[key] = Task.Run(async () =>
            {
                await inputWorkers.WaitAsync();
                try
                {
                    var triangles = chunk.Triangles().Select(t => (t.A, t.B, t.C, GroundSurface.Unknown))
                        .Concat(RockTriangles(chunk, preset, state).Select(t => (t.A, t.B, t.C, GroundSurface.Stone)))
                        .Concat(TreeTriangles(chunk, preset, state, fixture).Select(t => (t.A, t.B, t.C, GroundSurface.Wood)));
                    return MakeInput(triangles);
                }
                finally
                {
                    inputWorkers.Release();
                }
            });
        }
    }
    static CollisionInput MakeInput(IEnumerable<(Vector3 A, Vector3 B, Vector3 C, GroundSurface Surface)> source)
    {
        var triangles = source.ToArray();
        var vertices = new Vector3[triangles.Length * 3];
        var indices = new IndexedTriangle[triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
        {
            var t = triangles[i];
            vertices[i * 3] = t.A;
            vertices[i * 3 + 1] = t.B;
            vertices[i * 3 + 2] = t.C;
            indices[i] = new(i * 3, i * 3 + 1, i * 3 + 2, 0, (uint)t.Surface);
        }
        return new(vertices, indices);
    }
    static Task<(uint Layer, CollisionInput Input)[]> BuildingInput(WorldChanges state, ChunkCoordinate owner, IEnumerable<BuildingPiece> pieces)
    {
        var captured = pieces.ToArray();
        return buildingInputs.GetValue(state, _ => new()).GetOrAdd(owner, _ => Task.Run(() =>
        {
            List<(uint Layer, CollisionInput Input)> result = [];
            foreach (uint layer in new[] { StaticLayer, StairPropLayer, StairPlayerLayer })
            {
                var triangles = captured.Where(p => !BuildingPrefabs.IsDoor(p)).Where(p => StairGeometry.IsStair(p) ? layer != StaticLayer : layer == StaticLayer)
                    .SelectMany(p =>
                    {
                        var mesh = layer == StairPlayerLayer
                            ? StairGeometry.PlayerMesh(state.Structures.Single(s=>s.Id==p.StructureId),p)
                            : BuildingGeometry.CollisionMesh(state,p);
                        var surface = p.Prefab is "kit-floor-wooddark" or "kit-stair-interior-simple" or "kit-door-1-flat" or "settlement-seahill-door" ? GroundSurface.Wood : GroundSurface.Stone;
                        return mesh.Select(t=>(t.A,t.B,t.C,
                            (p.Prefab.StartsWith("settlement-mega-house-") || p.Prefab.StartsWith("settlement-seahill-house-")) &&
                            Math.Abs(t.A.Y-state.Structures.Single(s=>s.Id==p.StructureId).Origin.Y)<.03f &&
                            Math.Abs(t.B.Y-t.A.Y)<.001f && Math.Abs(t.C.Y-t.A.Y)<.001f ? GroundSurface.Wood : surface));
                    }).ToArray();
                if (triangles.Length > 0) result.Add((layer,MakeInput(triangles)));
            }
            return result.ToArray();
        }));
    }
    Shape Cook(CollisionInput input)
    {
        var watch = Stopwatch.StartNew();
        using var settings = Native.Own(new MeshShapeSettings(input.Vertices, input.Indices) { PerTriangleUserData = true });
        settings.Sanitize();
        var shape = Own(settings.Create());
        Console.WriteLine($"COLLISION cooked location={World.Definition.LocationId} revision={(stagedChanges ?? Changes).Revision} triangles={input.Indices.Length} ms={watch.Elapsed.TotalMilliseconds:F2}");
        LongestCollisionCookMilliseconds = Math.Max(LongestCollisionCookMilliseconds, watch.Elapsed.TotalMilliseconds);
        return shape;
    }
    bool TryRestoreTerrain(ChunkCoordinate c, TerrainChunkData chunk, WorldChanges state)
    {
        if (!retainedTerrain.Remove(c, out var retained))
            return false;
        if (retained.Key == TerrainInputKey(chunk, World.Definition.Preset, state, World.Definition.Fixture))
        {
            preparedTerrain.Add(c, retained.Shape);
            CollisionCacheHits++;
            return true;
        }
        shapes.Remove(retained.Shape);
        retained.Shape.Dispose();
        return false;
    }
    void RetainTerrain(ChunkCoordinate c, Shape shape)
    {
        if (dirtyTerrain.Contains(c) || terrainSource is null || !terrainSource.TryGetChunk(c, out var chunk))
        {
            shapes.Remove(shape);
            shape.Dispose();
            return;
        }
        retainedTerrain[c] = (shape, TerrainInputKey(chunk, World.Definition.Preset, Changes, World.Definition.Fixture));
        while (retainedTerrain.Count > 32)
        {
            var first = retainedTerrain.First();
            retainedTerrain.Remove(first.Key);
            shapes.Remove(first.Value.Shape);
            first.Value.Shape.Dispose();
        }
    }
}
