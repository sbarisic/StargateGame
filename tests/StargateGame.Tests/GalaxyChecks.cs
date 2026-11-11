using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.Galaxy;
using StargateGame.Persistence;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Server;
using StargateGame.Client;
using StargateGame.WorldGen;

static class GalaxyChecks
{
    public static async Task Run(bool network, bool delayed = false, bool expiry = false)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "StargateGame.sln")))
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)) ?? throw new DirectoryNotFoundException("Repository root");
        string artifact = Path.Combine(root, "artifacts/galaxy/galaxy.bin");
        if (!File.Exists(artifact))
            Catalogue.Build(artifact, Console.WriteLine);
        using (var catalogue = new Catalogue(artifact))
        {
            Verify.Check(catalogue.Count == 19_020_803, "complete adopted galaxy census");
            int[] symbols = [28, 26, 5, 36, 11, 29];
            var solved = Address.Resolve(symbols);
            var key = solved.Key;
            int[][] permutations = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
            foreach (var permutation in permutations)
                for (int bits = 0; bits < 8; bits++)
                {
                    var equivalent = permutation.SelectMany((pair, i) => (bits & (1 << i)) == 0 ? new[] { symbols[pair * 2], symbols[pair * 2 + 1] } : new[] { symbols[pair * 2 + 1], symbols[pair * 2] }).ToArray();
                    Verify.Check(Address.Resolve(equivalent).Key == key, "pair order/reflection equivalence " + string.Join('-', equivalent));
                }
            Verify.Check(Address.Canonical([28, 5, 26, 36, 11, 29]) != key, "different pairings remain different addresses");
            foreach (var route in Address.Data.Routes.Where(r => !r.Intergalactic))
                Verify.Check(catalogue.Find(route.Key).Position == Address.Resolve(route.GameSequence).Position, "named route " + route.Record + " " + route.Name);
            Verify.Check(Address.Data.Routes.Count(r => r.GameAuthored) == 3, "three explicit game-authored completions");
            Verify.Check(Address.Data.Routes.Count(r => !r.Intergalactic && !r.DhdCompatible) == 2, "two ordinary routes require hidden glyph 13");
            Verify.Check(Address.Acquired(default, new(1, 0, 0)) && !Address.Acquired(default, new(1.000001, 0, 0)) && Address.Acquired(default, new(.999999, 0, 0)), "inclusive one-light-year acquisition");
            Verify.Check(catalogue.Nearby(solved.Position).Any(r => r.Key == key), "spatial index finds retained Earth entry");
            var universe = Guid.NewGuid();
            Verify.Check(Address.Identity(universe, key, "location") != Address.Identity(universe, key, "gate") && Address.Identity(universe, key, "location") == Address.Identity(universe, key, "location"), "stable namespaced identities");
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            int seed = Address.ContentSeed(-123456789, universe);
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
                Verify.Check(Address.ContentSeed(-123456789, universe) == seed, "content seed remains stable across system cultures");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
            foreach (int[] bad in new[] { new[] { 1, 2, 3, 4, 5, 6 }, new[] { 2, 2, 3, 4, 5, 6 }, new[] { 11, 27, 23, 16, 33, 3, 9 } })
            {
                bool failed = false;
                try
                {
                    Address.Canonical(bad);
                }
                catch (ArgumentException)
                {
                    failed = true;
                }
                Verify.Check(failed, "invalid/intergalactic address refused");
            }
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            string interrupted = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".catalogue");
            try
            {
                Catalogue.Build(interrupted, cancellation: cancel.Token);
            }
            catch (OperationCanceledException)
            {
            }
            Verify.Check(!File.Exists(interrupted) && Directory.GetFiles(Path.GetDirectoryName(interrupted)!, Path.GetFileName(interrupted) + ".building-*").Length == 0, "cancelled catalogue never publishes partial data");
            File.WriteAllText(interrupted, "retained catalogue");
            try
            {
                Catalogue.Build(interrupted, cancellation: cancel.Token);
            }
            catch (OperationCanceledException)
            {
            }
            Verify.Check(File.ReadAllText(interrupted) == "retained catalogue", "interrupted replacement preserves previous catalogue");
            File.Delete(interrupted);
            using (var corrupt = File.Create(interrupted))
            {
                corrupt.SetLength(catalogue.Bytes);
                using var source = File.OpenRead(artifact);
                var header = new byte[256];
                source.ReadExactly(header);
                corrupt.Write(header);
            }
            bool invalid = false;
            try
            {
                using var corrupt = new Catalogue(interrupted);
            }
            catch (InvalidDataException)
            {
                invalid = true;
            }
            Verify.Check(invalid, "same-sized corrupt catalogue rejected by checksum");
            File.Delete(interrupted);
        }
        var dhd = new StaticEntity("test", "dhd", Vector3.Zero);
        foreach (int key in Enumerable.Range(0, 40).Where(k => k == 0 || DhdInteraction.Available(k)))
        {
            bool found = false;
            foreach (var eye in new[] { new Vector3(0, 1.65f, 1.1f), new Vector3(-1.1f, 1.65f, .4f), new Vector3(1.1f, 1.65f, .4f), new Vector3(0, 2.2f, 0) })
                foreach (var face in DhdInteraction.Surfaces.Where(s => s.Key == key))
                    if (DhdInteraction.Pick(dhd, eye, Vector3.Normalize((face.A + face.B + face.C) / 3 - eye))?.Key == key)
                    {
                        found = true;
                        break;
                    }
            Verify.Check(found, "physical picking surface " + key);
        }
        Verify.Check(DhdInteraction.Surfaces.All(s => s.Key != 13), "hidden overlapping key has no interaction surface");
        // Physical points on the red dome, independent of the exported key labels.
        foreach (float x in new[] { -.06f, 0, .06f })
            foreach (float z in new[] { -.13f, -.07f, -.01f })
                Verify.Check(DhdInteraction.Pick(dhd, new(x, 2, z), -Vector3.UnitY)?.Key == 0, $"red orb surface picks at {x}, {z}");
        foreach (var eye in new[] { new Vector3(0, 1.65f, 1.1f), new Vector3(-1.1f, 1.65f, .4f), new Vector3(1.1f, 1.65f, .4f), new Vector3(0, 1.65f, -1.1f) })
            Verify.Check(DhdInteraction.Pick(dhd, eye, Vector3.Normalize(new Vector3(0, 1.18f, -.08f) - eye))?.Key == 0, "red orb centre picks from " + eye);
        Verify.Check(DhdInteraction.Pick(dhd, new(0, 5, 0), -Vector3.UnitY) is null, "DHD rejects surfaces beyond three metres");
        Verify.Check(DhdInteraction.Pick(dhd, new(0, -.5f, 0), Vector3.UnitY) is null, "DHD base occludes keys when aiming from below");
        Verify.Check(DhdInteraction.Pick(dhd, Vector3.Zero, new(float.NaN, 0, 0)) is null, "non-finite DHD aim rejected");
        await Persistence(artifact);
        if (network)
            await Network(artifact, delayed, expiry);
    }
    static async Task Persistence(string artifact)
    {
        using var catalogue = new Catalogue(artifact);
        string directory = Path.Combine(Path.GetTempPath(), "stargate-galaxy-save-" + Guid.NewGuid().ToString("N"));
        var definition = new PlanetPlan(7319, "temperate").Definition;
        UniverseGalaxy retained;
        Guid home;
        ulong earth = Address.Canonical([28, 26, 5, 36, 11, 29]), abydos = Address.Canonical([27, 7, 15, 32, 12, 30]);
        await using (var db = new WorldRepository(directory))
        {
            var original = await db.ResolveAsync(definition);
            home = original.Id;
            retained = await db.AdoptGalaxyAsync(catalogue, home);
            Verify.Check(retained.HomeLocation == home, "adoption preserves current location identity");
            var homeEnvironment = (await db.LoadAsync(home)).Definition.Environment!;
            Verify.Check(homeEnvironment.Phase(0) * 24 == 11 && homeEnvironment.Phase(2000000000) * 24 == 11, "home stays at 11:00 independently of UTC");
            var first = db.Association(retained, earth);
            var second = db.Association(retained, abydos);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.MaterializeGalaxyAsync(retained, first, definition)));
            Verify.Check(results.All(r => r.Id == first.LocationId), "concurrent materialization deduplicates stable identity");
            var other = await db.MaterializeGalaxyAsync(retained, second, definition);
            Verify.Check(other.Id != first.LocationId && other.Definition.Seed == results[0].Definition.Seed, "same seed never merges galaxy locations");
            Verify.Check(other.Definition.Environment!.EpochUtcMilliseconds == retained.Epoch, "unvisited clock derives from retained universe epoch");
            Verify.Check(other.Definition.Environment.FixedHour is null, "destination worlds retain day/night cycles");
            await db.SetGalaxyCorrectionAsync(catalogue, earth, 0, new(1, 0, 0));
            Verify.Check((await db.ResolveGalaxyAsync(retained, catalogue, earth)).Association.LocationId == first.LocationId, "correction at inclusive acquisition boundary retains receiver");
            await db.SetGalaxyCorrectionAsync(catalogue, earth, 1, new(1.0001, 0, 0));
            bool rejected = false;
            try
            {
                await db.ResolveGalaxyAsync(retained, catalogue, earth);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Verify.Check(rejected, "out-of-range retained association fails without redirect");
            await db.SetGalaxyCorrectionAsync(catalogue, earth, 2, default);
            ulong alias = catalogue.At(100).Key;
            await db.SetGalaxyAliasAsync(catalogue, alias, earth);
            await db.SetGalaxyCorrectionAsync(catalogue, alias, 0, catalogue.Find(earth).Position - catalogue.Find(alias).Position);
            Verify.Check((await db.ResolveGalaxyAsync(retained, catalogue, alias)).Association.LocationId == first.LocationId, "explicit corrected alias reaches retained location");
            await db.SetGateAvailabilityAsync(retained, catalogue, earth, true, false);
            Verify.Check(!(await db.ResolveGalaxyAsync(retained, catalogue, earth)).Association.Available, "receiver availability retained independently of terrain");
            await db.SetGateRouteSupportAsync(retained, catalogue, earth, false);
            Verify.Check(!(await db.ResolveGalaxyAsync(retained, catalogue, earth)).Association.SupportsOrdinary, "ordinary-route capability retained independently of availability");
        }
        await using (var db = new WorldRepository(directory))
        {
            var loaded = await db.AdoptGalaxyAsync(catalogue, home);
            Verify.Check(loaded == retained, "universe seed, epoch and home persist across restart");
            Verify.Check((await db.LoadAsync(home)).Definition.Environment!.FixedHour == 11, "home clock survives restart");
            Verify.Check((await db.LoadAsync(db.Association(loaded, earth).LocationId)).Id == db.Association(retained, earth).LocationId, "materialized location survives restart with stable ID");
            Verify.Check(!(await db.ResolveGalaxyAsync(loaded, catalogue, earth)).Association.SupportsOrdinary, "receiver capability survives server restart");
        }
    }
    static string hazardReadyFile = "";
    static Process StartHazardCamera(int port)
    {
        var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../StargateGame.Client/release/StargateGame.Client.dll"));
        var output = Path.GetFullPath("artifacts/gate-physics-fixes/destruction-capture-" + DateTime.UtcNow.ToString("HHmmss"));
        Directory.CreateDirectory(output);
        hazardReadyFile = Path.Combine(output, "ready.txt");
        Console.WriteLine("HAZARD CAMERA " + output);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { executable, "--host", "127.0.0.1", "--port", port.ToString(), "--name", "Hazard-camera", "--view", "horizon-side", "--characters", "0", "--audio", "off", "--gate-movie", "--inspection-ready-file", hazardReadyFile, "--benchmark-seconds", "100", "--benchmark-warmup", "5", "--graphics", "performance", "--time-of-day", "12", "--capture-dir", output })
            info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    public static async Task Network(string catalogue, bool delayed, bool expiry, bool travel = false, bool cold = false, bool blocked = false, bool playerPush = false, bool hazard = false, bool capture = false, bool unattended = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "stargate-galaxy-network-" + Guid.NewGuid().ToString("N"));
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, 7319, "temperate", true, directory, cataloguePath: catalogue, allowTestIdentities: travel || hazard);
        server.Start();
        await using var proxy = delayed ? new DelayProxy(server.Port) : null;
        int port = proxy?.Port ?? server.Port;
        using var identity = new PlayerIdentity();
        var connectionA = await GameConnection.ConnectAsync("127.0.0.1", port, "Dialer", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity);
        using var a = new ClientSession(connectionA);
        var connectionB = await GameConnection.ConnectAsync("127.0.0.1", port, "Observer", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        using var b = new ClientSession(connectionB);
        bool activeA = true;
        async Task Pump(int ms, Vector3? target = null, Vector3? secondTarget = null)
        {
            var time = Stopwatch.StartNew();
            double nextStep = 0;
            while (time.ElapsedMilliseconds < ms)
            {
                if (activeA)
                    a.Poll();
                b.Poll();
                if (activeA && a.Failure is not null || b.Failure is not null)
                    throw new Exception(a.Failure ?? b.Failure);
                if (time.Elapsed.TotalSeconds >= nextStep)
                {
                    nextStep += 1d / 60;
                    var d = target is { } t ? t - a.Local.Position : Vector3.Zero;
                    var move = new Vector2(d.X, -d.Z);
                    if (move.LengthSquared() > .04f)
                        move = Vector2.Normalize(move);
                    else
                        move = Vector2.Zero;
                    if (activeA)
                        a.Step(move.X, move.Y, 0, 0, false, false);
                    var bd = secondTarget is { } bt ? bt - b.Local.Position : Vector3.Zero;
                    var bm = new Vector2(bd.X, -bd.Z);
                    if (bm.LengthSquared() > .04f)
                        bm = Vector2.Normalize(bm);
                    else
                        bm = Vector2.Zero;
                    b.Step(bm.X, bm.Y, 0, 0, false, false);
                }
                await Task.Delay(4);
            }
        }
        await Pump(300);
        Guid home = a.World.Definition.LocationId;
        Verify.Check(a.Gate?.Phase == GatePhase.Idle && a.ReturnAddress.Length == 6, "home starts idle with retained return address");
        var dhd = a.World.Definition.Entities.Single(e => e.Model == "dhd");
        var target = dhd.Position + new Vector3(0, 0, 1.1f);
        for (int i = 0; i < 100 && Vector2.Distance(new(a.Local.Position.X, a.Local.Position.Z), new(target.X, target.Z)) > .25f; i++)
            await Pump(100, target);
        await Pump(1800);
        Vector3 Aim(int key, ClientSession? client = null)
        {
            var controlled = client ?? a;
            var eye = controlled.LastSnapshot.Players.Single(p => p.Id == controlled.Local.Id).Position + Vector3.UnitY * Movement.EyeHeight;
            if (key == 0)
                return Vector3.Normalize(new Vector3(0, 1.18f, -.08f) * dhd.Scale + dhd.Position - eye);
            foreach (var face in DhdInteraction.Surfaces.Where(f => f.Key == key).OrderByDescending(f => Math.Abs(Vector3.Cross(f.B - f.A, f.C - f.A).Y)))
            {
                var aim = Vector3.Normalize((face.A + face.B + face.C) / 3 * dhd.Scale + dhd.Position - eye);
                if (DhdInteraction.Pick(dhd, eye, aim)?.Key == key)
                    return aim;
            }
            throw new Exception($"Cannot pick key {key} from {a.Local.Position} near {dhd.Position}");
        }
        async Task Press(int key)
        {
            // Wait out presentation/prediction position convergence after fixture walking.
            // Retry only a rejected pick, never a successful press or a semantic failure.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                a.Dial("press", key, Aim(key));
                for (int i = 0; i < 150 && a.DialPending; i++)
                    await Pump(20);
                if (!a.DialStatus.Contains("Aim at a visible"))
                    break;
                await Pump(1000);
            }
            Console.WriteLine("DIAL " + key + ": " + a.DialStatus);
        }
        if (cold || blocked || unattended)
        {
            server.DiagnosticDestinationDelayMilliseconds = cold ? 22000 : 0;
            a.PrepareGateVisuals = _ => false;
            b.PrepareGateVisuals = _ => false;
            foreach (int key in new[] { 28, 26, 5, 36, 11, 29, 1 })
                await Press(key);
            var openWatch = Stopwatch.StartNew();
            await Press(0);
            while (a.Gate!.Phase != GatePhase.Open && openWatch.Elapsed.TotalSeconds < 10)
                await Pump(20);
            Verify.Check(a.Gate.Phase == GatePhase.Open && (!cold || server.LoadedLocationCount == 1), "source opens before cold destination loads, without visual-readiness ACKs");
            double openMilliseconds = openWatch.Elapsed.TotalMilliseconds;
            if (unattended)
            {
                var readyWatch = Stopwatch.StartNew();
                while (a.Destination?.Stage != PreloadStage.Ready && readyWatch.Elapsed.TotalSeconds < 40)
                    await Pump(20);
                Verify.Check(a.Destination?.Stage == PreloadStage.Ready, "unattended receiving collision is prepared");
                Guid destination = a.Destination!.Begin.LocationId;
                var sourceFrame = GateFrame.From(a.World.Definition.Entities.Single(e => e.Model == "stargate"));
                var sent = a.World.Definition.Props.Where(p => !p.IsBarrel).Take(1).Concat(a.World.Definition.Props.Where(p => p.IsBarrel).Take(1)).ToArray();
                Verify.Check(sent.Length == 2, "unattended travel exercises a crate and barrel");
                GateFrame receiver = default;
                BodyState lastReceived = default;
                foreach (var item in sent)
                {
                    await server.InspectPhysicsAsync(home, w => w.Apply(w.Snapshot() with
                    {
                        Bodies = w.Bodies().Select(body => body.Id == item.Id ? body with
                        {
                            Position = sourceFrame.Centre + sourceFrame.Normal * 1.1f,
                            Rotation = Quaternion.Identity,
                            LinearVelocity = -sourceFrame.Normal * 6 + Vector3.UnitY * 2,
                            AngularVelocity = default,
                            Sleeping = false
                        } : body).ToArray()
                    }));
                    bool arrived = false;
                    BodyState first = default;
                    long firstTick = 0;
                    var flight = Stopwatch.StartNew();
                    while (!arrived && flight.Elapsed.TotalSeconds < 15)
                    {
                        await Pump(100);
                        await server.InspectPhysicsAsync(destination, w =>
                        {
                            var received = w.World.Definition.Props.FirstOrDefault(p => p.PersistentId == item.PersistentId);
                            if (received is null)
                                return;
                            Verify.Check(!w.Players.Any(), "receiver has no players during prop arrival");
                            receiver = GateFrame.From(w.World.Definition.Entities.Single(e => e.Model == "stargate"));
                            first = w.Bodies().Single(body => body.Id == received.Id);
                            firstTick = w.Tick;
                            arrived = true;
                        });
                    }
                    Verify.Check(arrived, "item arrives in unattended world: " + item.Kind);
                    Verify.Check(Vector3.Dot(first.LinearVelocity, receiver.Normal) > .1f, "thrown item retains outward momentum: " + item.Kind);
                    await Pump(1200);
                    await server.InspectPhysicsAsync(destination, w =>
                    {
                        var received = w.World.Definition.Props.Single(p => p.PersistentId == item.PersistentId);
                        var body = w.Bodies().Single(p => p.Id == received.Id);
                        lastReceived = body;
                        Verify.Check(!w.Players.Any() && w.Tick > firstTick + 30, "unattended receiver continues fixed physics steps after transfer completes");
                        Console.WriteLine($"UNATTENDED {item.Kind}: first={first.Position} velocity={first.LinearVelocity} final={body.Position} velocity={body.LinearVelocity} ticks={w.Tick - firstTick} normal={receiver.Normal}");
                        Verify.Check(Vector3.Distance(body.LinearVelocity, first.LinearVelocity) > .1f,
                            "received item responds to gravity/contact instead of freezing at exit: " + item.Kind);
                    });
                    await server.InspectPhysicsAsync(home, w => Verify.Check(w.World.Definition.Props.All(p => p.PersistentId != item.PersistentId), "unattended arrival does not duplicate source prop"));
                }
                await Press(0);
                var closeWatch = Stopwatch.StartNew();
                while (a.Gate!.Phase != GatePhase.Idle && closeWatch.Elapsed.TotalSeconds < 12)
                    await Pump(20);
                Verify.Check(a.Gate!.Phase == GatePhase.Idle && server.ActiveConnectionCount == 0, "connection closes before unattended settling check");
                Vector3 launched = default;
                long tick = 0;
                await server.InspectPhysicsAsync(destination, w =>
                {
                    launched = receiver.World(new(4, 0, 8));
                    launched.Y = w.World.SampleHeight(launched.X, launched.Z) + 6;
                    tick = w.Tick;
                    w.Apply(w.Snapshot() with
                    {
                        Bodies = w.Bodies().Select(body => body.Id == lastReceived.Id ? body with
                        {
                            Position = launched,
                            LinearVelocity = receiver.Normal * 4,
                            AngularVelocity = default,
                            Sleeping = false
                        } : body).ToArray(),
                        Clips = []
                    });
                });
                await Pump(1200);
                await server.InspectPhysicsAsync(destination, w =>
                {
                    var body = w.Bodies().Single(p => p.Id == lastReceived.Id);
                    Verify.Check(!w.Players.Any() && w.Tick > tick + 30 && body.Position.Y < launched.Y - 1 && Vector3.Dot(body.Position - launched, receiver.Normal) > 1,
                        "unattended airborne prop keeps flying and falling after gate closes");
                });
                return;
            }

            var gate = GateFrame.From(a.World.Definition.Entities.Single(e => e.Model == "stargate"));
            // Barrels have the expensive rounded hull that previously caused
            // blocked-exit searches to monopolize the authoritative tick.
            var prop = blocked ? (a.World.Definition.Props.FirstOrDefault(p => p.IsBarrel) ?? a.World.Definition.Props.Single(p => p.Id == 1)) : a.World.Definition.Props.Single(p => p.Id == 1);
            if (blocked)
            {
                while (a.Destination?.Stage != PreloadStage.Ready)
                    await Pump(20);
                await server.InspectPhysicsAsync(a.Destination.Begin.LocationId, w =>
                {
                    var receiver = GateFrame.From(w.World.Definition.Entities.Single(e => e.Model == "stargate"));
                    w.AddStaticBox(receiver.Centre + receiver.Normal, new(8, 8, 8));
                });
            }
            await server.InspectPhysicsAsync(home, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(body => body.Id == prop.Id ? body with { Position = gate.Centre + gate.Normal * 1.1f, LinearVelocity = -gate.Normal * 4 + Vector3.UnitY * 3, AngularVelocity = default, Sleeping = false } : body).ToArray() }));
            var front = gate.Centre + gate.Normal * 3;
            front.Y = a.Local.Position.Y;
            for (int i = 0; i < 100 && Vector2.Distance(new(a.Local.Position.X, a.Local.Position.Z), new(front.X, front.Z)) > .2f; i++)
                await Pump(100, front);
            var rear = front - gate.Normal * 6;
            var transit = Stopwatch.StartNew();
            double captured = -1;
            bool coldHold = false;
            while (a.World.Definition.LocationId == home && transit.Elapsed.TotalSeconds < 40)
            {
                await Pump(20, blocked && captured >= 0 ? null : rear);
                if (captured < 0 && a.Transit is not null)
                    captured = transit.Elapsed.TotalSeconds;
                coldHold |= a.TransitWhite >= .99 && server.LoadedLocationCount == 1;
                if (blocked && captured >= 0 && a.Transit is null && !a.TransitLocked)
                    break;
            }
            if (blocked)
            {
                await Pump(300);
                Verify.Check(captured >= 0 && a.World.Definition.LocationId == home && a.Transit is null && !a.Local.TransitFrozen, "blocked receiver returns player to source without permanent white");
                for (int i = 0; i < 250 && !a.World.Definition.Props.Any(p => p.PersistentId == prop.PersistentId); i++)
                    await Pump(20);
                Verify.Check(a.World.Definition.Props.Count(p => p.PersistentId == prop.PersistentId) == 1, "blocked receiver restores captured prop exactly once");
                Verify.Check(a.Gate!.Phase == GatePhase.Open, "blocked transfer does not corrupt the connection");
                return;
            }
            Verify.Check(coldHold, "player remains white while receiver is still unloaded");
            Verify.Check(a.World.Definition.LocationId != home && transit.Elapsed.TotalSeconds - captured > 2, "cold readiness extends the shared minimum transit hold");
            for (int i = 0; i < 250 && !a.World.Definition.Props.Any(p => p.PersistentId == prop.PersistentId); i++)
                await Pump(20);
            Verify.Check(a.World.Definition.Props.Any(p => p.PersistentId == prop.PersistentId) && b.World.Definition.Props.All(p => p.PersistentId != prop.PersistentId), "cold prop waits captured and arrives exactly once");
            Verify.Check(b.World.Definition.LocationId == home && b.Transit is null, "source observer remains active during cold transit");
            Console.WriteLine($"COLD TRANSIT seconds={transit.Elapsed.TotalSeconds - captured:F3}; source_open_ms={openMilliseconds:F1}");
            return;
        }
        await Press(0);
        Verify.Check(a.Gate!.Symbols.Length == 0 && a.DialStatus.Contains("six"), "early orb preserves idle sequence");
        await Press(28);
        await Press(28);
        Verify.Check(a.Gate.Symbols.SequenceEqual(new[] { 28 }) && b.Gate!.Symbols.SequenceEqual(a.Gate.Symbols), "accepted lights replicate; duplicate glyph does not toggle");
        await Press(0);
        await Pump(200);
        Verify.Check(a.Gate.Phase == GatePhase.Idle && a.Gate.Symbols.Length == 0 && a.Gate.Owner == 0 && b.Gate!.Symbols.Length == 0, "incomplete orb activation resets accepted lights and ownership for all observers");
        await Press(28);
        await Press(1);
        Verify.Check(a.Gate.Symbols.Length == 1, "early origin retains accepted progress");
        var observerTarget = dhd.Position + new Vector3(-1.1f, 0, 1.1f);
        for (int i = 0; i < 100 && Vector2.Distance(new(b.Local.Position.X, b.Local.Position.Z), new(observerTarget.X, observerTarget.Z)) > .25f; i++)
            await Pump(100, secondTarget: observerTarget);
        // The dome has a broad visible target from the observer's side; a thin key
        // can be hidden by the nearer keys after authoritative position correction.
        await Pump(800);
        b.Dial("press", 0, Aim(0, b));
        for (int i = 0; i < 150 && b.DialPending; i++)
            await Pump(20);
        Verify.Check(b.LastDhdResult is { Accepted: false } && b.DialStatus.Contains("owns"), "competing nearby dialer cannot modify exclusive session");

        async Task<DhdResult> Raw(DhdCommand command)
        {
            connectionA.Peer.Send(new()
            {
                Kind = "dhd",
                Dhd = command
            });
            for (int i = 0; i < 150 && a.LastDhdResult?.RequestId != command.RequestId; i++)
                await Pump(20);
            return a.LastDhdResult?.RequestId == command.RequestId ? a.LastDhdResult : throw new Exception("No raw DHD result");
        }
        var duplicate = new DhdCommand(Guid.NewGuid(), home, a.Gate.DhdId, a.Physics.Epoch, a.Gate.Revision, "press", 26, Aim(26));
        var accepted = await Raw(duplicate);
        Verify.Check(accepted.Accepted, "raw valid key accepted");
        var stale = await Raw(duplicate with
        {
            RequestId = Guid.NewGuid(),
            Key = 5,
            Aim = Aim(5)
        });
        Verify.Check(!stale.Accepted && a.Gate.Symbols.Length == 2, "stale revision cannot alter accepted sequence");
        var replay = await Raw(duplicate);
        Verify.Check(replay == accepted && a.Gate.Symbols.Length == 2, "duplicate request returns original result without another press");
        var hidden = await Raw(duplicate with
        {
            RequestId = Guid.NewGuid(),
            Revision = a.Gate.Revision,
            Key = 13
        });
        Verify.Check(!hidden.Accepted, "hidden key cannot be submitted through another surface");
        foreach (int key in new[] { 5, 36, 11, 29 })
            await Press(key);
        for (int i = 0; i < 100 && server.PrefetchLoadsStarted == 0; i++)
            await Pump(20);
        Verify.Check(server.PrefetchLoadsStarted == 1 && a.Gate.Phase == GatePhase.Dialing && server.ActiveConnectionCount == 0, "sixth symbol starts background load without reserving or opening either gate");
        for (int i = 0; i < 300 && a.Destination?.Stage != PreloadStage.Ready; i++)
            await Pump(50);
        Verify.Check(a.Destination is { Stage: PreloadStage.Ready }, "six-symbol dialer prepares independent destination cache: " + a.Destination?.Stage);
        int preloadCount = a.Destination!.Begin.Manifest!.Length;
        Console.WriteLine($"PRELOAD counts manifest={preloadCount} store={a.Destination.Store.Count} meshes={a.Destination.Meshes.Count} collision={a.Destination.CollisionInputs.Count}");
        Verify.Check(a.Destination.Store.Count == preloadCount && a.Destination.Meshes.Count == preloadCount && a.Destination.CollisionInputs.Count == preloadCount, "preload contains all gate-area and building-owner chunks with meshes and collision inputs");
        for (int i = 0; i < 300 && a.Destination?.PhysicsReady != true; i++)
            await Pump(20);
        Verify.Check(a.Destination is { PhysicsReady: true, Physics: not null }, "destination native collision is ready before gate capture");
        Verify.Check(a.Destination.Bytes <= StreamingSettings.PreloadCpuLimit && b.PreloadBytesReceived == 0, "bounded preload memory; nearby idle observer receives zero speculative bytes");
        a.Dial("undo", -1, Aim(29));
        for (int i = 0; i < 100 && a.DialPending; i++)
            await Pump(20);
        Verify.Check(a.Gate.Symbols.Length == 5 && server.PrefetchedLocationCount == 0, "editing an address releases speculative interest");
        await Pump(150);
        Verify.Check(a.Destination is null, "undo cancels client cache without moving source player");
        await Press(29);
        await Press(1);
        Verify.Check(a.Gate.Symbols.Length == 7, "complete destination and origin accepted");
        Verify.Check(Enumerable.Range(1, 7).All(n => a.AudioGates.Any(e => !e.Baseline && e.Gate.Symbols.Length == n)), "audio retains every accepted glyph transition");
        Verify.Check(a.AudioGates.Select(e => e.Gate.Revision).Distinct().Count() == a.AudioGates.Count, "duplicate requests do not duplicate audio transitions");
        for (int i = 0; i < 300 && a.Destination?.Stage != PreloadStage.Ready; i++)
            await Pump(50);
        var sourcePosition = a.Local.Position;
        b.InspectDestination([28, 26, 5, 36, 11, 29]);
        for (int i = 0; i < 600 && b.World.Definition.LocationId == home; i++)
            await Pump(100);
        Verify.Check(b.World.Definition.LocationId != home && a.World.Definition.LocationId == home, "developer relocation moves only its own session");
        Verify.Check(Vector3.Distance(a.Local.Position, sourcePosition) < .1f, "destination loading preserves source position");
        using var captureProcess = capture ? StartHazardCamera(server.Port) : null;
        if (capture)
        {
            var readiness = Stopwatch.StartNew();
            while (!File.Exists(hazardReadyFile) && !captureProcess!.HasExited && readiness.Elapsed.TotalSeconds < 90)
                await Pump(50);
            Verify.Check(File.Exists(hazardReadyFile), "capture camera warmed before opening");
            await Pump(500);
        }
        Guid[] hazardVictims = [], hazardSurvivors = [];
        if (hazard)
        {
            var victims = new List<Guid>();
            var survivors = new List<Guid>();
            foreach (var client in new[] { a, b })
            {
                var props = client.World.Definition.Props;
                victims.Add(props[0].PersistentId);
                survivors.Add(props[1].PersistentId);
                await server.InspectPhysicsAsync(client.World.Definition.LocationId, w =>
                {
                    var g = GateFrame.From(w.World.Definition.Entities.Single(e => e.Model == "stargate"));
                    w.Apply(w.Snapshot() with
                    {
                        Bodies = w.Bodies().Select(body => body.Id == props[0].Id ? body with { Position = g.World(new(0, 0, 3)), LinearVelocity = default, AngularVelocity = default } : body.Id == props[1].Id ? body with { Position = g.World(new(0, 0, -2)), LinearVelocity = default, AngularVelocity = default } : body).ToArray()
                    });
                    w.HoldProp(props[0].Id);
                    w.HoldProp(props[1].Id);
                });
            }
            hazardVictims = victims.ToArray();
            hazardSurvivors = survivors.ToArray();
        }
        await Press(0);
        bool incoming = false, opening = false;
        for (int i = 0; i < 400 && (a.Gate!.Phase != GatePhase.Open || b.Gate?.Phase != GatePhase.Open); i++)
        {
            await Pump(50);
            incoming |= b.Gate?.Incoming == true;
            opening |= a.Gate?.Phase == GatePhase.Opening;
        }
        Verify.Check(a.Gate!.Phase == GatePhase.Open && b.Gate!.Phase == GatePhase.Open && a.Gate.ConnectionId == b.Gate.ConnectionId, "both endpoints establish the same connection: " + a.DialStatus);
        Verify.Check(incoming && opening, "incoming and source opening phases observed independently");
        var openedSource = a.AudioGates.Select(e => e.Gate).First(g => g.ConnectionId == a.Gate.ConnectionId && g.Phase == GatePhase.Opening);
        var openedReceiver = b.AudioGates.Select(e => e.Gate).First(g => g.ConnectionId == b.Gate!.ConnectionId && g.Phase == GatePhase.Opening);
        Verify.Check(a.Gate.PhaseTick - openedSource.PhaseTick == GateEffectTimeline.OpeningTicks, "source readiness follows the complete four-second opening");
        Verify.Check(b.Gate!.PhaseTick - openedReceiver.PhaseTick == GateEffectTimeline.OpeningTicks && openedReceiver.PhaseTick - openedSource.PhaseTick == GateEffectTimeline.IncomingLeadTicks, "receiver retains independent lead-in and four-second opening");
        Verify.Check(b.PreloadBytesReceived == 0 && b.Destination is null, "incoming client receives zero source-world preload bytes");
        Verify.Check(server.PrefetchLoadsReused == 1, "orb reuses the sixth-symbol destination instead of generating it again");
        if (hazard)
        {
            for (int i = 0; i < 500 && a.World.Definition.Props.Concat(b.World.Definition.Props).Any(p => hazardVictims.Contains(p.PersistentId)); i++)
                await Pump(20);
            var registry = a.World.Definition.Props.Concat(b.World.Definition.Props).ToArray();
            Verify.Check(hazardVictims.All(id => registry.All(p => p.PersistentId != id)), "both independent opening plumes durably destroy contained props");
            Verify.Check(hazardSurvivors.All(id => registry.Count(p => p.PersistentId == id) == 1), "rear-side props survive both opening endpoints");
            Verify.Check(a.Gate.Phase == GatePhase.Open && b.Gate.Phase == GatePhase.Open && a.Local.Id != 0 && b.Local.Id != 0, "hazard preserves gates and players");
            await Pump(3000);
            Verify.Check(!a.World.Definition.Props.Concat(b.World.Definition.Props).Any(p => hazardVictims.Contains(p.PersistentId)), "destroyed items do not return through subsequent snapshots");
            Console.WriteLine("KAWOOSH NETWORK evidence=" + directory);
            if (captureProcess is not null)
            {
                await Pump(2000);
                if (!captureProcess.HasExited)
                    captureProcess.Kill(entireProcessTree: true);
            }
            return;
        }
        if (travel)
        {
            async Task AbsorbWrongWay(ClientSession observer, Guid location, PropDefinition prop, float side)
            {
                var frame = GateFrame.From(observer.World.Definition.Entities.Single(e => e.Model == "stargate"));
                async Task Stage(float distance) => await server.InspectPhysicsAsync(location, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(body => body.Id == prop.Id ? body with { Position = frame.Centre + frame.Normal * (side * distance), Rotation = Quaternion.Identity, LinearVelocity = default, AngularVelocity = default, Sleeping = false } : body).ToArray(), Clips = [] }));
                // Walk the setup around the rim, not through the outgoing gate.
                await server.InspectPhysicsAsync(location, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(body => body.Id == prop.Id ? body with { Position = frame.World(new(6, 0, side * 1.2f)), Rotation = Quaternion.Identity, LinearVelocity = default, AngularVelocity = default, Sleeping = false } : body).ToArray(), Clips = [] }));
                await Pump(150);
                await Stage(1.2f);
                await Pump(150);
                await Stage(.15f);
                await Pump(120);
                var clip = observer.Physics.CurrentClips().FirstOrDefault(c => c.Body == prop.Id);
                Verify.Check(clip is not null && Vector3.Dot(clip.Gate.Normal, frame.Normal) * side > .99f, "network wrong-way prop clips on approach side");
                var state = observer.Physics.Bodies().Single(p => p.Id == prop.Id);
                Verify.Check(GateTravel.ClippedVertices(clip!.Gate, state, prop).All(v => clip.Gate.Distance(state.Position + Vector3.Transform(v, state.Rotation)) >= -.0001f), "hidden part of incoming prop is excluded by render/collision plane");
                // Continue through using the existing clip so its approach side
                // remains authoritative even after the centre crosses the plane.
                await server.InspectPhysicsAsync(location, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(body => body.Id == prop.Id ? body with { Position = frame.Centre - frame.Normal * (side * (prop.HalfExtent.Z + .01f)), LinearVelocity = default, AngularVelocity = default } : body).ToArray() }));
                for (int i = 0; i < 100 && observer.World.Definition.Props.Any(p => p.PersistentId == prop.PersistentId); i++)
                    await Pump(30);
                Verify.Check(observer.World.Definition.Props.All(p => p.PersistentId != prop.PersistentId), "fully consumed wrong-way prop is destroyed");
                await Pump(2500);
                Verify.Check(a.World.Definition.Props.Concat(b.World.Definition.Props).All(p => p.PersistentId != prop.PersistentId), "destroyed prop does not return or teleport to other endpoint");
            }
            foreach (var sourceProp in a.World.Definition.Props.Where(p => p.Id is 1 or 3).ToArray())
            {
                var sourceGate = GateFrame.From(a.World.Definition.Entities.Single(e => e.Model == "stargate"));
                await server.InspectPhysicsAsync(home, w =>
                {
                    var start = sourceGate.Centre + sourceGate.Normal * 1.1f;
                    if (!sourceProp.IsBarrel)
                        start.Y = w.World.SampleHeight(start.X, start.Z) + sourceProp.HalfExtent.Y + .035f;
                    var current = w.Snapshot();
                    var staged = current.Bodies.Select(body => body.Id == sourceProp.Id ? body with { Position = start, Rotation = sourceProp.IsBarrel ? Quaternion.CreateFromYawPitchRoll(.2f, .1f, 0) : Quaternion.Identity, LinearVelocity = -sourceGate.Normal * 4 + (sourceProp.IsBarrel ? Vector3.UnitY * 3 : Vector3.Zero), AngularVelocity = sourceProp.IsBarrel ? new(.2f, .4f, .1f) : Vector3.Zero, Sleeping = false } : body).ToArray();
                    w.Apply(current with
                    {
                        Bodies = staged
                    });
                });
                if (sourceProp.IsBarrel)
                {
                    // Reproduce the old gap between the empty-proxy threshold
                    // (+2 mm) and server capture (-20 mm), with no forward speed.
                    await Pump(80);
                    await server.InspectPhysicsAsync(home, w =>
                    {
                        var current = w.Snapshot();
                        var staged = current.Bodies.Select(body =>
                        {
                            if (body.Id != sourceProp.Id)
                                return body;
                            var stopped = body with
                            {
                                Position = sourceGate.Centre,
                                Rotation = Quaternion.Identity,
                                LinearVelocity = default,
                                AngularVelocity = default,
                                Sleeping = false
                            };
                            float depth = GateTravel.Extents(sourceGate, stopped, sourceProp).Max;
                            return stopped with
                            {
                                Position = stopped.Position - sourceGate.Normal * (depth - .001f)
                            };
                        }).ToArray();
                        w.Apply(current with
                        {
                            Bodies = staged
                        });
                    });
                }
                var flight = Stopwatch.StartNew();
                double capturedProp = -1;
                double nextPush = 0;
                while (flight.Elapsed.TotalSeconds < 15 && !b.World.Definition.Props.Any(p => p.PersistentId == sourceProp.PersistentId))
                {
                    if (!sourceProp.IsBarrel && capturedProp < 0 && flight.Elapsed.TotalSeconds >= nextPush)
                    {
                        nextPush = flight.Elapsed.TotalSeconds + .1;
                        await server.InspectPhysicsAsync(home, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(body => body.Id == sourceProp.Id && GateTravel.Extents(sourceGate, body, sourceProp).Max > GateTravel.PropCompletionDepth ? body with { LinearVelocity = -sourceGate.Normal * 2 + Vector3.UnitY * body.LinearVelocity.Y } : body).ToArray() }));
                    }
                    await Pump(20);
                    if (capturedProp < 0 && !a.World.Definition.Props.Any(p => p.PersistentId == sourceProp.PersistentId))
                        capturedProp = flight.Elapsed.TotalSeconds;
                }
                await server.InspectPhysicsAsync(home, w =>
                {
                    var body = w.Bodies().FirstOrDefault(b => b.Id == sourceProp.Id);
                    Console.WriteLine($"PROP TRACE {sourceProp.Kind}: {body} extents={GateTravel.Extents(sourceGate, body, sourceProp)} clips={w.CurrentClips().Length}");
                });
                Verify.Check(b.World.Definition.Props.Any(p => p.PersistentId == sourceProp.PersistentId) && a.World.Definition.Props.All(p => p.PersistentId != sourceProp.PersistentId), "prop transfers exactly once: " + sourceProp.Kind);
                var receiver = GateFrame.From(b.World.Definition.Entities.Single(e => e.Model == "stargate"));
                var received = b.World.Definition.Props.Single(p => p.PersistentId == sourceProp.PersistentId);
                Verify.Check(receiver.Distance(received.Position) >= sourceProp.HalfExtent.Length() + .19f, "network item materializes on receiver front side: " + sourceProp.Kind);
                Verify.Check(capturedProp >= 0 && flight.Elapsed.TotalSeconds - capturedProp >= 1.8, "prop spends at least two seconds in transit: " + sourceProp.Kind);
                await Pump(3000);
                await server.InspectPhysicsAsync(b.World.Definition.LocationId, w =>
                {
                    var definition = w.World.Definition.Props.Single(p => p.PersistentId == sourceProp.PersistentId);
                    var body = w.Bodies().Single(p => p.Id == definition.Id);
                    Verify.Check(body.Position.Y >= w.World.SampleHeight(body.Position.X, body.Position.Z) - .1f, "received prop remains above actual terrain: " + definition.Kind);
                    // Keep the later player-transfer test independent of the
                    // stationary barrel that now correctly occupies this exit.
                    var right = Vector3.Transform(Vector3.UnitX, GateFrame.From(w.World.Definition.Entities.Single(e => e.Model == "stargate")).Rotation);
                    w.Apply(w.Snapshot() with
                    {
                        Bodies = w.Bodies().Select(p => p.Id == body.Id ? p with { Position = p.Position + right * 6 + Vector3.UnitY * 2, LinearVelocity = default, AngularVelocity = default } : p).ToArray()
                    });
                });
                await AbsorbWrongWay(b, b.World.Definition.LocationId, received, sourceProp.IsBarrel ? -1 : 1);
            }
            if (!playerPush)
                await AbsorbWrongWay(a, home, a.World.Definition.Props.Single(p => p.Id == 2), -1);
            var gateFrame = GateFrame.From(a.World.Definition.Entities.Single(e => e.Model == "stargate"));
            var front = gateFrame.Centre + gateFrame.Normal * 3;
            front.Y = a.Local.Position.Y;
            for (int i = 0; i < 100 && Vector2.Distance(new(a.Local.Position.X, a.Local.Position.Z), new(front.X, front.Z)) > .2f; i++)
                await Pump(100, front);
            var pushedProp = playerPush ? a.World.Definition.Props.First(p => p.Kind == PropKind.WoodenCrate) : null;
            if (pushedProp is not null)
                await server.InspectPhysicsAsync(home, w =>
                {
                    var position = gateFrame.Centre + gateFrame.Normal * 1.6f;
                    position.Y = w.World.SampleHeight(position.X, position.Z) + pushedProp.HalfExtent.Y + .03f;
                    w.Apply(w.Snapshot() with
                    {
                        Bodies = w.Bodies().Select(body => body.Id == pushedProp.Id ? body with { Position = position, Rotation = Quaternion.Identity, LinearVelocity = default, AngularVelocity = default, Sleeping = false } : body).ToArray()
                    });
                });
            var rear = front - gateFrame.Normal * 6;
            var watch = Stopwatch.StartNew();
            double captured = -1;
            bool white = false;
            while (a.World.Definition.LocationId == home && watch.Elapsed.TotalSeconds < 45)
            {
                await Pump(20, rear);
                white |= a.TransitWhite >= .99;
                if (captured < 0 && a.Transit is not null)
                    captured = watch.Elapsed.TotalSeconds;
            }
            Console.WriteLine($"TRAVEL result: {a.TransitDiagnostic}, pos {a.Local.Position}, elapsed {watch.Elapsed.TotalSeconds:F3}");
            if (pushedProp is not null)
            {
                for (int wait = 0; wait < 250 && !b.World.Definition.Props.Any(p => p.PersistentId == pushedProp.PersistentId); wait++)
                    await Pump(20);
                Verify.Check(b.World.Definition.Props.Count(p => p.PersistentId == pushedProp.PersistentId) == 1, "continuous player capsule pushing transfers crate without scripted prop velocity");
            }
            Verify.Check(a.World.Definition.LocationId == b.World.Definition.LocationId && a.World.Definition.LocationId != home, "player arrives at reserved receiver");
            Verify.Check(a.PreloadPhysicsPromotions > 0, "player transfer adopts prebuilt destination collision");
            Verify.Check(a.Physics.TerrainCoordinates.Count <= 12, "arrival client does not inherit distant explorers collision regions");
            Verify.Check(white && captured >= 0 && watch.Elapsed.TotalSeconds - captured >= 1.9, "white transition waits at least two seconds");
            Verify.Check(b.Transit is null && b.TransitWhite == 0, "only traveler fades");
            a.DestinationFrameRendered(true);
            await Pump(200);
            Verify.Check(!a.TransitLocked && a.TransitWhite == 0, "complete destination frame releases white fade");
            var arrived = a.World.Definition.LocationId;
            a.Dispose();
            activeA = false;
            await Pump(350);
            var reconnecting = GameConnection.ConnectAsync("127.0.0.1", port, "RenamedTraveler", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity);
            while (!reconnecting.IsCompleted)
                await Pump(20);
            using var reconnect = new ClientSession(await reconnecting);
            Verify.Check(reconnect.World.Definition.LocationId == arrived, "authenticated reconnect restores destination after name change");
            return;
        }
        var joining = GameConnection.ConnectAsync("127.0.0.1", port, "LateGateObserver", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        while (!joining.IsCompleted)
            await Pump(20);
        using (var late = new ClientSession(await joining))
        {
            for (int i = 0; i < 50 && late.Gate is null; i++)
            {
                late.Poll();
                await Pump(20);
            }
            Verify.Check(late.Gate?.Phase == GatePhase.Open && late.Gate.ConnectionId == a.Gate.ConnectionId && late.GateConnection?.Id == a.Gate.ConnectionId, "late join receives current open state and connection without replaying activation");
        }
        await Press(0);
        for (int i = 0; i < 100 && (a.Gate.Phase != GatePhase.Idle || b.Gate?.Phase != GatePhase.Idle); i++)
            await Pump(50);
        Verify.Check(a.Gate.Phase == GatePhase.Idle && b.Gate!.Phase == GatePhase.Idle && a.Gate.Symbols.Length == 0, "orb closes both gates and clears sequence");
        Verify.Check(a.World.Definition.LocationId == home, "opening and closing never teleport source player");
        Verify.Check(a.Destination is { Stage: PreloadStage.Ready }, "completed cache retained after closure");
        Console.WriteLine($"PRELOAD bytes={a.PreloadBytesReceived} cpu={a.Destination?.Bytes} prepare_ms={a.Destination?.Milliseconds:F1}; incoming_bytes={b.PreloadBytesReceived}");
        foreach (int key in a.ReturnAddress.Concat(new[] { 1, 0 }).ToArray())
            await Press(key);
        Verify.Check(a.Gate.Phase == GatePhase.Idle && a.Gate.Symbols.Length == 0 && a.Gate.Owner == 0 && a.Gate.ConnectionId == Guid.Empty && a.DialStatus.Contains("itself"), "self-dial resets gate and symbols while preserving failure reason");
        await Press(28);
        a.Dial("reset", 0, Aim(0));
        while (a.DialPending)
            await Pump(20);
        Verify.Check(a.Gate.Phase == GatePhase.Idle && a.Gate.Owner == 0, "reset releases exclusive dial session");
        await Press(28);
        await Pump(2500, target + new Vector3(0, 0, 9));
        await Pump(2200);
        Verify.Check(a.Gate.Phase == GatePhase.Idle && a.Gate.Symbols.Length == 0, "leaving DHD reach for two seconds releases session");
        for (int i = 0; i < 100 && Vector2.Distance(new(a.Local.Position.X, a.Local.Position.Z), new(target.X, target.Z)) > .25f; i++)
            await Pump(100, target);
        await Pump(500);
        b.PrepareGateVisuals = _ => false;
        foreach (int key in new[] { 28, 26, 5, 36, 11, 29, 1, 0 })
            await Press(key);
        for (int i = 0; i < 200 && (a.Gate.Phase != GatePhase.Open || b.Gate?.Phase != GatePhase.Open); i++)
            await Pump(50);
        Verify.Check(a.Gate.Phase == GatePhase.Open && b.Gate!.Phase == GatePhase.Open, "visual-readiness ACKs do not delay opening");
        await Press(0);
        for (int i = 0; i < 100 && a.Gate.Phase != GatePhase.Idle; i++)
            await Pump(50);
        Verify.Check(a.Gate.Phase == GatePhase.Idle && server.ActiveConnectionCount == 0, "connection closes without visual readiness");
        b.PrepareGateVisuals = _ => true;
        foreach (int key in new[] { 28, 26, 5, 36, 11, 29, 1, 0 })
            await Press(key);
        for (int i = 0; i < 200 && (a.Gate.Phase != GatePhase.Open || b.Gate!.Phase != GatePhase.Open); i++)
            await Pump(50);
        Verify.Check(a.Gate.Phase == GatePhase.Open && b.Gate!.Phase == GatePhase.Open, "new connection opens after closure");
        var homeAddress = a.ReturnAddress;
        for (int i = 0; i < 300 && a.Destination?.Stage != PreloadStage.Ready; i++)
            await Pump(50);
        int promotions = a.PreloadPromotions;
        a.InspectDestination([28, 26, 5, 36, 11, 29]);
        for (int i = 0; i < 600 && a.World.Definition.LocationId == home; i++)
            await Pump(50);
        Verify.Check(a.World.Definition.LocationId == b.World.Definition.LocationId && a.PreloadPromotions == promotions + 1, "developer relocation reuses prepared canonical cache");
        Verify.Check(a.Physics.Players.Select(p => p.Id).Distinct().Count() == a.Physics.Players.Count(), "cache activation does not duplicate actors");
        activeA = false;
        a.Dispose();
        await Pump(1000);
        Verify.Check(b.Gate!.Phase == GatePhase.Open && server.ActiveConnectionCount == 1, "established connection survives dialer disconnect");
        dhd = b.World.Definition.Entities.Single(e => e.Model == "dhd");
        target = dhd.Position + new Vector3(0, 0, 1.1f);
        for (int i = 0; i < 100 && Vector2.Distance(new(b.Local.Position.X, b.Local.Position.Z), new(target.X, target.Z)) > .25f; i++)
            await Pump(100, secondTarget: target);
        await Pump(500);
        b.Dial("press", 0, Aim(0, b));
        while (b.DialPending)
            await Pump(20);
        for (int i = 0; i < 100 && b.Gate?.Phase != GatePhase.Idle; i++)
            await Pump(50);
        Verify.Check(b.Gate!.Phase == GatePhase.Idle && server.ActiveConnectionCount == 0, "receiver orb closes established connection");
        var closed = b.AudioGates.Select(e => e.Gate).Last(g => g.Phase == GatePhase.Closing);
        Verify.Check(b.Gate.PhaseTick - closed.PhaseTick == GateEffectTimeline.ClosingTicks, "connection release follows the complete shutdown-sound closing");
        b.InspectDestination(homeAddress);
        for (int i = 0; i < 600 && b.World.Definition.LocationId != home; i++)
            await Pump(100);
        Verify.Check(b.World.Definition.LocationId == home, "developer returns to retained home after closure");
        await Pump(32000);
        Verify.Check(server.LoadedLocationCount == 1, "unreferenced destination saved and unloaded after reuse grace period");
        if (expiry)
        {
            dhd = b.World.Definition.Entities.Single(e => e.Model == "dhd");
            target = dhd.Position + new Vector3(0, 0, 1.1f);
            for (int i = 0; i < 100 && Vector2.Distance(new(b.Local.Position.X, b.Local.Position.Z), new(target.X, target.Z)) > .25f; i++)
                await Pump(100, secondTarget: target);
            await Pump(500);
            foreach (int key in new[] { 28, 26, 5, 36, 11, 29, 1, 0 })
            {
                b.Dial("press", key, Aim(key, b));
                for (int i = 0; i < 150 && b.DialPending; i++)
                    await Pump(20);
            }
            for (int i = 0; i < 400 && b.Gate!.Phase != GatePhase.Open; i++)
                await Pump(100);
            Verify.Check(b.Gate!.Phase == GatePhase.Open && server.LoadedLocationCount == 2, "unloaded destination rematerializes for a new connection");
            long opened = b.Gate.PhaseTick;
            for (int i = 0; i < 1400 && b.Gate.Phase != GatePhase.Idle; i++)
                await Pump(100);
            Verify.Check(b.Gate.Phase == GatePhase.Idle && b.Gate.PhaseTick >= opened + 7200 + GateEffectTimeline.ClosingTicks && server.ActiveConnectionCount == 0, "120-second expiry closes both endpoints after the shutdown-sound closing phase");
        }
    }
}
