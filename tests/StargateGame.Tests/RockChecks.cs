using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class RockChecks
{
    public static void Run()
    {
        var seen = new HashSet<string>();
        foreach (string preset in PlanetSettings.Presets)
        {
            var plan = new PlanetPlan(7319, preset);
            var chunks = ChunkCoordinate.At(plan.Gate.X, plan.Gate.Z).Around(3).Select(plan.BuildChunk).ToArray();
            var results = chunks.SelectMany(c => RockScenery.Resolve(c, preset)).OrderBy(r => r.Id).ToArray();
            var reversed = chunks.Reverse().Select(c => ChunkCodec.Decode(ChunkCodec.Encode(c))).SelectMany(c => RockScenery.Resolve(c, preset)).OrderBy(r => r.Id).ToArray();
            Verify.Check(results.Length > 20 && results.SequenceEqual(reversed), preset + " rocks stable across order and canonical reload");
            Verify.Check(results.Select(r => r.Id).Distinct().Count() == results.Length, preset + " rock IDs unique");
            Verify.Check(results.All(r => Vector2.Distance(new(r.Transform.M41, r.Transform.M43), new(plan.Gate.X, plan.Gate.Z)) > 28), preset + " rocks keep gate clearing clear");
            Verify.Check(results.All(r => !r.Collidable || preset switch { "temperate" => r.Model.StartsWith("Stone_") || r.Model.StartsWith("SM_rock_"), "desert" => r.Model.StartsWith("SM_Rock_"), _ => r.Model.StartsWith("Stone_") }), preset + " uses appropriate rock materials");
            foreach (var rock in results)
                seen.Add(rock.Model);
            foreach (var chunk in chunks)
            {
                var before = ChunkCodec.Hash(ChunkCodec.Encode(chunk));
                RockScenery.Resolve(chunk, preset);
                Verify.Check(before == ChunkCodec.Hash(ChunkCodec.Encode(chunk)), preset + " scenery leaves canonical terrain unchanged");
            }
            Console.WriteLine($"ROCKS {preset}: {results.Length}, collidable {results.Count(r => r.Collidable)}");
        }
        Verify.Check(RockScenery.Models.All(seen.Contains), "all 13 recovered rocks exercised");
        var coordinate = new ChunkCoordinate(0, 0);
        TerrainChunkData Flat(ulong id, int[]? water = null) => new(coordinate, new int[TerrainChunkData.Count], water ?? Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray(), new byte[TerrainChunkData.Count * 4], [new(id, "rock", new(32, 0, 32), .7f, 1)]);
        var flat = Enumerable.Range(0, 2000).Select(i => Flat((1UL << 20) + (ulong)i)).First(c => RockScenery.Resolve(c, "temperate").Any(r => r.Model == "Stone_01" && r.Collidable));
        var chosen = RockScenery.Resolve(flat, "temperate").Single();
        var wet = Enumerable.Repeat(100, TerrainChunkData.Count).ToArray();
        Verify.Check(RockScenery.Resolve(Flat(chosen.Id, wet), "temperate").Count == 0, "rock footprint excludes water");
        var definition = new PlanetPlan(7319, "temperate").Definition with
        {
            Entities = [],
            Props = []
        };
        var store = new TerrainStore(definition.SettingsHash);
        store.Put(flat, false);
        using var world = new PhysicsWorld(definition, true, store, [coordinate]);
        world.AddPlayer(new(1, "Rock support", new(32, chosen.Max.Y + .4f, 32), default, 0, 0, false, 0));
        for (int i = 0; i < 180; i++)
            world.Step();
        Verify.Check(world.Player(1).Grounded && world.Player(1).Position.Y > .6f, "character stands on imported rock collision");
        using var saved = world.Save();
        for (int i = 0; i < 10; i++)
            world.Step();
        var expected = world.Snapshot();
        world.Restore(saved);
        for (int i = 0; i < 10; i++)
            world.Step();
        Verify.Check(world.Snapshot().Players.SequenceEqual(expected.Players), "rock contact checkpoint replay");
        world.SetTerrain([]);
        bool invalid = false;
        try
        {
            world.Restore(saved);
        }
        catch (InvalidOperationException)
        {
            invalid = true;
        }
        Verify.Check(invalid, "rock chunk unload invalidates old prediction checkpoint");
    }
}
