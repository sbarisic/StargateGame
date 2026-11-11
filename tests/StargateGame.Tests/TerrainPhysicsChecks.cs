using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class TerrainPhysicsChecks
{
    public static void Run()
    {
        var plan = new PlanetPlan(7319, "temperate");
        var store = new TerrainStore(plan.Definition.SettingsHash);
        var centre = ChunkCoordinate.At(plan.Gate.X, plan.Gate.Z);
        var chunks = centre.Around(2).ToArray();
        foreach (var c in chunks)
            store.Put(plan.BuildChunk(c), false);
        using var world = new PhysicsWorld(plan.Definition, true, store, centre.Around(1));
        world.AddPlayer(new(1, "Topology", plan.Definition.Spawns[0], default, 0, 0, true, 0));
        for (int i = 0; i < 120; i++)
            world.Step();
        Verify.Check(world.Player(1).Grounded, "streamed terrain supports characters");
        Verify.Check(world.Bodies().All(b => float.IsFinite(b.Position.Y) && b.Position.Y > plan.Gate.Y - .1f), "streamed terrain supports crates");
        world.Step(new Dictionary<int, PlayerInput> { { 1, new(1, 0, 0, 0, 0, false, true) } });
        float jumpHeight = world.Player(1).Position.Y;
        using var old = world.Save();
        while (!world.PrepareTerrainBatch(chunks))
        {
        }
        world.SetTerrain(chunks);
        world.Epoch++;
        bool invalid = false;
        try
        {
            world.Restore(old);
        }
        catch (InvalidOperationException)
        {
            invalid = true;
        }
        Verify.Check(invalid, "terrain topology invalidates native checkpoints");
        world.Step();
        Verify.Check(world.Player(1).Position.Y > jumpHeight, "collider activation preserves jumping velocity");
        for (int i = 0; i < 120; i++)
            world.Step();
        Verify.Check(world.Player(1).Grounded, "character lands after collision topology change");
        using var checkpoint = world.Save();
        for (int i = 0; i < 10; i++)
            world.Step();
        var expected = world.Snapshot();
        world.Restore(checkpoint);
        for (int i = 0; i < 10; i++)
            world.Step();
        Verify.Check(world.Snapshot().Players.SequenceEqual(expected.Players) && world.Bodies().SequenceEqual(expected.Bodies), "generated topology checkpoint replay");
        Verify.Check(!world.HasTerrain(new(511, plan.Gate.Y, 511)), "unsupported streaming frontier is detected");
        Verify.Check(ChunkCoordinate.At(-.01f, -64.01f) == new ChunkCoordinate(-1, -2), "negative world coordinates use floor chunks");
        var gate = plan.Definition.Entities.Single(e => e.Model == "stargate").Position;
        world.RemovePlayer(1);
        world.AddPlayer(new(1, "Gate clearance", new(gate.X, plan.Gate.Y, gate.Z + 3), default, 0, 0, true, 0));
        for (int i = 0; i < 120; i++)
            world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 1, 0, 1, 0, 0, false, false) } });
        Verify.Check(world.Player(1).Position.Z < gate.Z - 2 && world.Player(1).Grounded, "embedded gate opening remains walkable without jumping");
    }
}
