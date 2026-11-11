using System.Numerics;
using System.Security.Cryptography;
using StargateGame.Shared;
using StargateGame.Physics;

static class TravelChecks
{
    public static async Task IdentityNetwork()
    {
        if (OperatingSystem.IsWindows())
        {
            string profile = "travel-" + Guid.NewGuid().ToString("N")[..16];
            var keys = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                using var k = StargateGame.Client.DeviceIdentity.Load(profile);
                return k.Fingerprint;
            })));
            Verify.Check(keys.Distinct().Count() == 1, "atomic DPAPI identity creation across concurrent launches");
        }
        await using var server = new StargateGame.Server.GameServer(System.Net.IPAddress.Loopback, 0, 8);
        server.Start();
        using var identity = new PlayerIdentity();
        var connection = await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Original", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity);
        Guid id = connection.Welcome.PersistentPlayerId;
        using var client = new StargateGame.Client.ClientSession(connection);
        bool rejected = false;
        try
        {
            using var duplicate = await GameConnection.ConnectAsync("127.0.0.1", server.Port, "OtherName", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity);
        }
        catch (IOException)
        {
            rejected = true;
        }
        client.Poll();
        Verify.Check(rejected && client.Failure is null, "duplicate identity rejected without disconnecting first session");
        using var testIdentity = new PlayerIdentity("explicit-test");
        rejected = false;
        try
        {
            using var test = await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Test", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: testIdentity);
        }
        catch (IOException)
        {
            rejected = true;
        }
        Verify.Check(rejected, "test identity requires server opt-in");
        client.Dispose();
        await Task.Delay(200);
        using var reconnected = await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Renamed", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity);
        Verify.Check(id != Guid.Empty && reconnected.Welcome.PersistentPlayerId == id, "authenticated persistent identity survives reconnect and rename");
    }
    public static async Task Persistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "StargateTravel-" + Guid.NewGuid());
        var definition = new StargateGame.WorldGen.PlanetPlan(7319, "temperate", settlements: new SettlementSettings("wilderness", "auto")).Definition;
        Guid source, destination;
        StargateGame.Persistence.SavedPlayer player;
        PropDefinition prop;
        BodyState body;
        using var key = new PlayerIdentity();
        using var replacement = new PlayerIdentity();
        await using (var db = new StargateGame.Persistence.WorldRepository(path))
        {
            var a = await db.CreateAsync(definition);
            var b = await db.CreateAsync(definition);
            source = a.Id;
            destination = b.Id;
            player = await db.AuthenticatePlayerAsync(key.Fingerprint, source, "Original");
            Verify.Check((await db.AuthenticatePlayerAsync(key.Fingerprint, source, "Renamed")).Id == player.Id, "display-name changes retain identity");
            prop = a.Definition.Props[0];
            body = new(prop.Id, prop.Position, Quaternion.Identity, default, default, false);
            var bodies = a.Definition.Props.Select(p => new BodyState(p.Id, p.Position, Quaternion.Identity, default, default, false)).ToArray();
            var capture = new StargateGame.Persistence.TransitRecord(Guid.NewGuid(), Guid.NewGuid(), source, destination, null, prop, body);
            await db.CaptureTransitAsync(capture, bodies.Where(b => b.Id != prop.Id).ToArray(), a.Definition.Props.Where(p => p.Id != prop.Id).ToArray());
        }
        await using (var db = new StargateGame.Persistence.WorldRepository(path))
        {
            var a = await db.LoadAsync(source);
            Verify.Check(a.Definition.Props.Count(p => p.PersistentId == prop.PersistentId) == 1, "captured prop recovers exactly once on restart");
            var capture = new StargateGame.Persistence.TransitRecord(Guid.NewGuid(), Guid.NewGuid(), source, destination, null, prop, body);
            await db.CaptureTransitAsync(capture, a.Bodies.Where(b => b.Id != prop.Id).ToArray(), a.Definition.Props.Where(p => p.Id != prop.Id).ToArray());
            var b = await db.LoadAsync(destination);
            var transferred = prop with
            {
                Id = 5
            };
            var existing = b.Definition.Props.Select(p => new BodyState(p.Id, p.Position, Quaternion.Identity, default, default, false));
            await db.CommitPropTransitAsync(capture.Id, destination, existing.Append(body with
            {
                Id = 5
            }).ToArray(), b.Definition.Props.Append(transferred).ToArray());
            await db.SaveCurrentBodiesAsync(source, a.Bodies, a.Definition.Props);
            Verify.Check((await db.LoadAsync(source)).Definition.Props.All(p => p.PersistentId != prop.PersistentId), "stale source autosave cannot resurrect transferred item");
            await db.SaveCurrentBodiesAsync(destination, b.Bodies, b.Definition.Props);
            Verify.Check((await db.LoadAsync(destination)).Definition.Props.Count(p => p.PersistentId == prop.PersistentId) == 1, "stale receiver autosave preserves newly arrived ownership");
            var playerCapture = new StargateGame.Persistence.TransitRecord(Guid.NewGuid(), Guid.NewGuid(), source, destination, player, null, null);
            await db.CaptureTransitAsync(playerCapture, [], []);
            await db.CommitPlayerTransitAsync(playerCapture.Id, player with
            {
                Location = destination
            });
        }
        await using (var db = new StargateGame.Persistence.WorldRepository(path))
        {
            Verify.Check((await db.LoadAsync(source)).Definition.Props.All(p => p.PersistentId != prop.PersistentId) && (await db.LoadAsync(destination)).Definition.Props.Count(p => p.PersistentId == prop.PersistentId) == 1, "committed ownership survives restart without duplicate");
            Verify.Check((await db.AuthenticatePlayerAsync(key.Fingerprint, source, "Renamed")).Location == destination, "reconnect retains committed destination");
            await db.RebindPlayerAsync(player.Id, replacement.Fingerprint);
            Verify.Check(await db.DeviceRevokedAsync(key.Fingerprint), "administrator revokes previous key");
            Verify.Check((await db.AuthenticatePlayerAsync(replacement.Fingerprint, source, "Recovered")).Id == player.Id, "replacement key retains player identity");
            bool rejected = false;
            try
            {
                await db.AuthenticatePlayerAsync(key.Fingerprint, source, "OldKey");
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Verify.Check(rejected, "revoked key cannot create a new identity");
            await db.DestroyPropAsync(destination, prop.PersistentId);
            await db.DestroyPropAsync(destination, prop.PersistentId);
            Verify.Check((await db.LoadAsync(destination)).Definition.Props.All(p => p.PersistentId != prop.PersistentId), "wrong-way destruction is durable and idempotent");
            await db.SaveBodiesAsync(source, [], []);
        }
        await using (var db = new StargateGame.Persistence.WorldRepository(path))
        {
            Verify.Check((await db.LoadAsync(source)).Definition.Props.Length == 0, "empty world never regenerates starter props");
            Verify.Check((await db.LoadAsync(destination)).Definition.Props.All(p => p.PersistentId != prop.PersistentId), "destroyed transferred prop cannot respawn after restart");
        }
        Console.WriteLine("Travel persistence evidence: " + path);
    }
    public static void Run()
    {
        var fixture = GraphicsFixture.Create("gate-pair");
        var terrain = new StargateGame.WorldGen.TerrainStore(fixture.SettingsHash);
        foreach (var c in GraphicsFixture.Chunks)
            terrain.Put(GraphicsFixture.BuildChunk(fixture, c), false);
        using (var fixtureWorld = new PhysicsWorld(fixture, true, terrain, GraphicsFixture.Chunks))
        {
            var spawn = fixtureWorld.World.Spawn(0);
            Verify.Check(fixtureWorld.CanPlacePlayer(spawn), "fixture spawn passes authoritative clearance");
            fixtureWorld.AddStaticBox(spawn + new Vector3(3, 1, 0), new(.5f, 1, .5f));
            Verify.Check(!fixtureWorld.CanPlacePlayer(spawn + new Vector3(3, 0, 0)) && fixtureWorld.CanPlacePlayer(spawn), "clearance checks translated geometry at the intended position");
            var testPosition = spawn + new Vector3(-4, 0, 0);
            testPosition.Y = fixtureWorld.World.SampleHeight(testPosition.X, testPosition.Z) + .2f;
            var groundProp = new PropDefinition(64, testPosition, new(.5f), 10) { PersistentId = Guid.NewGuid() };
            var buried = new BodyState(64, testPosition, Quaternion.Identity, default, default, false);
            Verify.Check(!fixtureWorld.CanPlaceProp(groundProp, buried), "mapped ground-level arrival can intersect receiving bank");
            Verify.Check(fixtureWorld.TryPlacePropExit(groundProp, buried, out var lifted) && lifted.Position.Y > buried.Position.Y && lifted.Position.X == buried.Position.X && lifted.Position.Z == buried.Position.Z, "receiver lifts full hull above terrain without changing tangential or forward offset");
            fixtureWorld.AddStaticBox(lifted.Position, new(.6f));
            Verify.Check(!fixtureWorld.TryPlacePropExit(groundProp, buried, out _), "terrain correction does not bypass occupied exits");
        }
        var position = new Vector3(-511.12345f, 123.12345f, -511.12345f);
        var rotation = Quaternion.Normalize(new(.12345678f, .23456789f, .34567891f, .45678912f));
        var registry = Enumerable.Range(1, 64).Select(id => PropDefinition.Barrel(id, position, false) with { PersistentId = Guid.NewGuid() }).ToArray();
        var maximum = new PhysicsSnapshot(long.MaxValue, 99999,
            Enumerable.Range(1, 8).Select(id => new PlayerState(id, new string('x', 24), position, position, 1, 1, true, long.MaxValue, ArrivalMomentum: position)).ToArray(),
            registry.Select(p => new BodyState(p.Id, position, rotation, position, position, false)).ToArray(), [], registry,
            registry.Select(p => new PropClip(p.Id, new(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.2345f), 3), PropClipMode.Emergence, Guid.NewGuid())).ToArray());
        maximum = maximum with
        {
            Commands = maximum.Players.Select(p => new AppliedCommand(p.Id, new(1, 0, 0, 1, 1, false, false), 0)).ToArray()
        };
        PhysicsWorld.Validate(maximum, fixture);
        bool invalidClip = false;
        try
        {
            PhysicsWorld.Validate(maximum with
            {
                TravelGate = new(position, Quaternion.Identity, float.NaN)
            }, fixture);
        }
        catch (InvalidDataException)
        {
            invalidClip = true;
        }
        Verify.Check(invalidClip, "invalid clipping frame rejected before native queries");
        int bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new Message { Kind = "snapshot", State = maximum }, Protocol.Json).Length;
        Verify.Check(bytes <= Protocol.MaxFrameBytes, $"full prop registry and clipping fit bounded frame: {bytes} bytes");
        using var identity = new PlayerIdentity();
        var challenge = new IdentityChallenge(Guid.NewGuid(), Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), Protocol.Version);
        var signature = identity.Sign(challenge);
        Verify.Check(PlayerIdentity.Verify(identity.PublicKey, challenge, signature), "P256 identity proof");
        Verify.Check(!PlayerIdentity.Verify(identity.PublicKey, challenge with
        {
            Connection = Guid.NewGuid()
        }, signature), "proof bound to connection");
        Verify.Check(!PlayerIdentity.Verify(identity.PublicKey, challenge with
        {
            Universe = Guid.NewGuid()
        }, signature), "proof bound to universe");
        using var restored = new PlayerIdentity(identity.ExportPrivateKey());
        Verify.Check(restored.Fingerprint == identity.Fingerprint, "identity survives key reload");
        var gate = new GateFrame(new(0, 2, 0), Quaternion.Identity, 3);
        Verify.Check(GateTravel.PlayerCrossing(gate, new(0, 1, 1), new(0, 1, 0)), "front capsule captures");
        Verify.Check(!GateTravel.PlayerCrossing(gate, new(0, 1, -1), new(0, 1, 1)), "rear capsule passes through");
        Verify.Check(!GateTravel.PlayerCrossing(gate, new(3, 1, 1), new(3, 1, 0)), "rim rejects capsule");
        Verify.Check(!GateTravel.PlayerCrossing(gate, new(0, 1, 20), new(0, 1, -1)), "correction suppresses capture");
        var prop = new PropDefinition(1, new(0, 2, .2f), new(.5f), 10) { PersistentId = Guid.NewGuid() };
        var body = new BodyState(1, prop.Position, Quaternion.CreateFromYawPitchRoll(.4f, .2f, .1f), new(2, 1, -3), new(.2f, 1, .4f), false);
        var vertices = GateTravel.ClippedVertices(gate, body, prop);
        Verify.Check(vertices.Length >= 4 && vertices.All(v => gate.Distance(body.Position + Vector3.Transform(v, body.Rotation)) > -.00001f), "rotated box clipped to source half-space");
        var target = new GateFrame(new(25, 2, -10), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f), 3);
        var exit = GateTravel.Exit(gate, target, body, prop);
        Verify.Near(exit.LinearVelocity.Length(), body.LinearVelocity.Length(), .0001f, "linear speed preserved");
        Verify.Near(exit.AngularVelocity.Length(), body.AngularVelocity.Length(), .0001f, "spin preserved");
        Verify.Near(GateTravel.Extents(target, exit, prop).Max, .02f, .0001f, "leading edge emerges at receiver");
        foreach (float sourceYaw in new[] { 0f, .7f, MathF.PI })
            foreach (float destinationYaw in new[] { 0f, -1.1f, MathF.PI })
                foreach (bool barrel in new[] { false, true })
                {
                    var from = new GateFrame(new(4, 3, -7), Quaternion.CreateFromAxisAngle(Vector3.UnitY, sourceYaw), 3);
                    var to = new GateFrame(new(-15, 5, 20), Quaternion.CreateFromAxisAngle(Vector3.UnitY, destinationYaw), 3);
                    var item = barrel ? PropDefinition.Barrel(1, default, false) : prop;
                    var entry = new BodyState(1, from.World(new(.25f, -.1f, -item.HalfExtent.Z)), from.Rotation,
                        Vector3.Transform(new Vector3(.4f, -1, 2), from.Rotation), Vector3.Transform(new Vector3(12, 0, 0), from.Rotation), false);
                    var arrival = GateTravel.Exit(from, to, entry, item);
                    Verify.Near(GateTravel.Extents(to, arrival, item).Max, .02f, .0001f, "leading edge at oriented receiver");
                    Verify.Near(Vector3.Distance(arrival.LinearVelocity, Vector3.Transform(entry.LinearVelocity, from.MappingTo(to))), 0, .0001f, "exact transformed velocity including reverse component");
                    Verify.Near(arrival.LinearVelocity.Length(), entry.LinearVelocity.Length(), .0001f, "outward item mapping preserves speed");
                    var turned = arrival with
                    {
                        Rotation = Quaternion.Normalize(to.Rotation * Quaternion.CreateFromYawPitchRoll(.8f, .8f, .8f))
                    };
                    Verify.Check(GateTravel.Extents(to, turned, item).Min < 0, "partially emitted rotating hull remains behind sheet");
                    Verify.Near(to.Local(arrival.Position).X, -.25f, .0001f, "item keeps mapped tangential entry offset");
                }
        using var world = new PhysicsWorld(PlanetDefinition.Default with
        {
            Entities = [],
            Props = [prop]
        }, false);
        world.InsertProp(prop, body);
        var snap = world.Snapshot();
        world.Apply(snap with
        {
            Bodies = [body]
        });
        world.TravelGate = gate;
        world.BeginPropEntry(1, gate);
        var clipped = world.Bodies().Single();
        Verify.Near(Vector3.Distance(clipped.Position, body.Position), 0, .00001f, "proxy keeps body reference position");
        Verify.Near(Vector3.Distance(clipped.LinearVelocity, body.LinearVelocity), 0, .00001f, "proxy keeps velocity");
        Verify.Near(Vector3.Distance(clipped.AngularVelocity, body.AngularVelocity), 0, .00001f, "proxy keeps angular velocity");
        using (var checkpoint = world.Save())
        {
            world.Apply(world.Snapshot() with
            {
                Bodies = [body with { Position = new(0, 2, 2) }],
                Clips = []
            });
            Verify.Check(world.CurrentClips().Length == 0, "withdrawal restores original collider");
            world.Restore(checkpoint);
            Verify.Check(world.CurrentClips().Length == 1, "rollback restores clipped collider");
            world.Step();
        }
        world.RemoveProp(1);
        Verify.Check(world.Bodies().Length == 0 && world.World.Definition.Props.Length == 0, "empty prop registry remains empty");
        world.InsertProp(prop, body);
        Verify.Check(world.Bodies().Length == 1, "dynamic prop insertion");
        world.AddPlayer(new(1, "Traveler", new(4, 2, 0), new(2, 1, -3), 0, 0, false, 0));
        world.FreezePlayer(1, true);
        world.Step();
        Verify.Check(world.Player(1).TransitFrozen, "captured player remains frozen");
        world.RemovePlayer(1);
        foreach (bool barrel in new[] { false, true })
        {
            var definition = barrel ? PropDefinition.Barrel(1, new(0, 4, .2f), false) : prop with
            {
                Position = new(0, 4, .2f)
            };
            using var clippedWorld = new PhysicsWorld(PlanetDefinition.Default with
            {
                Entities = [],
                Props = []
            }, false);
            var moving = new BodyState(1, definition.Position, Quaternion.Identity, new(0, 0, -2), new(.1f, .2f, .1f), false);
            clippedWorld.InsertProp(definition, moving);
            clippedWorld.AddStaticBox(new(0, 0, -.35f), new(8, 30, .15f));
            clippedWorld.TravelGate = gate;
            clippedWorld.BeginPropEntry(1, gate);
            for (int i = 0; i < 40; i++)
                clippedWorld.Step();
            var completed = clippedWorld.Bodies().Single();
            Verify.Check(GateTravel.Extents(gate, completed, definition).Max <= GateTravel.PropCompletionDepth, "hidden " + (barrel ? "barrel" : "crate") + " passes rear wall and reaches transfer plane");
            for (int i = 0; i < 60; i++)
                clippedWorld.Step();
            Verify.Check(clippedWorld.Bodies().Single() == completed, "completed empty proxy cannot fall while awaiting server transfer");
            Verify.Check(completed.LinearVelocity.Z < -1, "completed proxy retains entry momentum");
            using (var completedCheckpoint = clippedWorld.Save())
            {
                clippedWorld.Apply(clippedWorld.Snapshot() with
                {
                    Bodies = [moving],
                    Clips = []
                });
                clippedWorld.Restore(completedCheckpoint);
                clippedWorld.Step();
                Verify.Check(clippedWorld.Bodies().Single() == completed, "rollback restores pending completed prop without gravity drift");
            }
            var arrived = GateTravel.Exit(gate, target, completed, definition);
            using var receivingWorld = new PhysicsWorld(PlanetDefinition.Default with
            {
                Entities = [],
                Props = []
            }, false);
            receivingWorld.AddStaticBox(target.Centre - new Vector3(0, 3, 0), new(20, .5f, 20));
            receivingWorld.InsertProp(definition, arrived);
            receivingWorld.BeginPropEmergence(definition.Id, target, Guid.NewGuid());
            for (int i = 0; i < 180; i++)
                receivingWorld.Step();
            Verify.Check(receivingWorld.Bodies().Single().Position.Y > target.Centre.Y - 2.6f, "transferred " + (barrel ? "barrel" : "crate") + " restores full collider and lands on receiver ground");
        }
        foreach (float side in new[] { 1f, -1f })
        {
            var entry = gate with
            {
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, side < 0 ? MathF.PI : 0)
            };
            using var receiverWorld = new PhysicsWorld(PlanetDefinition.Default with
            {
                Entities = [],
                Props = []
            }, false);
            var entering = body with
            {
                Position = gate.Centre + entry.Normal * .2f,
                Rotation = Quaternion.Identity,
                LinearVelocity = -entry.Normal * 3,
                AngularVelocity = default
            };
            receiverWorld.InsertProp(prop, entering);
            receiverWorld.BeginPropEntry(prop.Id, entry);
            for (int i = 0; i < 30; i++)
                receiverWorld.Step();
            Verify.Check(receiverWorld.TravelGate is null && receiverWorld.CurrentClips().Single().Gate == entry, "receiving/rear proxy retains its own plane without enabling player travel");
            Verify.Check(GateTravel.Extents(entry, receiverWorld.Bodies().Single(), prop).Max <= GateTravel.PropCompletionDepth, "wrong-way proxy fully consumes from either side without falling");
        }
        var released = GateTravel.Exit(gate, target, new PlayerState(2, "Arrival", new(0, 1, .2f), new(2, 3, -7), 0, 0, false, 0));
        world.AddPlayer(released);
        Verify.Near(Vector3.Distance(world.Player(2).Velocity, released.Velocity), 0, .0001f, "player exact release velocity");
        world.Step();
        Verify.Near(new Vector2(world.Player(2).Velocity.X, world.Player(2).Velocity.Z).Length(), new Vector2(released.Velocity.X, released.Velocity.Z).Length(), .001f, "first movement step retains horizontal arrival momentum");
        world.RemovePlayer(2);
    }
}
