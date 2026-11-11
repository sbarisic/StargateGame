using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class BuriedPropChecks
{
    public static void Run()
    {
        var fixture = GraphicsFixture.Create("graphics-lab") with
        {
            Props = []
        };
        var terrain = new TerrainStore(fixture.SettingsHash);
        foreach (var c in GraphicsFixture.Chunks)
            terrain.Put(GraphicsFixture.BuildChunk(fixture, c), false);
        var prop = PropDefinition.Barrel(61, new(30, 2, 30), false) with
        {
            PersistentId = Guid.NewGuid()
        };
        var fallen = new BodyState(61, new(30, -8699, 30), Quaternion.Identity, new(0, -315, 0), Vector3.One, false);
        using (var world = new PhysicsWorld(fixture, true, terrain, GraphicsFixture.Chunks))
        {
            world.InsertProp(prop, fallen);
            world.Step();
            var recovered = world.Bodies().Single();
            Verify.Check(recovered.Position.Y > world.World.SampleHeight(30, 30), "deeply buried barrel recovers above loaded terrain");
            Verify.Check(recovered.LinearVelocity.Length() < 1 && recovered.AngularVelocity.Length() < 1, "recovery clears falling momentum");
            Verify.Check(world.World.Definition.Props.Single().PersistentId == prop.PersistentId, "recovery retains prop identity");
            for (int i = 0; i < 180; i++)
                world.Step();
            Verify.Check(world.Bodies().Single().Position.Y >= world.World.SampleHeight(30, 30), "recovered barrel lands without falling through");
            PhysicsWorld.Validate(world.Snapshot(), world.World.Definition);
        }
        using (var unloaded = new PhysicsWorld(fixture, true, terrain, []))
        {
            unloaded.InsertProp(prop, fallen);
            unloaded.Step();
            Verify.Check(unloaded.Bodies().Single() == fallen, "unloaded terrain suspends prop instead of guessing a recovery height");
        }
        using (var isolated = new PhysicsWorld(fixture, false))
        {
            isolated.InsertProp(prop, fallen);
            isolated.Step();
            Verify.Check(isolated.Bodies().Single().Position.Y < fallen.Position.Y, "physics-only worlds do not invent terrain for recovery");
        }
    }
    public static async Task SavedWorld(string directory, string universe)
    {
        await using var server = new StargateGame.Server.GameServer(IPAddress.Loopback, 0, 8, 7319, "temperate", true,
            directory, universe, Path.GetFullPath("artifacts/galaxy/galaxy.bin"), allowTestIdentities: true);
        server.Start();
        using var identity = new PlayerIdentity();
        using var connection = await GameConnection.ConnectAsync("127.0.0.1", server.Port, "RecoveryCheck", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity);
        using var client = new StargateGame.Client.ClientSession(connection);
        var watch = Stopwatch.StartNew();
        double next = 0;
        while (watch.Elapsed.TotalSeconds < 10)
        {
            client.Poll();
            if (client.Failure is { } failure)
                throw new Exception(failure);
            if (watch.Elapsed.TotalSeconds >= next)
            {
                next += 1d / 60;
                client.Step(0, 0, (float)(watch.Elapsed.TotalSeconds * .5) % MathF.Tau, 0, false, false);
            }
            await Task.Delay(4);
        }
        await server.InspectPhysicsAsync(client.World.Definition.LocationId, w =>
        {
            PhysicsWorld.Validate(w.Snapshot(), w.World.Definition);
            var barrel = w.Bodies().Single(b => b.Id == 5);
            Verify.Check(barrel.Position.Y >= w.World.SampleHeight(barrel.Position.X, barrel.Position.Z) - .1f, "saved fallen barrel remains above real terrain after ten seconds of turning");
            Console.WriteLine($"Recovered save barrel: {barrel}");
        });
        Verify.Check(client.Failure is null, "copied save stays connected while looking around");
    }
}
