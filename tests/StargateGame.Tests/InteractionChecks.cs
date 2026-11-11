using System.Diagnostics;
using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.WorldGen;

static class InteractionChecks
{
    public static async Task Network()
    {
        await using var server = new StargateGame.Server.GameServer(System.Net.IPAddress.Loopback, 0, 4, fixture: "graphics-lab", devPlanets: true, allowTestIdentities: true);
        server.Start();
        await using var proxy = new DelayProxy(server.Port);
        using var a = new StargateGame.Client.ClientSession(await NetworkChecks.Connect(proxy.Port, "Holder"));
        using var b = new StargateGame.Client.ClientSession(await NetworkChecks.Connect(proxy.Port, "Observer"));
        float yaw = 0, pitch = -.6f;
        async Task Pump(int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < milliseconds)
            {
                a.Poll();
                b.Poll();
                if (a.Failure is { } af)
                    throw new Exception(af);
                if (b.Failure is { } bf)
                    throw new Exception(bf);
                a.Step(0, 0, yaw, pitch, false, false);
                b.Step(0, 0, 0, 0, false, false);
                await Task.Delay(3);
            }
        }
        await Pump(500);
        await server.InspectPhysicsAsync(a.World.Definition.LocationId, w => w.Apply(w.Snapshot() with { Players = w.Players.Select(p => p.Id == a.Local.Id ? p with { Position = new(8, 0, 5), Velocity = default, Yaw = 0, Pitch = pitch } : p with { Position = new(14, 0, 7), Velocity = default }).ToArray() }));
        await Pump(500);
        var prop = a.World.Definition.Props.Single(p => p.Id == 1);
        a.Carry(CarryAction.Grab, prop.PersistentId);
        await Pump(1600);
        Verify.Check(a.Carrying && b.LastSnapshot.Carries?.Single().PersistentId == prop.PersistentId, "network carry predicted locally and replicated to observer with latency");
        using (var late = new StargateGame.Client.ClientSession(await NetworkChecks.Connect(server.Port, "Late observer")))
        {
            Verify.Check(late.LastSnapshot.Carries?.Single().PersistentId == prop.PersistentId, "late join receives active hold baseline");
        }
        yaw = .5f;
        pitch = -.2f;
        await Pump(1000);
        Verify.Check(a.Carrying && Vector3.Distance(a.Physics.Bodies().Single(p => p.Id == 1).Position, b.LastSnapshot.Bodies.Single(p => p.Id == 1).Position) < .5f, "moving hold reconciles without detaching under jitter");
        a.Carry(CarryAction.Throw);
        await Pump(500);
        Verify.Check(!a.Carrying && (b.LastSnapshot.Carries?.Length ?? 0) == 0, "network throw clears local and remote hold exactly once");
        Verify.Check(a.Physics.Bodies().Single(p => p.Id == 1).LinearVelocity.Length() < 12.5f, "network throw remains bounded");
        Console.WriteLine($"INTERACTION NETWORK {Verify.Count} CHECKS PASSED");
    }
    static PlanetDefinition Definition() => new("carry-test", "Carry test", 7319, 64, [new(0, 0, 0)], []) { Props = [new(1, new(0, 1.5f, -2), new(.5f), 10) { PersistentId = Guid.NewGuid() }] };
    static PlayerState Player(int id = 1) => new(id, "Holder", new(0, .95f, 0), default, 0, -.45f, true, 0);
    static PlayerInput Input(long seq, CarryAction action = CarryAction.None, Guid item = default, float yaw = 0, float pitch = -.45f) => new(seq, 0, 0, yaw, pitch, false, false, Carry: action, GrabItem: item);
    public static void Run()
    {
        DhdReach();
        var d = Definition();
        using var w = new PhysicsWorld(d);
        w.AddPlayer(Player());
        for (int i = 0; i < 60; i++)
            w.Step();
        var eye = w.Player(1).Position + Vector3.UnitY * Movement.EyeHeight;
        var aim = Vector3.Normalize(w.Bodies()[0].Position - eye);
        float pitch = MathF.Asin(aim.Y);
        var picked = w.PickProp(1, eye, aim);
        Verify.Check(picked.Prop?.Id == 1, "bounded native query selects visible crate");
        Verify.Check(w.InteractionBlocked(eye, aim, 2.5f, ignorePlayer: 1), "interaction query sees dynamic props");
        w.Step(new Dictionary<int, PlayerInput> { { 1, Input(1, CarryAction.Grab, d.Props[0].PersistentId, pitch: pitch) } });
        Verify.Check(w.IsCarrying(1), "visible prop is authoritatively grabbed");
        for (int i = 2; i <= 180; i++)
            w.Step(new Dictionary<int, PlayerInput> { { 1, Input(i) } });
        Verify.Check(w.IsCarrying(1) && Vector3.Distance(w.Bodies()[0].Position, w.Player(1).Position) < 3, "damped carry remains stable for three seconds");
        PhysicsWorld.Validate(w.Snapshot(), d);
        using (var checkpoint = w.Save())
        {
            for (int i = 181; i <= 210; i++)
                w.Step(new Dictionary<int, PlayerInput> { { 1, Input(i, yaw: .2f) } });
            var expected = w.Snapshot();
            w.Restore(checkpoint);
            for (int i = 181; i <= 210; i++)
                w.Step(new Dictionary<int, PlayerInput> { { 1, Input(i, yaw: .2f) } });
            Verify.Check(w.Snapshot().Bodies.SequenceEqual(expected.Bodies) && w.Carries.SequenceEqual(expected.Carries), "carry state and forces replay identically after rollback");
        }
        var before = w.Bodies()[0];
        w.Step(new Dictionary<int, PlayerInput> { { 1, Input(211, CarryAction.Throw) } });
        Verify.Check(!w.IsCarrying(1) && w.Bodies()[0].LinearVelocity.Length() > 3 && w.Bodies()[0].LinearVelocity.Length() < 12.5f, "throw releases hold and applies bounded impulse");
        w.Step(new Dictionary<int, PlayerInput> { { 1, Input(211, CarryAction.Throw) } });
        Verify.Check(w.Bodies()[0].LinearVelocity.Length() < 12.5f, "duplicate throw input does not add another impulse");
        using (var wall = new PhysicsWorld(d))
        {
            wall.AddPlayer(Player());
            wall.AddStaticBox(new(0, 2, -.9f), new(2, 2, .1f));
            wall.Step(new Dictionary<int, PlayerInput> { { 1, Input(1, CarryAction.Grab, d.Props[0].PersistentId) } });
            Verify.Check(!wall.IsCarrying(1), "cannot grab through a wall");
        }
        using (var two = new PhysicsWorld(d))
        {
            two.AddPlayer(Player());
            two.AddPlayer(Player(2) with
            {
                Position = new(.5f, .95f, 0),
                Yaw = -.2f
            });
            two.Step(new Dictionary<int, PlayerInput> { { 1, Input(1, CarryAction.Grab, d.Props[0].PersistentId) }, { 2, Input(1, CarryAction.Grab, d.Props[0].PersistentId, yaw: -.2f) } });
            Verify.Check(two.Carries.Length == 1, "simultaneous grabs have exactly one owner");
            var owner = two.Carries[0].Player;
            two.RemovePlayer(owner);
            Verify.Check(two.Carries.Length == 0, "disconnect releases physical hold");
        }
        using (var gate = new PhysicsWorld(d))
        {
            gate.AddPlayer(Player());
            gate.Step(new Dictionary<int, PlayerInput> { { 1, Input(1, CarryAction.Grab, d.Props[0].PersistentId) } });
            Verify.Check(gate.IsCarrying(1), "gate contact fixture starts held");
            gate.BeginPropEntry(1, new(new(0, 1.5f, -2), Quaternion.Identity, 2.3f));
            Verify.Check(gate.IsCarrying(1) && gate.CurrentClips().Length == 1, "partial horizon contact retains carry and clipping");
            gate.SetPropClips([]);
            Verify.Check(gate.IsCarrying(1) && gate.EntrySupports.Length == 0, "withdrawal restores full hull without dropping the held object");
            gate.BeginPropEntry(1, new(new(0, 1.5f, -1), Quaternion.Identity, 2.3f));
            Verify.Check(!gate.IsCarrying(1) && gate.EntrySupports.Length == 0, "full capture releases hold and temporary support exactly once");
        }
        using (var arrival = new PhysicsWorld(d with
        {
            Props = []
        }))
        {
            var frame = new GateFrame(new(0, 3, 0), Quaternion.Identity, 2.3f);
            var pose = new BodyState(1, new(0, 1.5f, 1.2f), Quaternion.Identity, new(0, 0, 2), default, false);
            arrival.AddStaticBox(pose.Position, new(.6f));
            Verify.Check(arrival.TryFindPropExit(frame, d.Props[0], pose, out var first) && first.Position != pose.Position, "blocked exact exit finds a nearby clear front-side pose");
            Verify.Check(Vector3.Distance(first.Position, pose.Position) <= 3 && GateTravel.Extents(frame, first, d.Props[0]).Min >= .05f && first.LinearVelocity == pose.LinearVelocity, "nearby arrival preserves momentum and front-side bounds");
            var reservation = Guid.NewGuid();
            arrival.ReservePropExit(reservation, d.Props[0], first);
            Verify.Check(!arrival.CanPlaceProp(d.Props[0], first) && arrival.TryFindPropExit(frame, d.Props[0], pose, out var second) && second.Position != first.Position, "pending arrival reservation prevents overlapping exits");
            arrival.ReleasePropExit(reservation);
            Verify.Check(arrival.CanPlaceProp(d.Props[0], first), "arrival reservation is released without leaving a collider");
            arrival.AddStaticBox(new(0, 2, 2), new(5, 3, 5));
            Verify.Check(!arrival.TryFindPropExit(frame, d.Props[0], pose, out _), "fully blocked landing area cannot bypass collision");
        }
        using (var barrel = new PhysicsWorld(d with
        {
            Props = [PropDefinition.Barrel(1, new(0, 1.6f, -2), false) with { PersistentId = d.Props[0].PersistentId }]
        }))
        {
            barrel.AddPlayer(Player());
            barrel.Step(new Dictionary<int, PlayerInput> { { 1, Input(1, CarryAction.Grab, d.Props[0].PersistentId) } });
            for (int i = 2; i < 180; i++)
                barrel.Step(new Dictionary<int, PlayerInput> { { 1, Input(i, pitch: -.15f) } });
            Verify.Check(barrel.IsCarrying(1) && barrel.Bodies()[0].Position.Y > 1.6f, "15 kg barrel lifts and remains stable");
            barrel.AddStaticBox(new(0, 2, -3), new(3, 3, .15f));
            for (int i = 180; i < 240; i++)
                barrel.Step(new Dictionary<int, PlayerInput> { { 1, new(i, 0, 1, 0, 0, false, false) } });
            Verify.Check(barrel.Bodies()[0].Position.Z > -3, "held barrel cannot pass through an obstructing wall");
        }
        QueryAndDebug();
        Console.WriteLine($"INTERACTION {Verify.Count} CHECKS PASSED");
    }
    static void DhdReach()
    {
        var dhd = new StaticEntity("reach-check", "dhd", new(17, 4, -29));
        foreach (float yaw in new[] { 0f, .7f, 2.1f, MathF.PI })
            foreach (float scale in new[] { .7f, 1f, 1.4f })
            {
                var transformed = dhd with
                {
                    Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw),
                    Scale = scale
                };
                foreach (var localEye in new[] { new Vector3(0, 2, 0), new Vector3(2.7f, 1, 0), new Vector3(0, 6, 0) })
                {
                    var eye = Vector3.Transform(localEye * scale, transformed.Rotation) + transformed.Position;
                    bool original = DhdInteraction.WithinReach(dhd with
                    {
                        Position = Vector3.Zero
                    }, localEye, 3 / scale);
                    Verify.Check(DhdInteraction.WithinReach(transformed, eye) == original, "DHD session reach respects rotation, scale and translation");
                }
            }
        Verify.Check(!DhdInteraction.WithinReach(dhd, new(float.NaN, 0, 0)), "non-finite DHD eye is rejected");
        Verify.Check(!DhdInteraction.WithinReach(dhd, dhd.Position, -1), "negative DHD reach is rejected");
    }

    static void QueryAndDebug()
    {
        var fixture = GraphicsFixture.Create("graphics-lab");
        var store = new TerrainStore(fixture.SettingsHash);
        foreach (var c in GraphicsFixture.Chunks)
            store.Put(GraphicsFixture.BuildChunk(fixture, c), false);
        using var world = new PhysicsWorld(fixture, true, store, GraphicsFixture.Chunks);
        var eye = world.World.Spawn(0) + Vector3.UnitY * Movement.EyeHeight;
        var timings = new List<double>();
        int lines = 0;
        for (int i = 0; i < 100; i++)
        {
            var watch = Stopwatch.StartNew();
            lines = world.DebugLines(eye).Take(30000).Count();
            if (i > 60)
                timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        Console.WriteLine($"DEBUG cached p95={timings[(int)(timings.Count * .95)]:F3}ms lines={lines} pending={world.DebugGeometryIncomplete}");
        Verify.Check(lines > 0 && !world.DebugGeometryIncomplete, "debug geometry finishes incremental preparation");
        var dhd = fixture.Entities.First(e => e.Model == "dhd");
        var localEye = new Vector3(0, 2, 1.8f);
        var localAim = Vector3.Normalize(new Vector3(0, .9f, 0) - localEye);
        var origin = Vector3.Transform(localEye, dhd.Transform);
        var aim = Vector3.Transform(localAim, dhd.Rotation);
        timings.Clear();
        for (int i = 0; i < 400; i++)
        {
            var watch = Stopwatch.StartNew();
            var hit = DhdInteraction.Pick(dhd, origin, aim);
            if (hit is { } h)
                world.InteractionBlocked(origin, aim, h.Distance - .015f, ignoreEntity: dhd.Id);
            if (i > 20)
                timings.Add(watch.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        Console.WriteLine($"DHD pick+occlusion p95={timings[(int)(timings.Count * .95)]:F4}ms");
        var random = new Random(5);
        for (int i = 0; i < 100; i++)
        {
            var o = new Vector3((float)random.NextDouble() * 3 - 1.5f, 2, (float)random.NextDouble() * 3 - 1.5f);
            var d = Vector3.Normalize(new Vector3(0, .9f, 0) - o);
            float closest = 3;
            int key = -1;
            foreach (var f in DhdInteraction.Surfaces)
                if (DhdInteraction.Ray(o, d, f.A, f.B, f.C) is { } t && t < closest)
                {
                    closest = t;
                    key = f.Key;
                }
            var h = DhdInteraction.Pick(new("test", "dhd", Vector3.Zero), o, d);
            Verify.Check((h?.Key ?? -1) == key, "DHD BVH agrees with exact triangle reference");
        }
    }
}
