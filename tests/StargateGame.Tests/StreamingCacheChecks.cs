using System.Numerics;
using StargateGame.Client;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;
static class StreamingCacheChecks
{
    public static async Task Run()
    {
        using var generation = new GenerationService(7319, "temperate");
        var plan = await generation.Plan;
        var chunks = await Task.WhenAll(ChunkCoordinate.All.Take(40).Select(c => generation.RequestAsync(c)));
        var chunk = chunks[0];
        var encoded = ChunkCodec.Cached(chunk);
        Verify.Check(ReferenceEquals(encoded, ChunkCodec.Cached(chunk)) && ReferenceEquals(encoded.Bytes, ChunkCodec.Encode(chunk)), "immutable canonical encoding/hash reused");
        foreach (var c in chunks.Take(3))
        {
            var tile = FarTile.Build(c, "temperate");
            var bytes = tile.Encode();
            var restored = FarTile.Decode(bytes, c.Coordinate);
            Console.WriteLine($"FAR {c.Coordinate} bytes={bytes.Length} vertices={tile.Mesh.Positions.Length} water={tile.Mesh.WaterPositions.Length} trees={tile.Trees.Length}");
            Verify.Check(restored.Mesh.Positions.SequenceEqual(tile.Mesh.Positions) && restored.Mesh.WaterPositions.SequenceEqual(tile.Mesh.WaterPositions) && restored.Trees.SequenceEqual(tile.Trees), "far tile preserves coarse terrain, canonical shoreline and tree silhouettes " + c.Coordinate);
            Verify.Check(restored.Mesh.Materials.SequenceEqual(tile.Mesh.Materials) && restored.Mesh.WaterFlow.SequenceEqual(tile.Mesh.WaterFlow) && restored.Mesh.Normals.Zip(tile.Mesh.Normals).All(p => Vector3.Distance(p.First, p.Second) < .00003f), "far material/flow exact and normal error below 0.00003");
            Verify.Check(bytes.Length < ChunkCodec.Encode(c).Length, "far tile smaller than canonical samples for fixture " + c.Coordinate);
            bool rejected = false;
            try
            {
                FarTile.Decode(bytes, new(7, 7));
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Verify.Check(rejected, "far tile rejects incorrect identity");
        }
        using var physics = new PhysicsWorld(plan.Definition, true, generation.Store, [chunk.Coordinate]);
        physics.SetTerrain([]);
        Verify.Check(physics.RetainedCollisionShapes == 1, "removed native shape retained");
        physics.SetTerrain([chunk.Coordinate]);
        Verify.Check(physics.CollisionCacheHits == 1, "return visit reuses native shape");
        foreach (var c in chunks)
        {
            physics.SetTerrain([c.Coordinate]);
        }
        physics.SetTerrain([]);
        Verify.Check(physics.RetainedCollisionShapes == 32, "native shape cache bounded to 32 entries");
        var keep = chunks.Take(2).Select(c => c.Coordinate).ToHashSet();
        generation.Store.Retain(keep);
        Verify.Check(generation.Store.Count == 2 && keep.All(c => generation.Store.TryGetChunk(c, out _)), "canonical eviction retains only required dependencies");
        Verify.Check(Protocol.Version == 17 && StreamingSettings.PreloadCpuLimit == 128L * 1024 * 1024 && StreamingSettings.PreloadGpuLimit == 64L * 1024 * 1024, "protocol and independent preload budgets");
        Console.WriteLine($"CACHE cook_max_ms={physics.LongestCollisionCookMilliseconds:F3} hits={physics.CollisionCacheHits}");
    }
}
