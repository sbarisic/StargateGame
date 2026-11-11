using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Persistence;

static class EmergenceChecks
{
    public static async Task Run()
    {
        var gate = new GateFrame(new(0, 4, 0), Quaternion.Identity, 3);
        foreach (bool barrel in new[] { false, true })
            foreach (float yaw in new[] { 0f, 1.2f })
            {
                var receiver = gate with
                {
                    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)
                };
                var prop = barrel ? PropDefinition.Barrel(1, default, false) : new PropDefinition(1, default, new(.5f), 10);
                var captured = new BodyState(1, gate.World(new(.2f, 0, -1)), Quaternion.CreateFromYawPitchRoll(.3f, .1f, .2f), new(.2f, 0, -2), new(.2f, .1f, .3f), false);
                var arrival = GateTravel.Exit(gate, receiver, captured, prop);
                Verify.Near(GateTravel.Extents(receiver, arrival, prop).Max, .02f, .0001f, "only leading 2cm starts exposed");
                Verify.Near(Vector3.Distance(arrival.LinearVelocity, Vector3.Transform(captured.LinearVelocity, gate.MappingTo(receiver))), 0, .0001f, "no added exit impulse");
                using var world = new PhysicsWorld(PlanetDefinition.Default with
                {
                    Entities = [],
                    Props = []
                }, false);
                // A rear wall must not veto or collide with the hidden hull.
                world.AddStaticBox(new(0, 4, -1.2f), new(10, 10, .2f));
                Verify.Check(world.CanBeginPropEmergence(receiver, prop, arrival), "rear geometry permits exposed-only admission");
                world.InsertProp(prop, arrival);
                var connection = Guid.NewGuid();
                world.BeginPropEmergence(1, receiver, connection);
                Verify.Near(Vector3.Distance(world.Bodies().Single().LinearVelocity, arrival.LinearVelocity), 0, .0001f, "clipped insertion preserves momentum");
                var snapshot = JsonSerializer.Deserialize<PhysicsSnapshot>(JsonSerializer.Serialize(world.Snapshot(), Protocol.Json), Protocol.Json)!;
                Verify.Check(snapshot.Clips!.Single().Mode == PropClipMode.Emergence && snapshot.Clips![0].Connection == connection, "snapshot retains receiving mode and connection");
                using (var checkpoint = world.Save())
                {
                    world.FinishPropEmergence(1);
                    world.Restore(checkpoint);
                    Verify.Check(world.CurrentClips().Single().Mode == PropClipMode.Emergence, "rollback retains emergence");
                }
                for (int i = 0; i < 10; i++)
                    world.Step();
                var moved = world.Bodies().Single();
                Verify.Check(receiver.Distance(moved.Position) > receiver.Distance(arrival.Position) + .1f, "exposed hull advances under captured momentum");
            }
        var cube = new PropDefinition(1, default, new(.5f), 10);
        var slow = GateTravel.Exit(gate, gate, new(1, gate.World(new(0, 0, -1)), Quaternion.Identity, default, default, false), cube);
        using (var world = new PhysicsWorld(PlanetDefinition.Default with
        {
            Entities = [],
            Props = []
        }, false))
        {
            world.InsertProp(cube, slow);
            world.BeginPropEmergence(1, gate, Guid.NewGuid());
            world.AddPlayer(new(1, "Puller", new(0, slow.Position.Y - Movement.EyeHeight, 1), default, 0, 0, false, 0));
            var picked = world.PickProp(1, new(0, slow.Position.Y, 1), -Vector3.UnitZ);
            Verify.Check(picked.Prop?.Id == 1, "exposed receiving sliver can be grabbed");
            for (int i = 0; i < 8; i++)
                world.Step();
            Verify.Near(world.Bodies().Single().LinearVelocity.Z, 0, .00001f, "stationary emergence gets no automatic forward motion");
            Verify.Check(world.CurrentClips().Single().Mode == PropClipMode.Emergence, "stationary partial body remains marked");
        }
        using (var blocked = new PhysicsWorld(PlanetDefinition.Default with
        {
            Entities = [],
            Props = []
        }, false))
        {
            blocked.AddStaticBox(new(0, 4, .03f), new(2, 2, .02f));
            Verify.Check(!blocked.CanBeginPropEmergence(gate, cube, slow), "foreground obstacle blocks admission without sideways relocation");
        }
        Verify.Check(!GateTravel.ConnectionExpired(100 + GateTravel.MaximumConnectionTicks - 1, 100) && GateTravel.ConnectionExpired(100 + GateTravel.MaximumConnectionTicks, 100), "38-minute cap uses exact server tick boundary");
        await Persistence();
        Console.WriteLine($"EMERGENCE {Verify.Count} CHECKS PASSED");
    }
    static async Task Persistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "StargateEmergence-" + Guid.NewGuid());
        Guid source, destination;
        Guid unfinished, finished;
        await using (var db = new WorldRepository(path))
        {
            var definition = new StargateGame.WorldGen.PlanetPlan(7319, "temperate", settlements: new SettlementSettings("wilderness", "auto")).Definition;
            var a = await db.CreateAsync(definition);
            var b = await db.CreateAsync(definition);
            source = a.Id;
            destination = b.Id;
            unfinished = a.Definition.Props[0].PersistentId;
            finished = a.Definition.Props[1].PersistentId;
            for (int i = 0; i < 2; i++)
            {
                a = await db.LoadAsync(source);
                b = await db.LoadAsync(destination);
                var prop = a.Definition.Props.Single(p => p.PersistentId == (i == 0 ? unfinished : finished));
                var body = new BodyState(prop.Id, prop.Position, Quaternion.Identity, default, default, false);
                var record = new TransitRecord(Guid.NewGuid(), Guid.NewGuid(), source, destination, null, prop, body);
                await db.CaptureTransitAsync(record, [], []);
                var arrived = prop with
                {
                    Id = 10 + i
                };
                var pose = body with
                {
                    Id = arrived.Id
                };
                var bodies = b.Definition.Props.Select(p => new BodyState(p.Id, p.Position, Quaternion.Identity, default, default, false));
                await db.CommitPropTransitAsync(record.Id, destination, bodies.Append(pose).ToArray(), b.Definition.Props.Append(arrived).ToArray(), emerging: true);
                if (i == 1)
                    await db.CompletePropEmergenceAsync(record.Id, destination, arrived, pose);
                await db.SaveCurrentBodiesAsync(source, [body], [prop]);
            }
        }
        await using (var db = new WorldRepository(path))
        {
            var a = await db.LoadAsync(source);
            var b = await db.LoadAsync(destination);
            Verify.Check(a.Definition.Props.All(p => p.PersistentId != unfinished && p.PersistentId != finished), "stale autosaves never duplicate transferred props");
            Verify.Check(b.Definition.Props.All(p => p.PersistentId != unfinished), "restart destroys unfinished receiving prop");
            Verify.Check(b.Definition.Props.Count(p => p.PersistentId == finished) == 1, "restart keeps completed arrival exactly once");
            var prop = b.Definition.Props.Single(p => p.PersistentId == finished);
            await db.DestroyPropAsync(destination, finished);
            await db.SaveCurrentBodiesAsync(destination, [new(prop.Id, prop.Position, Quaternion.Identity, default, default, false)], [prop]);
            Verify.Check((await db.LoadAsync(destination)).Definition.Props.All(p => p.PersistentId != finished), "destroyed prop cannot resurrect through old save");
        }
    }
}
