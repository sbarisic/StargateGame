using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using StargateGame.Physics;
using StargateGame.Server;
using StargateGame.Shared;

try
{
    int groupIndex = Array.IndexOf(args, "--group");
    if (groupIndex >= 0) { await CheckGroups.Run(args[groupIndex + 1], args); return; }
    if (args.Length == 0) { await CheckGroups.Run("quick", args); return; }
    foreach (string focused in new[] { "streaming", "trees", "rocks", "worldgen", "dhd", "environment" })
        if (args.Contains("--" + focused + "-only")) { await CheckGroups.Run(focused, args); return; }
    if (args.Contains("--landscape-visual")) { LandscapeVisualChecks.Run(); return; }
    if(args.Contains("--regional-hydrology")) { RegionalHydrologyChecks.Run(); return; }
    if(args.Contains("--regional-seams")) { RegionalChecks.Seams(); return; }
    if(args.Contains("--regional-profile")) { await RegionalChecks.Profile(); return; }
    if(args.Contains("--regional-integration")) { await RegionalIntegrationChecks.Run(); return; }
    if(args.Contains("--regional")) { await RegionalChecks.Run(); return; }
    if (args.Contains("--weapon-visual-fixture")) { await WeaponNetworkChecks.Run(visual: true); return; }
    if (args.Contains("--tactical-movement"))
    {
        TacticalMovementChecks.Run(args.Contains("--trace") ? "artifacts/movement/tactical-course.csv" : null);
        MovementPresentationChecks.Run();
        return;
    }
    if (args.Contains("--weapon-network")) { await WeaponNetworkChecks.Run(); return; }
    if (args.Contains("--weapons")) { await WeaponChecks.Run(); return; }
    if (args.Contains("--canopy-lod")) { CanopyLodChecks.Run(); return; }
    if (args.Contains("--planet-debug")) { await PlanetDebugChecks.Run(); return; }
    if (args.Contains("--emergence-only"))
    {
        await EmergenceChecks.Run();
        return;
    }
    if (args.Contains("--forest-fixture"))
    {
        ForestFixtureChecks.Run();
        return;
    }
    if (args.Contains("--fixture-gates"))
    {
        await FixtureGateChecks.Run();
        return;
    }
    if (args.Contains("--unattended-travel"))
    {
        await GalaxyChecks.Network(Path.GetFullPath("artifacts/galaxy/galaxy.bin"), false, false, travel: true, unattended: true);
        return;
    }
    if (args.Contains("--prop-exit-only"))
    {
        PropExitChecks.Run();
        return;
    }
    if (args.Contains("--vegetation-only"))
    {
        EnvironmentChecks.Run();
        await CookedAssetChecks.Run();
        Console.WriteLine($"VEGETATION {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--gate-physics-only"))
    {
        GatePhysicsChecks.Run();
        await GatePhysicsChecks.Persistence();
        return;
    }
    if (args.Contains("--kawoosh-network"))
    {
        await GalaxyChecks.Network(Path.GetFullPath("artifacts/galaxy/galaxy.bin"), false, false, hazard: true, capture: args.Contains("--capture"));
        return;
    }
    if (args.Contains("--interaction-network"))
    {
        await InteractionChecks.Network();
        return;
    }
    if (args.Contains("--interaction-only"))
    {
        InteractionChecks.Run();
        return;
    }
    if (args.Contains("--mounted-palettes")) { PaletteChecks.Mounted(); return; }
    if (args.Contains("--palette-only"))
    {
        PaletteChecks.Run();
        return;
    }
    if (args.Contains("--buried-props"))
    {
        BuriedPropChecks.Run();
        if (args.Length >= 3)
            await BuriedPropChecks.SavedWorld(Path.GetFullPath(args[1]), args[2]);
        Console.WriteLine($"BURIED PROP {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--furnishing-fixtures"))
    {
        FurnishingChecks.ExportFixtures();
        return;
    }
    if (args.Contains("--furnishings-only"))
    {
        FurnishingChecks.Run();
        FurnishingChecks.Physics();
        await FurnishingChecks.Persistence();
        return;
    }
    if (args.Contains("--travel-network"))
    {
        await GalaxyChecks.Network(Path.GetFullPath("artifacts/galaxy/galaxy.bin"), args.Contains("--delayed"), false, true, args.Contains("--cold"), args.Contains("--blocked"), args.Contains("--player-push"));
        return;
    }
    if (args.Contains("--travel-only"))
    {
        TravelChecks.Run();
        await TravelChecks.Persistence();
        await TravelChecks.IdentityNetwork();
        Console.WriteLine($"TRAVEL {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--settlement-seeds"))
    {
        SettlementChecks.InspectionSeeds();
        return;
    }
    if (args.Contains("--settlement-network"))
    {
        await SettlementChecks.Network(args.Contains("--delayed"));
        return;
    }
    if (args.Contains("--settlements-only"))
    {
        if (!args.Contains("--integration"))
            SettlementChecks.Run(args.Contains("--corpus"));
        await SettlementChecks.Integration();
        return;
    }
    if (args.Contains("--rivers-only"))
    {
        await RiverChecks.Run();
        Console.WriteLine($"RIVER {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--bounded-streaming-only"))
    {
        await StreamingChecks.Run(false, args.Contains("--delayed"), true, false);
        Console.WriteLine($"BOUNDED STREAMING {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--streaming-cache-only"))
    {
        await StreamingCacheChecks.Run();
        Console.WriteLine($"STREAMING CACHE {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--gate-build-only"))
    {
        await BuildingChecks.Network(gateZone: true);
        Console.WriteLine($"GATE BUILD {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--prefab-network"))
    {
        await BuildingChecks.Network(args.Contains("--delayed"), true);
        Console.WriteLine($"PREFAB NETWORK {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--prefab-only"))
    {
        await PrefabChecks.Run();
        Console.WriteLine($"PREFAB {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--prefab-demo"))
    {
        await PrefabChecks.Demo(args[1]);
        return;
    }
    if (args.Contains("--gate-transition-only"))
    {
        GateTransitionChecks.Run();
        EventHorizonChecks.Run();
        Console.WriteLine($"GATE TRANSITION {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--meadow-props-only"))
    {
        MeadowPropChecks.Run();
        EnvironmentChecks.Run();
        if (args.Contains("--network"))
            await MeadowPropChecks.Network();
        Console.WriteLine($"MEADOW PROPS {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--address-book-only"))
    {
        AddressBookChecks.Run();
        if (args.Contains("--network"))
            await AddressBookChecks.Network();
        Console.WriteLine($"ADDRESS BOOK {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--audio-only"))
    {
        AudioEventChecks.Run();
        Console.WriteLine($"AUDIO EVENTS {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--galaxy-only"))
    {
        await GalaxyChecks.Run(!args.Contains("--no-network"), args.Contains("--delayed"), args.Contains("--expiry"));
        Console.WriteLine($"GALAXY {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--movement-only"))
    {
        MovementPresentationChecks.Run();
        PhysicsChecks.Run();
        await LatencyScenario.Run();
        Console.WriteLine($"MOVEMENT {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--horizon-only"))
    {
        EventHorizonChecks.Run();
        Console.WriteLine($"HORIZON {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--horizon-network"))
    {
        EventHorizonChecks.Run();
        await EventHorizonChecks.Network();
        await EventHorizonChecks.PropNetwork();
        Console.WriteLine($"HORIZON NETWORK {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--building-demo"))
    {
        await BuildingDemo.Create(args[1], args.Contains("--large"));
        return;
    }
    if (args.Contains("--persistence-crash-worker"))
    {
        await PersistenceChecks.CrashWorker(args[1]);
        return;
    }
    if (args.Contains("--persistence-only"))
    {
        await PersistenceChecks.Run();
        return;
    }
    if (args.Contains("--building-only"))
    {
        await BuildingChecks.Run(!args.Contains("--no-network"));
        Console.WriteLine($"BUILDING {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--building-delayed"))
    {
        await BuildingChecks.Network(true);
        return;
    }
    if (args.Contains("--atmosphere-network"))
    {
        await AtmosphereWaterChecks.Network();
        Console.WriteLine($"ATMOSPHERE NETWORK {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--atmosphere-only"))
    {
        await AtmosphereWaterChecks.Run();
        Console.WriteLine($"ATMOSPHERE WATER {Verify.Count} CHECKS PASSED");
        return;
    }
    if (args.Contains("--environment-network")) { await EnvironmentChecks.Network(); return; }
    throw new ArgumentException("Unknown test selection. Use --group NAME or scripts/Test.ps1 check -Group NAME. Full coverage is an explicit harness selection.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

static class Verify
{
    public static int Count;
    public static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAIL: " + name);
        Count++;
        Console.WriteLine("PASS: " + name);
    }
    public static void Near(float actual, float expected, float tolerance, string name) => Check(Math.Abs(actual - expected) <= tolerance, $"{name} (actual={actual:F5}, expected={expected:F5})");
}
static class PhysicsChecks
{
    static PlanetDefinition Empty => PlanetDefinition.Default with { Entities = [], Props = [] };
    static PhysicsWorld Fixture()
    {
        var w = new PhysicsWorld(Empty, false);
        w.AddStaticBox(new(0, -.5f, 0), new(150, .5f, 150));
        return w;
    }
    static void Add(PhysicsWorld w, Vector3 p, int id = 1) => w.AddPlayer(new(id, "Tester", p, default, 0, 0, true, 0));
    static void Walk(PhysicsWorld w, int ticks, float forward = 0, float strafe = 0, bool walk = false)
    {
        for (int i = 0; i < ticks; i++)
            w.Step(new Dictionary<int, PlayerInput> { { 1, new(w.Tick + 1, strafe, forward, 0, 0, walk, false) } });
    }
    public static void Run()
    {
        var terrain = new PlanetWorld(PlanetDefinition.Default);
        var terrain2 = new PlanetWorld(PlanetDefinition.Default);
        Verify.Check(Enumerable.Range(-60, 120).All(i => terrain.SampleHeight(i * 1.3f, i * .2f) == terrain2.SampleHeight(i * 1.3f, i * .2f)), "deterministic terrain");
        Verify.Check(terrain.Triangles().Count() == 32768, "shared render/collision triangle grid");
        using (var w = Fixture())
        {
            Add(w, new(0, 5, 12));
            for (int i = 0; i < 120; i++)
                w.Step();
            Verify.Check(w.Player(1).Grounded, "gravity runs with no input");
            Verify.Near(w.Player(1).Position.Y, 0, .025f, "capsule feet on ground");
            w.Step(new Dictionary<int, PlayerInput> { { 1, new(1, 0, 0, 0, 0, false, true) } });
            Verify.Check(w.Player(1).Position.Y > .05f && !w.Player(1).Grounded, "jump leaves ground without floor snap");
            for (int i = 0; i < 120; i++)
                w.Step();
            Verify.Check(w.Player(1).Grounded && Math.Abs(w.Player(1).Position.Y) < .025f, "jump edge is not repeated on missing input");
            Walk(w, 60, 1);
            var start = w.Player(1).Position;
            Walk(w, 60, 1);
            Verify.Near(start.Z - w.Player(1).Position.Z, Movement.RunSpeed, .03f, "steady default running speed");
            Walk(w, 60, 1, 0, true);
            start = w.Player(1).Position;
            Walk(w, 60, 1, 0, true);
            Verify.Near(start.Z - w.Player(1).Position.Z, Movement.WalkSpeed, .03f, "steady walking speed");
            Walk(w, 60, 1, 1);
            start = w.Player(1).Position;
            Walk(w, 60, 1, 1);
            Verify.Near(Vector3.Distance(start, w.Player(1).Position), Movement.RunSpeed, .04f, "normalized diagonal movement");
        }
        using (var w = Fixture())
        {
            w.AddStaticBox(new(0, 2.25f, 0), new(3, .15f, 3));
            Add(w, Vector3.Zero);
            Walk(w, 5);
            w.Step(new Dictionary<int, PlayerInput> { { 1, new(10, 0, 0, 0, 0, false, true) } });
            float max = w.Player(1).Position.Y;
            for (int i = 0; i < 90; i++)
            {
                w.Step();
                max = Math.Max(max, w.Player(1).Position.Y);
            }
            Verify.Check(max < .34f && w.Player(1).Grounded, "ceiling blocks jumping and character lands");
        }
        foreach (float height in new[] { .25f, .4f })
        {
            using var w = Fixture();
            w.AddStaticBox(new(0, height / 2, -3), new(2, height / 2, 2));
            Add(w, Vector3.Zero);
            Walk(w, 100, 1);
            Console.WriteLine($"step {height}: {w.Player(1).Position}");
            Verify.Check(height < .3f ? w.Player(1).Position.Z < -4 : w.Player(1).Position.Z > -1, "step limit " + height);
        }
        foreach (float degrees in new[] { 44f, 46f })
        {
            using var w = Fixture();
            float angle = degrees * MathF.PI / 180;
            w.AddStaticBox(new(4 * MathF.Cos(angle), 4 * MathF.Sin(angle) - .1f / MathF.Cos(angle), 0), new(4, .1f, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle));
            Add(w, new(-1, 0, 0));
            Walk(w, 100, 0, 1);
            Console.WriteLine($"slope {degrees}: {w.Player(1).Position}");
            Verify.Check(degrees < 45 ? w.Player(1).Position.Y > 1 : w.Player(1).Position.Y < .4f, "slope limit " + degrees);
        }
        using (var w = Fixture())
        {
            float angle = 46 * MathF.PI / 180;
            w.AddStaticBox(new(4 * MathF.Cos(angle), 4 * MathF.Sin(angle) - .1f / MathF.Cos(angle), 0), new(4, .1f, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle));
            Add(w, new(2, 2.7f, 0));
            for (int i = 0; i < 120; i++)
                w.Step();
            Verify.Check(w.Player(1).Position.Y < .4f && w.Player(1).Position.X < 1, "gravity slides down steep slopes");
        }
        using (var w = Fixture())
        {
            Add(w, Vector3.Zero);
            w.Step(new Dictionary<int, PlayerInput> { { 1, new(1, 0, 1, 0, 0, false, false) } });
            var start = w.Player(1).Position;
            for (int i = 0; i < 60; i++)
                w.Step();
            Verify.Near(start.Z - w.Player(1).Position.Z, .4f, .02f, "missing movement holds six ticks then becomes neutral");
            using var old = w.Save();
            Add(w, new(5, 0, 0), 2);
            try
            {
                w.Restore(old);
                Verify.Check(false, "stale topology restore");
            }
            catch (InvalidOperationException)
            {
                Verify.Check(true, "stale topology checkpoint rejected");
            }
        }
        using (var w = new PhysicsWorld(PlanetDefinition.Default))
        {
            Add(w, new(0, 0, 8));
            Walk(w, 240, 1);
            Verify.Check(w.Player(1).Position.Z < -6, "walk through gate opening");
            w.RemovePlayer(1);
            Add(w, new(4, 0, 9));
            Walk(w, 120, 1, 0, true);
            Console.WriteLine("DHD approach: " + w.Player(1));
            Verify.Check(w.Player(1).Position.Z > 6.4f || Math.Abs(w.Player(1).Position.X - 4) > .95f, "DHD structural mesh blocks or slides character around its side");
            w.RemovePlayer(1);
            Add(w, new(2.7f, terrain.SampleHeight(2.7f, 3), 3));
            Walk(w, 100, 1);
            Verify.Check(w.Player(1).Position.Z > 0, "gate ring blocks character");
            w.RemovePlayer(1);
            Add(w, new(127, terrain.SampleHeight(127, 20), 20));
            Walk(w, 120, 0, 1, true);
            Verify.Check(w.Player(1).Position.X < 127.71f, "perimeter collider");
            w.RemovePlayer(1);
            Add(w, new(64, terrain.SampleHeight(64, 20), 20));
            Walk(w, 60, 0, 1);
            Verify.Check(w.Player(1).Position.X > 67 && Math.Abs(w.Player(1).Position.Y - terrain.SampleHeight(w.Player(1).Position.X, w.Player(1).Position.Z)) < .04f, "terrain seams and surface alignment");
            foreach (var body in w.Bodies())
                Verify.Check(body.Sleeping, "crate sleeps " + body.Id);
            Verify.Near(w.Bodies()[1].Position.Y, 1.5f, .02f, "stack settles at correct height");
        }
        using (var w = Fixture())
        {
            w.AddProp(new(1, new(0, .51f, -2), new(.5f), 10));
            w.AddProp(new(2, new(0, .51f, -3.02f), new(.5f), 10));
            Add(w, Vector3.Zero);
            Walk(w, 30);
            var start = w.Bodies().Single(b => b.Id == 1).Position;
            Walk(w, 150, 1);
            Verify.Check(w.Bodies().All(b => b.Position.Z < -6 && b.Position.Y < .65f) && w.Bodies().Single(b => b.Id == 1).Position.Z < start.Z - 4, "sustained pushing overcomes friction of two grounded crates without launching them");
        }
        using (var w = Fixture())
        {
            Add(w, new(0, 0, 2));
            Add(w, new(0, 0, -2), 2);
            for (int i = 1; i <= 90; i++)
                w.Step(new Dictionary<int, PlayerInput> { { 1, new(i, 0, 1, 0, 0, false, false) }, { 2, new(i, 0, -1, 0, 0, false, false) } });
            Verify.Check(w.Player(1).Position.Z < -3 && w.Player(2).Position.Z > 3, "players pass through each other");
        }
        using (var w = new PhysicsWorld(PlanetDefinition.Default))
        {
            Add(w, new(-7, 0, 14));
            Walk(w, 30);
            var before = w.Bodies().Single(b => b.Id == 4).Position;
            Walk(w, 60, 1);
            Verify.Check(w.Bodies().Single(b => b.Id == 4).Position.Z < before.Z - .3f, "character pushes dynamic crate");
            using var checkpoint = w.Save();
            Walk(w, 40, 1);
            var expected = w.Snapshot();
            w.Restore(checkpoint);
            Walk(w, 40, 1);
            Verify.Check(expected.Bodies.SequenceEqual(w.Bodies()) && expected.Players.SequenceEqual(w.Players), "native rollback reproduces contacts, characters and rigid bodies");
            Verify.Check(checkpoint.ByteSize > 0 && checkpoint.ByteSize < 65536, "bounded local checkpoint");
            var corrected = w.Snapshot();
            var bodies = corrected.Bodies.ToArray();
            bodies[0] = bodies[0] with
            {
                Position = bodies[0].Position + new Vector3(.2f, .2f, 0),
                Sleeping = false
            };
            w.Apply(corrected with
            {
                Bodies = bodies
            });
            for (int i = 0; i < 180; i++)
                w.Step();
            Verify.Check(w.Bodies().All(b => float.IsFinite(b.Position.Y) && b.Position.Y > .3f), "authoritative correction rebuilds sleeping/contact state");
        }
        using (var w = new PhysicsWorld(PlanetDefinition.Default))
        {
            for (int i = 0; i < 120; i++)
                w.Step();
            var crate = w.Bodies().Single(b => b.Id == 3);
            Add(w, crate.Position + new Vector3(0, .5f, 0));
            Walk(w, 30);
            Verify.Check(w.Player(1).Grounded && w.Player(1).SupportId == 3, "standing on a crate reports support");
            var snap = w.Snapshot();
            var bodies = snap.Bodies.Select(b => b.Id == 3 ? b with { LinearVelocity = new(2, 0, 0), Sleeping = false } : b).ToArray();
            w.Apply(snap with
            {
                Bodies = bodies
            });
            var x = w.Player(1).Position.X;
            Walk(w, 8);
            Verify.Check(w.Player(1).Position.X > x + .025f, "moving support carries player");
        }
        using (var authoritative = new PhysicsWorld(PlanetDefinition.Default))
        {
            Add(authoritative, new(-7, 0, 14));
            using var predicted = new PredictedWorld(PlanetDefinition.Default, 1, authoritative.Snapshot());
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int i = 1; i <= 240; i++)
            {
                var input = new PlayerInput(i, 0, i < 100 ? 1 : 0, 0, 0, false, false, i);
                authoritative.Step(new Dictionary<int, PlayerInput> { { 1, input } });
                predicted.Advance(input);
                if (i == 120)
                {
                    var corrected = authoritative.Snapshot();
                    authoritative.Apply(corrected with
                    {
                        Bodies = corrected.Bodies.Select(b => b.Id == 4 ? b with { Position = b.Position + new Vector3(.1f, 0, 0), Sleeping = false } : b).ToArray()
                    });
                }
                if (i % 3 == 0)
                    predicted.Accept(authoritative.Snapshot());
            }
            Verify.Check(Vector3.Distance(authoritative.Player(1).Position, predicted.Simulation.Player(1).Position) < .02f, "predicted player converges");
            Verify.Check(authoritative.Bodies().Zip(predicted.Simulation.Bodies()).All(p => Vector3.Distance(p.First.Position, p.Second.Position) < .02f), "predicted crates converge");
            Verify.Check(predicted.HistoryCount <= 180 && predicted.HistoryBytes <= 32 * 1024 * 1024 && predicted.Reconciliations > 0, "prediction history bounded and rollback exercised");
            Verify.Check(predicted.MatchedSnapshots > 0, "exact confirmations preserve live contact state without rollback");
            authoritative.AddPlayer(new(2, "Join", new(3, 0, 14), default, 0, 0, true, 0));
            authoritative.Epoch++;
            predicted.Accept(authoritative.Snapshot());
            Verify.Check(predicted.Resets == 1 && predicted.Simulation.Players.Count() == 2, "epoch changes rebuild prediction topology");
        }
        Verify.Check(!new PlayerInput(1, float.NaN, 0, 0, 0, false, false).IsValid && !new PlayerInput(1, 2, 0, 0, 0, false, false).IsValid, "nonfinite and oversized input rejected");
    }
}
static class NetworkChecks
{
    public static Task<GameConnection> Connect(int port, string name) => GameConnection.ConnectAsync("127.0.0.1", port, name, PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
    static async Task<Message> Until(Peer peer, Func<Message, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(8000);
        while (true)
        {
            var m = await peer.Incoming.ReadAsync(timeout.Token);
            if (predicate(m))
                return m;
        }
    }
    static async Task RejectFrame(byte[] data, string name)
    {
        try
        {
            await Protocol.ReadAsync(new MemoryStream(data));
            throw new Exception("Accepted bad frame");
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            Verify.Check(true, name);
        }
    }
    public static async Task Run()
    {
        using (var stream = new MemoryStream())
        {
            var state = new BodyState(1, new(1, 2, 3), Quaternion.Identity, new(1, 0, 0), default, false);
            await Protocol.WriteAsync(stream, new()
            {
                Kind = "snapshot",
                State = new(1, 1, [], [state], [])
            });
            stream.Position = 0;
            var message = await Protocol.ReadAsync(new FragmentStream(stream));
            Verify.Check(message.State!.Bodies[0] == state, "fragmented protocol preserves physics state");
        }
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, Protocol.MaxFrameBytes + 1);
        await RejectFrame(prefix, "oversized frame rejected before allocation");
        await RejectFrame([0, 0, 0, 0], "zero frame rejected");
        await RejectFrame([10, 0, 0, 0, 1, 2], "truncated frame rejected");
        await RejectFrame([1, 0, 0, 0, 255], "malformed JSON rejected");
        await using var server = new GameServer(IPAddress.Loopback, 0, 2);
        server.Start();
        using var alice = await Connect(server.Port, "Alice");
        using var bob = await Connect(server.Port, "Bob");
        Verify.Check(alice.Welcome.PlayerId != bob.Welcome.PlayerId && alice.Welcome.ContentHash == bob.Welcome.ContentHash, "two clients share world and have unique IDs");
        try
        {
            using var extra = await Connect(server.Port, "Extra");
            Verify.Check(false, "server full");
        }
        catch (IOException ex)
        {
            Verify.Check(ex.Message.Contains("full"), "server capacity rejection");
        }
        var baseline = (await Until(alice.Peer, m => m.State?.Players.Length == 2)).State!;
        long first = baseline.Tick + 15;
        for (int i = 1; i <= 12; i++)
            alice.Peer.Send(new()
            {
                Kind = "input",
                Input = new(i, 0, 1, 0, 0, false, false, first + i)
            });
        var moved = (await Until(bob.Peer, m => m.State?.Players.Any(p => p.Id == alice.Welcome.PlayerId && p.LastInput == 12) == true)).State!;
        Verify.Check(moved.Players.Single(p => p.Id == alice.Welcome.PlayerId).Position.Z < baseline.Players.Single(p => p.Id == alice.Welcome.PlayerId).Position.Z - .5f, "second client sees authoritative movement");
        var before = moved.Bodies[1];
        var settled = (await Until(bob.Peer, m => m.State?.Tick > moved.Tick + 80)).State!;
        Verify.Check(settled.Bodies.All(b => b.Sleeping), "server advances props without player inputs");
        alice.Peer.Send(new()
        {
            Kind = "input",
            Input = new(13, 0, 0, 0, 0, false, false, first + 13)
        });
        var ack = (await Until(alice.Peer, m => m.State?.Players.Any(p => p.Id == alice.Welcome.PlayerId && p.LastInput == 13) == true)).State!;
        Verify.Check(ack.Tick > first + 13, "late input is discarded and acknowledged");
        bob.Dispose();
        await Until(alice.Peer, m => m.State?.Players.Length == 1);
        using var rejoined = await Connect(server.Port, "Bob");
        Verify.Check(rejoined.Welcome.PlayerId != bob.Welcome.PlayerId, "reconnect gives fresh identity and epoch");
        alice.Peer.Send(new()
        {
            Kind = "input",
            Input = new(14, 20, 0, 0, 0, false, false, ack.Tick + 2)
        });
        var rejection = await Until(alice.Peer, m => m.Kind == "reject");
        Verify.Check(rejection.Reason!.Contains("Invalid"), "server rejects invalid movement");
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, server.Port);
            await Protocol.WriteAsync(raw.GetStream(), new()
            {
                Kind = "hello",
                Name = "Old",
                Version = 999
            });
            var rejected = await Protocol.ReadAsync(raw.GetStream());
            Verify.Check(rejected.Reason!.Contains("version"), "protocol mismatch explained");
        }
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, server.Port);
            await Protocol.WriteAsync(raw.GetStream(), new()
            {
                Kind = "hello",
                Name = "Wrong",
                PhysicsHash = "wrong",
                ContentHash = PhysicsWorld.ContentHash
            });
            var rejected = await Protocol.ReadAsync(raw.GetStream());
            Verify.Check(rejected.Reason!.Contains("mismatch"), "physics compatibility mismatch explained");
        }
        using (var future = await Connect(server.Port, "Future"))
        {
            future.Peer.Send(new()
            {
                Kind = "input",
                Input = new(1, 0, 1, 0, 0, false, false, long.MaxValue)
            });
            var rejected = await Until(future.Peer, m => m.Kind == "reject");
            Verify.Check(rejected.Reason!.Contains("Invalid"), "excessively future input rejected");
        }
        await server.DisposeAsync();
        await rejoined.Peer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Verify.Check(rejoined.Peer.Closed, "server loss closes client");
        await LatencyScenario.Run();
        await RepeatedConnections();
        await PingLimits();
    }
    static async Task PingLimits()
    {
        await using var server = new GameServer(IPAddress.Loopback, 0, 2);
        server.Start();
        using var client = await Connect(server.Port, "PingLimits");
        client.Peer.Send(new()
        {
            Kind = "ping",
            Echo = 1
        });
        client.Peer.Send(new()
        {
            Kind = "ping",
            Echo = 2
        });
        await Until(client.Peer, m => m.Kind == "pong" && m.Echo == 2);
        Verify.Check(!client.Peer.Closed, "ping limiter accepts bounded catch-up burst");
        await Task.Delay(1100);
        for (int i = 0; i < 10; i++)
            client.Peer.Send(new()
            {
                Kind = "ping",
                Echo = 3 + i
            });
        var rejected = await Until(client.Peer, m => m.Kind == "reject");
        Verify.Check(rejected.Reason == "Ping rate exceeded.", "wall-clock ping limit still rejects floods");
    }
    static async Task RepeatedConnections()
    {
        await using var server = new GameServer(IPAddress.Loopback, 0, 2);
        server.Start();
        using var observer = await Connect(server.Port, "Observer");
        long lastEpoch = observer.Welcome.State!.Epoch;
        for (int i = 0; i < 5; i++)
        {
            using (var client = new StargateGame.Client.ClientSession(await Connect(server.Port, "Cycle" + i)))
            {
                client.Poll();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Verify.Check(client.Failure is null, "connection cycle initializes native world " + i);
            }
            var state = (await Until(observer.Peer, m => m.State?.Players.Length == 1 && m.State.Epoch > lastEpoch)).State!;
            Verify.Check(state.Epoch > lastEpoch, "disconnect advances topology epoch " + i);
            lastEpoch = state.Epoch;
        }
    }
}
sealed class FragmentStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }
    public override int Read(byte[] b, int o, int c) => inner.Read(b, o, Math.Min(c, 3));
    public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => inner.ReadAsync(b[..Math.Min(b.Length, 3)], ct);
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}
