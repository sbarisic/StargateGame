using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.Client;
using StargateGame.Physics;
using StargateGame.Server;
using StargateGame.Shared;

static class FixtureGateChecks
{
    public static async Task Run()
    {
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, fixture: "gate-pair", devPlanets: true, allowTestIdentities: true);
        server.Start();
        using var identity = new PlayerIdentity();
        using var a = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Fixture tester", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity));
        var world = a.World.Definition;
        var id = world.LocationId;
        var gateA = world.Entities.Single(e => e.Id == "gate-a");
        var gateB = world.Entities.Single(e => e.Id == "gate-b");
        var frameA = GateFrame.From(gateA);
        var frameB = GateFrame.From(gateB);
        ClientSession? observer = null;
        async Task Pump(int ms, float forward = 0)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < ms)
            {
                a.Poll();
                observer?.Poll();
                if (a.Failure is not null || observer?.Failure is not null)
                    throw new Exception(a.Failure ?? observer?.Failure);
                a.Step(0, forward, 0, 0, false, false);
                observer?.Step(0, 0, 0, 0, false, false);
                if (a.AwaitingArrivalFrame)
                    a.DestinationFrameRendered(true);
                await Task.Delay(5);
            }
        }
        async Task Until(Func<bool> ready, string label, int seconds = 15)
        {
            var w = Stopwatch.StartNew();
            while (!ready() && w.Elapsed.TotalSeconds < seconds)
                await Pump(20);
            Verify.Check(ready(), label);
        }
        async Task AtDhd(string suffix)
        {
            var dhd = world.Entities.Single(e => e.Id == "dhd-" + suffix);
            await server.InspectPhysicsAsync(id, w => w.Apply(w.Snapshot() with { Players = w.Players.Select(p => p.Id == a.Local.Id ? p with { Position = dhd.Position + new Vector3(0, .05f, 1.2f), Velocity = default, TransitFrozen = false } : p).ToArray() }));
            await Pump(350);
            a.SelectDhd(dhd.Id);
        }
        Vector3 Aim(int key)
        {
            var dhd = world.Entities.Single(e => GraphicsFixture.EntityId(world, e.Id) == a.Gate!.DhdId);
            var eye = a.LastSnapshot.Players.Single(p => p.Id == a.Local.Id).Position + Vector3.UnitY * Movement.EyeHeight;
            foreach (var face in DhdInteraction.Surfaces.Where(f => f.Key == key).OrderByDescending(f => Math.Abs(Vector3.Cross(f.B - f.A, f.C - f.A).Y)))
            {
                var aim = Vector3.Normalize(Vector3.Transform((face.A + face.B + face.C) / 3, dhd.Transform) - eye);
                if (DhdInteraction.Pick(dhd, eye, aim)?.Key == key)
                    return aim;
            }
            throw new Exception("No visible button " + key);
        }
        async Task Press(int key)
        {
            a.Dial("press", key, Aim(key));
            await Until(() => !a.DialPending, "DHD input acknowledged");
            Verify.Check(a.LastDhdResult?.Accepted == true, "DHD accepts key " + key + ": " + a.DialStatus);
        }
        async Task Open(string suffix, int[] address)
        {
            await AtDhd(suffix);
            foreach (var key in address)
                await Press(key);
            await Press(0);
            await Until(() => a.Gates.Count == 2 && a.Gates.Values.All(g => g.Settled), "both independent endpoints open");
        }
        await Pump(400);
        Verify.Check(a.Gates.Count == 2 && a.Gates.Values.All(g => g.Phase == GatePhase.Idle), "fixture joins with two idle endpoint baselines");
        Verify.Check(Vector3.Distance(gateA.Position, gateB.Position) == 12 && gateA.Rotation == gateB.Rotation, "test gates side by side at the same elevation and facing");
        await Open("a", [2, 3, 4, 5, 6, 7]);
        Verify.Check(a.Gates[GraphicsFixture.EntityId(world, "gate-b")].Incoming, "arbitrary six-symbol dial from A reaches B");
        var crate = world.Props.Single(p => !p.IsBarrel);
        var barrel = world.Props.Single(p => p.IsBarrel);
        async Task Send(PropDefinition prop, GateFrame from, GateFrame to)
        {
            await server.InspectPhysicsAsync(id, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(b => b.Id == prop.Id ? b with { Position = from.Centre + from.Normal * 1.3f, LinearVelocity = -from.Normal * 6 + Vector3.UnitY * 2, AngularVelocity = default, Rotation = Quaternion.Identity, Sleeping = false } : b).ToArray() }));
            await Until(() => a.LastSnapshot.Registry?.All(p => p.PersistentId != prop.PersistentId) == true, "full capture removes source prop");
            await Until(() => a.LastSnapshot.Registry?.Any(p => p.PersistentId == prop.PersistentId) == true, "item arrives without players at receiver");
            var body = a.LastSnapshot.Bodies.Single(b => b.Id == prop.Id);
            Verify.Check(Vector3.Distance(body.Position, to.Centre) < 5 && GateTravel.Extents(to, body, prop).Max >= 0, "item emerges in front of the correct receiver");
            Verify.Check(a.LastSnapshot.Registry!.Count(p => p.PersistentId == prop.PersistentId) == 1, "exactly one persistent item after fixture transfer");
            await Until(() => a.LastSnapshot.Clips?.All(c => c.Body != prop.Id) == true, "prop finishes physical emergence");
        }
        await Send(crate, frameA, frameB);
        observer = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Late observer", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        try
        {
            await Pump(400);
            Verify.Check(observer.Gates.Count == 2 && observer.Gates.Values.All(g => g.Settled), "late join sees both active endpoints");
            await server.InspectPhysicsAsync(id, w =>
            {
                var pose = GateTravel.Exit(frameA, frameB, new BodyState(crate.Id, frameA.Centre, Quaternion.Identity, default, default, false), crate);
                w.Apply(w.Snapshot() with
                {
                    Bodies = w.Bodies().Select(b => b.Id == crate.Id ? pose : b).ToArray()
                });
                w.BeginPropEmergence(crate.Id, frameB, a.Gate!.ConnectionId);
                w.HoldProp(crate.Id);
            });
            await AtDhd("a");
            await Press(0);
            await Pump(800);
            Verify.Check(a.Gates.Values.All(g => g.Settled), "manual closure waits for stalled receiving prop");
            Verify.Check(observer.LastSnapshot.Clips?.Any(c => c.Body == crate.Id && c.Mode == PropClipMode.Emergence) == true, "second client receives stalled emergence state");
            await server.InspectPhysicsAsync(id, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(b => b.Id == crate.Id ? b with { Position = frameB.Centre - frameB.Normal * 2 } : b).ToArray() }));
            await Until(() => a.LastSnapshot.Registry?.All(p => p.Id != crate.Id) == true, "complete reversal destroys receiving prop");
            await Until(() => a.Gates.Values.All(g => g.Phase == GatePhase.Idle), "both endpoints close after stalled prop clears");
            await Open("b", [8, 9, 10, 11, 12, 14]);
            await Send(barrel, frameB, frameA);
            await server.InspectPhysicsAsync(id, w => w.Apply(w.Snapshot() with { Bodies = w.Bodies().Select(b => b.Id == barrel.Id ? b with { Position = frameA.Centre + frameA.Normal * 1.3f, LinearVelocity = -frameA.Normal * 6 + Vector3.UnitY * 2, Sleeping = false } : b).ToArray() }));
            await Until(() => a.LastSnapshot.Registry?.All(p => p.PersistentId != barrel.PersistentId) == true, "receiving-side wrong-way entry destroys item");
            await Pump(2300);
            Verify.Check(a.LastSnapshot.Registry!.All(p => p.PersistentId != barrel.PersistentId), "destroyed item does not respawn");
            await server.InspectPhysicsAsync(id, w => w.Apply(w.Snapshot() with { Players = w.Players.Select(p => p.Id == a.Local.Id ? p with { Position = new Vector3(gateB.Position.X, .05f, 2), Velocity = default } : p).ToArray() }));
            await Pump(250);
            await Pump(1300, 1);
            await Until(() => Math.Abs(a.Local.Position.X - gateA.Position.X) < 2 && !a.TransitLocked, "player transfers from B to A in the same world", 10);
            Verify.Check(a.World.Definition.LocationId == id && server.LoadedLocationCount == 1, "fixture travel needs no galaxy or second world load");
            await Pump(2000);
            Verify.Check(a.Failure is null, "movement stream remains valid after fixture arrival");
            await server.InspectPhysicsAsync(id, w =>
            {
                var pose = GateTravel.Exit(frameB, frameA, new BodyState(crate.Id, frameB.Centre, Quaternion.Identity, default, default, false), crate);
                w.InsertProp(crate, pose);
                w.BeginPropEmergence(crate.Id, frameA, a.Gate!.ConnectionId);
                w.HoldProp(crate.Id);
            });
            await server.InspectConnectionAgeAsync(id, GateTravel.MaximumConnectionTicks);
            await Until(() => a.Gates.Values.All(g => g.Phase == GatePhase.Idle), "38-minute limit closes both endpoints despite stalled emergence");
            Verify.Check(a.LastSnapshot.Registry?.All(p => p.Id != crate.Id) == true, "hard limit removes unfinished receiving prop");
        }
        finally
        {
            observer.Dispose();
        }
        Console.WriteLine("PASS: nearby gate-pair network checks complete");
    }
}
