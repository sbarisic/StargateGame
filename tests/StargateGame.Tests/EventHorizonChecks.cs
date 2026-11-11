using System.Numerics;
using StargateGame.Client;
using StargateGame.Shared;
using StargateGame.Server;
using StargateGame.Physics;
using System.Net;
using System.Diagnostics;

static class EventHorizonChecks
{
    public static async Task PropNetwork()
    {
        string saves = Path.Combine(Path.GetTempPath(), "stargate-prop-horizon-" + Guid.NewGuid().ToString("N"));
        using (var generation = new StargateGame.WorldGen.GenerationService(7319, "temperate"))
        {
            var plan = await generation.Plan;
            await using var db = new StargateGame.Persistence.WorldRepository(saves);
            var saved = await db.ResolveAsync(plan.Definition);
            var gate = saved.Definition.Entities.Single(e => e.Model == "stargate");
            var bodies = saved.Definition.Props.Select(p => new BodyState(p.Id, p.Position, Quaternion.Identity, Vector3.Zero, Vector3.Zero, false)).ToArray();
            bodies[0] = bodies[0] with
            {
                Position = gate.Position + new Vector3(-.75f, PlanetSettings.GateEmbedDepth + .51f, 0)
            };
            await db.SaveBodiesAsync(saved.Id, bodies);
        }
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, 7319, "temperate", true, saves);
        server.Start();
        await using var proxy = new DelayProxy(server.Port, 0);
        using var a = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", proxy.Port, "PropA", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        using var b = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", proxy.Port, "PropB", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        var activeGate = a.World.Definition.Entities.Single(e => e.Model == "stargate");
        var states = new[] { new EventHorizonState(), new EventHorizonState() };
        var clients = new[] { a, b };
        var timer = Stopwatch.StartNew();
        bool[] pulsed = [false, false];
        Vector3 start = default;
        async Task Tick(Vector3? target)
        {
            for (int i = 0; i < clients.Length; i++)
            {
                var c = clients[i];
                c.Poll();
                if (c.Failure is { } error)
                    throw new Exception(error);
                var delta = i == 0 && target.HasValue ? target.Value - c.Local.Position : Vector3.Zero;
                var movement = new Vector2(delta.X, -delta.Z);
                if (movement.LengthSquared() > .04f)
                    movement = Vector2.Normalize(movement);
                else
                    movement = Vector2.Zero;
                c.Step(movement.X, movement.Y, 0, 0, false, false);
                states[i].Update(activeGate, [], timer.Elapsed.TotalSeconds, c.World.Definition.LocationId, c.Physics.Epoch, c.RenderBodies(), c.World.Definition.Props);
                pulsed[i] |= states[i].Footprints.Count > 0;
            }
            await Task.Delay(16);
        }
        while (timer.Elapsed.TotalSeconds < 3)
            await Tick(null);
        start = a.RenderBodies().Single(p => p.Id == 1).Position;
        Verify.Check(pulsed.All(v => v), "two delayed clients see continuous resting-prop contact");
        async Task Reach(Vector3 target)
        {
            double deadline = timer.Elapsed.TotalSeconds + 20;
            while (timer.Elapsed.TotalSeconds < deadline)
            {
                await Tick(target);
                if (Vector2.Distance(new(a.Local.Position.X, a.Local.Position.Z), new(target.X, target.Z)) < .35f)
                    return;
            }
            throw new Exception("Prop contact network approach timed out: " + a.Local.Position);
        }
        await Reach(activeGate.Position + new Vector3(-.75f, 0, 2));
        await Reach(activeGate.Position + new Vector3(-.75f, 0, -2.5f));
        for (int i = 0; i < 240; i++)
            await Tick(null);
        var finalA = a.RenderBodies().Single(p => p.Id == 1);
        var finalB = b.RenderBodies().Single(p => p.Id == 1);
        Verify.Check(Vector3.Distance(start, finalA.Position) > 1, "client pushes authoritative crate through visual membrane");
        Verify.Check(Vector3.Distance(finalA.Position, finalB.Position) < .08f, "delayed clients converge on crate after gate contact");
        Verify.Check(states.All(s => s.Footprints.Count == 0), "prop boundaries disappear after separation on both clients");
    }
    public static async Task Network()
    {
        string saves = Path.Combine(Path.GetTempPath(), "stargate-horizon-" + Guid.NewGuid().ToString("N"));
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, 7319, "temperate", true, saves);
        server.Start();
        await using var proxy = new DelayProxy(server.Port, 0);
        using var a = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", proxy.Port, "HorizonA", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        using var b = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", proxy.Port, "HorizonB", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        var gate = a.World.Definition.Entities.Single(e => e.Model == "stargate");
        var sa = new EventHorizonState();
        var sb = new EventHorizonState();
        var clock = Stopwatch.StartNew();
        int seenA = 0, seenB = 0;
        bool localA = false, remoteA = false;
        async Task Tick(Vector3? ta, Vector3? tb)
        {
            a.Poll();
            b.Poll();
            if (a.Failure is not null || b.Failure is not null)
                throw new Exception(a.Failure ?? b.Failure);
            void Move(ClientSession c, Vector3? target)
            {
                var d = target.HasValue ? target.Value - c.Local.Position : Vector3.Zero;
                var v = new Vector2(d.X, -d.Z);
                if (v.LengthSquared() > .3f * .3f)
                    v = Vector2.Normalize(v);
                else
                    v = Vector2.Zero;
                c.Step(v.X, v.Y, 0, 0, true, false);
            }
            Move(a, ta);
            Move(b, tb);
            var time = clock.Elapsed.TotalSeconds;
            sa.Update(gate, a.RemotePlayers().Append(a.Local with
            {
                Position = a.RenderPosition
            }), time, a.World.Definition.LocationId, a.Physics.Epoch);
            sb.Update(gate, b.RemotePlayers().Append(b.Local with
            {
                Position = b.RenderPosition
            }), time, b.World.Definition.LocationId, b.Physics.Epoch);
            seenA = Math.Max(seenA, sa.Ripples.Count);
            seenB = Math.Max(seenB, sb.Ripples.Count);
            foreach (var r in sa.Ripples)
            {
                if (r.Position.X < .35f)
                    localA = true;
                else
                    remoteA = true;
            }
            await Task.Delay(16);
        }
        async Task Reach(Vector3 ta, Vector3 tb)
        {
            double until = clock.Elapsed.TotalSeconds + 20;
            while (clock.Elapsed.TotalSeconds < until)
            {
                await Tick(ta, tb);
                if (Vector2.Distance(new(a.Local.Position.X, a.Local.Position.Z), new(ta.X, ta.Z)) < .5f && Vector2.Distance(new(b.Local.Position.X, b.Local.Position.Z), new(tb.X, tb.Z)) < .5f)
                    return;
            }
            throw new Exception("Horizon walk timed out: " + a.Local.Position + " / " + b.Local.Position);
        }
        await Reach(gate.Position + new Vector3(0, 0, 2), gate.Position + new Vector3(.8f, 0, 2));
        await Reach(gate.Position + new Vector3(0, 0, -2), gate.Position + new Vector3(.8f, 0, -2));
        for (int i = 0; i < 50; i++)
            await Tick(null, null);
        Verify.Check(seenA >= 2 && seenB >= 2, "two delayed clients observe both players contacting the horizon");
        Verify.Check(localA && remoteA, "local and interpolated remote contact positions remain distinct");
        Verify.Check(a.Local.Position.Z < gate.Position.Z - 1 && b.Local.Position.Z < gate.Position.Z - 1, "horizon adds no physics barrier or teleportation");
        await Reach(gate.Position + new Vector3(0, 0, 2), gate.Position + new Vector3(.8f, 0, 2));
        Verify.Check(sa.Ripples.Count > 0 && sb.Ripples.Count > 0, "delayed reverse crossings produce new ripples");
        for (int i = 0; i < 220; i++)
            await Tick(null, null);
        Verify.Check(sa.Ripples.Count == 0 && sb.Ripples.Count == 0, "network contact ripples expire after interaction stops");
    }
    static void Props()
    {
        var gate = new StaticEntity("gate", "stargate", Vector3.Zero);
        var centre = EventHorizonState.Centre(gate);
        var definitions = new[] { new PropDefinition(1, Vector3.Zero, new(.5f), 10), new PropDefinition(2, Vector3.Zero, new(.5f), 10) };
        var state = new EventHorizonState();
        var location = Guid.NewGuid();
        double time = 0;
        BodyState Body(int id, float z, float x = 0) => new(id, centre + new Vector3(x, 0, z), Quaternion.Identity, new(0, 0, -10), Vector3.Zero, false);
        void Tick(params BodyState[] bodies)
        {
            time += .1;
            state.Update(gate, [], time, location, 1, bodies, definitions);
        }
        Tick(Body(1, 1));
        Tick(Body(1, .4f));
        Verify.Check(state.Impulses.Count == 1 && state.Impulses[0].Vertices.Length == 4, "prop entry preserves rectangular contour");
        Verify.Check(state.Footprints.Single().Previous.Position.Z == 1 && state.Footprints.Single().Current.Position.Z == .4f, "footprint retains rendered previous/current transforms");
        for (int i = 0; i < 40; i++)
            Tick(Body(1, .4f) with
            {
                Sleeping = true,
                LinearVelocity = Vector3.Zero
            });
        Verify.Check(state.Footprints.Count == 1 && state.Impulses.Count == 0, "sleeping prop remains a boundary without periodic point impulses");
        Verify.Check(state.Footprints[0].Vertices.Length == 4 && state.Footprints[0].Motion == 0, "stationary boundary preserves all four corners");
        Tick(Body(1, -1));
        Tick(Body(1, -.4f));
        Verify.Check(state.Impulses.Count == 1, "prop reverse entry rearms");
        state.Clear();
        Tick(Body(1, 1));
        Tick(Body(1, -1) with
        {
            LinearVelocity = new(0, 0, -25)
        });
        Verify.Check(state.Impulses.Count == 1 && state.Footprints.Count == 0, "complete fast crossing retains swept contour after separation");
        state.Clear();
        Tick(Body(1, 5));
        Tick(Body(1, 0) with
        {
            LinearVelocity = Vector3.Zero
        });
        Verify.Check(state.Impulses.Count == 0 && state.Footprints[0].SuppressImpact, "correction updates outline without impact");
        state.Clear();
        Tick(Body(1, 0));
        Verify.Check(state.Impulses.Count == 0 && state.Footprints[0].SuppressImpact, "first observation is quiet but supplies boundary");
        Tick(Body(1, 0));
        Verify.Check(!state.Footprints[0].SuppressImpact, "stationary boundary remains active after observation");
        state.Clear();
        Tick(Body(1, 1, 4));
        Tick(Body(1, 0, 4));
        Verify.Check(state.Impulses.Count == 0 && state.Footprints.Count == 0, "outside opening has no boundary");
        var rotated = Body(1, 0) with
        {
            Rotation = Quaternion.CreateFromYawPitchRoll(.7f, .4f, .3f)
        };
        var polygon = EventHorizonState.Footprint(rotated, new(.5f), centre, 2.3f);
        Verify.Check(polygon.Count >= 5 && polygon.All(p => p.Length() <= 2.3f), "rotated box has non-rectangular collision slice");
        var grazing = EventHorizonState.Footprint(Body(1, 0, 2.5f), new(.5f), centre, 2.3f);
        Verify.Check(grazing.Count >= 3 && grazing.All(p => p.Length() <= 2.3f), "rim contact is clipped to aperture");
        state.Clear();
        Tick(Body(1, .65f));
        Tick(Body(1, .65f) with
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4),
            AngularVelocity = new(0, 8, 0)
        });
        Verify.Check(state.Impulses.Count == 1 && state.Footprints[0].Motion > 0, "rotation-only contact retains contour and motion");
        state.Clear();
        Tick(Body(1, .8f));
        Tick(Body(1, .499f));
        for (int i = 0; i < 8; i++)
            Tick(Body(1, i % 2 == 0 ? .501f : .499f));
        Verify.Check(state.Impulses.Count == 1, "contact jitter does not repeat entry impulses");
        state.Clear();
        Tick(Body(1, 1), Body(2, -1));
        Tick(Body(1, .2f), Body(2, -.2f));
        Verify.Check(state.Footprints.Count == 2 && state.Footprints.Select(f => f.BodyId).Distinct().Count() == 2, "overlapping bodies retain distinct identities for union rasterization");
        state.Clear();
        Tick(Body(1, 1));
        time += .1;
        state.Update(gate, [new PlayerState(1, "same ID", centre + new Vector3(0, -.9f, 1), default, 0, 0, true, 0)], time, location, 1, [Body(1, .2f)], definitions);
        Verify.Check(state.Impulses.Count == 1, "player and body identity spaces are separate");
        Tick();
        for (int i = 0; i < 35; i++)
            Tick();
        Verify.Check(state.Footprints.Count == 0 && state.Impulses.Count == 0, "removed bodies leave no stale contour");
        for (int i = 0; i < 20; i++)
        {
            Tick(Body(1, 1));
            Tick(Body(1, 0));
        }
        Verify.Check(state.Impulses.Count <= 16, "pending contour storage is bounded");
        state.Clear();
        Tick(Body(1, 0));
        int generation = state.Generation;
        state.Update(gate, [], time + .1, location, 2, [Body(1, 0)], definitions);
        Verify.Check(state.Generation == generation && state.Impulses.Count == 0 && state.Footprints[0].SuppressImpact, "epoch correction quietly reseeds contacts without invalidating GPU wake");
        foreach (int n in new[] { 256, 384, 512 })
            Verify.Check(1.35f / 60 / EventHorizonSettings.Substeps(n, 2.3f, 1.35f) / (4.6f / n) <= .65f, "quality grid satisfies wave stability bound");
        var capsule = EventHorizonState.Capsule(Vector2.Zero, .32f, 1.8f);
        Verify.Check(capsule.Length == 24 && capsule.Max(v => v.Y) - capsule.Min(v => v.Y) > 1.79f, "player injection uses extended capsule contour");
        Verify.Check(Enumerable.Range(0, 24).All(i => Vector2.Distance(capsule[i], -capsule[(i + 12) % 24]) < .00001f), "capsule contour is symmetric on both sides");
        Verify.Check(Enumerable.Range(0, 24).All(i =>
        {
            var a = capsule[(i + 1) % 24] - capsule[i];
            var b = capsule[(i + 2) % 24] - capsule[(i + 1) % 24];
            return a.X * b.Y - a.Y * b.X >= -1e-6f;
        }), "capsule contour stays convex for GPU half-plane rasterization");
        Verify.Near(new EventHorizonSettings().WaveSpeed, 1.35f, .00001f, "contact propagation slowed independently");
        Verify.Check(new EventHorizonSettings().DisturbanceSmoothing >= .05f, "contact gradient rejects small high-frequency bumps");
        Verify.Near(new EventHorizonSettings().Speed, 7.0f, .00001f, "horizon ripple animation runs five times faster");
    }
    public static void Run()
    {
        Props();
        var gate = new StaticEntity("gate", "stargate", new(10, 0, 20));
        var centre = EventHorizonState.Centre(gate);
        var id = Guid.NewGuid();
        var state = new EventHorizonState();
        double time = 0;
        PlayerState Player(int n, float z, float x = 0, float y = -.9f) => new(n, "Contact", centre + new Vector3(x, y, z), default, 0, 0, true, 0);
        void Update(params PlayerState[] players)
        {
            time += .1;
            state.Update(gate, players, time, id, 1);
        }
        Update(Player(1, 1));
        Verify.Check(state.Ripples.Count == 0, "horizon initial observation does not emit");
        Update(Player(1, .2f));
        Verify.Check(state.Ripples.Count == 1, "front approach emits a contact ripple");
        Verify.Check(state.Impulses.Single().Displacement == 0 && state.Impulses.Single().Strength <= .04f, "player entry avoids an additional height wave");
        Verify.Check(state.Impulses.Count == 1 && state.Impulses[0].Polygons.Length == 1 && state.Impulses[0].Vertices.Length == 24 && state.PlayerFootprints.Count == 1 && state.Footprints.Count == 0,
            "player entry supplies a persistent capsule contour in a separate namespace");
        Update(Player(1, .1f));
        Update(Player(1, -.1f));
        Update(Player(1, .34f));
        Update(Player(1, .2f));
        Verify.Check(state.Ripples.Count == 1, "standing and correction jitter do not repeat contacts");
        Verify.Check(state.Impulses.Count == 1, "stationary player overlap does not repeat GPU impulses");
        Update(Player(1, -.6f));
        Update(Player(1, -.1f));
        Verify.Check(state.Ripples.Count == 2, "back approach rearms after leaving contact band");
        state.Clear();
        Update(Player(1, .6f));
        Update(Player(1, -.6f));
        Verify.Check(state.Ripples.Count == 1, "swept sprint crossing cannot skip the membrane");
        state.Clear();
        Update(Player(1, .7f, 0, 1));
        Update(Player(1, -.4f, 0, 1));
        Verify.Check(state.Ripples.Count == 1, "jumping capsule can contact upper opening");
        state.Clear();
        Update(Player(1, .7f, 4));
        Update(Player(1, -.4f, 4));
        Verify.Check(state.Ripples.Count == 0, "movement outside gate opening does not emit");
        state.Clear();
        Update(Player(1, 0, 3));
        Update(Player(1, 0, 2.5f));
        Verify.Check(state.Ripples.Count == 1, "sideways entry into the opening is not latched by the infinite plane");
        state.Clear();
        Update(Player(1, 2));
        Update(Player(1, -2));
        Verify.Check(state.Ripples.Count == 0, "discontinuous position corrections suppress ripples");
        state.Clear();
        Update(Player(1, .1f));
        Update(Player(1, .1f), Player(2, 0));
        Verify.Check(state.Ripples.Count == 0, "spawn and newly observed remote at plane stay quiet");
        Update(Player(1, .7f), Player(2, -.7f));
        Update(Player(1, .1f), Player(2, -.1f));
        Verify.Check(state.Ripples.Count == 2, "simultaneous players each produce a disturbance");
        Verify.Check(state.PlayerFootprints.Count == 2, "simultaneous players retain independent persistent contours");
        var rotatedGate = gate with
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.1f)
        };
        var rotatedState = new EventHorizonState();
        PlayerState Rotated(float z) => Player(7, z) with
        {
            Position = EventHorizonState.Centre(rotatedGate) + Vector3.Transform(new Vector3(0, -.9f, z), rotatedGate.Rotation)
        };
        rotatedState.Update(rotatedGate, [Rotated(.5f)], 1, id, 1);
        rotatedState.Update(rotatedGate, [Rotated(.2f)], 1.1, id, 1);
        Verify.Check(rotatedState.PlayerFootprints.Count == 1 && rotatedState.Impulses.Count == 1, "rotated gate uses local contact plane");
        foreach (int fps in new[] { 30, 60, 120 })
        {
            var steady = new EventHorizonState();
            for (int i = 0; i < fps; i++)
                steady.Update(gate, [Player(1, 0)], i / (double)fps, id, 1);
            Verify.Check(steady.PlayerFootprints.Count == 1 && steady.Impulses.Count == 0, "standing contact has no periodic impulse at " + fps);
        }
        for (int n = 0; n < 20; n++)
            state.Emit(new(n, 0), time + n * .001);
        Verify.Check(state.Ripples.Count == 16 && state.Ripples[0].Position.X == 4, "ripple buffer evicts oldest beyond sixteen");
        time += 4;
        Update(Player(1, 1));
        Verify.Check(state.Ripples.Count == 0, "expired ripples are removed");
        state.Emit(Vector2.Zero, time);
        state.Update(gate, [Player(1, 0)], time + .1, id, 2);
        Verify.Check(state.Ripples.Count > 0, "physics epoch changes preserve residual visual history");
        state.Emit(Vector2.Zero, time);
        state.Update(gate, [Player(1, 0)], time + .2, Guid.NewGuid(), 2);
        Verify.Check(state.Ripples.Count == 0, "planet changes clear visual history");
        state.Emit(Vector2.Zero, time);
        state.Clear();
        Verify.Check(state.Ripples.Count == 0, "reconnect clears ripple state");
        Verify.Near(EventHorizonState.Centre(gate with
        {
            Scale = 2
        }).Y, 6.127384f, .0001f, "membrane centre follows entity scale");
    }
}
