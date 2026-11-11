using System.Numerics;
using System.Text;
using System.Text.Json;
using StargateGame.Physics;
using StargateGame.Shared;

static class TacticalMovementChecks
{
    static readonly PlanetDefinition Definition = PlanetDefinition.Default with { Entities = [], Props = [] };
    static PhysicsWorld Floor(bool player = true, bool crouched = false)
    {
        var world = new PhysicsWorld(Definition, false);
        world.AddStaticBox(new(0, -.5f, 0), new(100, .5f, 100));
        if (player) world.AddPlayer(new(1, "Course", Vector3.Zero, default, 0, 0, true, 0, Crouched: crouched));
        return world;
    }
    static PlayerInput Input(long tick, float forward = 0, float strafe = 0, bool walk = false, bool crouch = false, bool jump = false)
        => new(tick, strafe, forward, 0, 0, walk, jump, tick, Crouch: crouch);
    static void Tick(PhysicsWorld world, float forward = 0, bool crouch = false, bool jump = false)
        => world.Step(new Dictionary<int, PlayerInput> { [1] = Input(world.Tick + 1, forward, crouch: crouch, jump: jump) });
    static float Horizontal(Vector3 v) => new Vector2(v.X, v.Z).Length();

    public static void Run(string? tracePath = null)
    {
        var trace = new StringBuilder("phase,tick,time_s,speed_mps,x,y,z,crouched,grounded\n");
        void Trace(string phase, int tick, Vector3 velocity, Vector3 position = default, bool crouched = false, bool grounded = true)
            => trace.AppendLine(FormattableString.Invariant($"{phase},{tick},{tick * Movement.Dt:F6},{Horizontal(velocity):F6},{position.X:F6},{position.Y:F6},{position.Z:F6},{crouched},{grounded}"));

        var wish = Movement.DesiredVelocity(Input(1, 1));
        Verify.Near(wish.Length(), 5.4f, .0001f, "run is the default");
        Verify.Near(Movement.DesiredVelocity(Input(1, 1, walk: true)).Length(), 2.7f, .0001f, "walk modifier");
        Verify.Near(Movement.DesiredVelocity(Input(1, 1), true).Length(), 1.8f, .0001f, "actual crouch stance selects speed");
        Verify.Near(Movement.DesiredVelocity(Input(1, 1, 1)).Length(), wish.Length(), .0001f, "diagonal input normalized");
        var slopeNormal = Vector3.Normalize(new Vector3(0, 1, .5f));
        Vector3 slopeVelocity = default;
        for (int i = 0; i < 120; i++) slopeVelocity = Movement.Accelerate(slopeVelocity, wish, true, 0, slopeNormal);
        Verify.Near(slopeVelocity.Length(), Movement.RunSpeed, .001f, "walkable slope retains surface travel speed");
        Verify.Near(Vector3.Dot(slopeVelocity, slopeNormal), 0, .001f, "ground wish velocity follows support plane");
        Vector3 velocity = default;
        int runTicks = 0;
        while (Horizontal(velocity) < Movement.RunSpeed - .001f && runTicks < 120)
        {
            velocity = Movement.Accelerate(velocity, wish, true, 0);
            Trace("accelerate", ++runTicks, velocity);
        }
        Verify.Check(runTicks is >= 15 and <= 25, "running speed reached in approximately 0.3 seconds");
        var release = velocity; var counter = velocity;
        int releaseTicks = 0, counterTicks = 0;
        while (Horizontal(release) > .001f && releaseTicks < 120)
        {
            release = Movement.Accelerate(release, Vector3.Zero, true, 0);
            Trace("release", ++releaseTicks, release);
        }
        while (Vector3.Dot(counter, wish) > 0 && counterTicks < 120)
        {
            counter = Movement.Accelerate(counter, -wish, true, 0);
            Trace("counter", ++counterTicks, counter);
        }
        Verify.Check(releaseTicks is >= 15 and <= 21, "release stop within 0.25-0.35 seconds");
        Verify.Check(counterTicks is >= 6 and <= 9 && counterTicks < releaseTicks, "counter-strafe stop within 0.10-0.15 seconds");
        velocity = new(12, 2, 0);
        Verify.Check(Movement.Accelerate(velocity, Vector3.Zero, false, 12) == velocity, "air coast preserves external momentum and vertical velocity");
        for (int i = 0; i < 600; i++)
        {
            var strafe = Vector3.Normalize(new(-velocity.Z, 0, velocity.X)) * Movement.RunSpeed;
            velocity = Movement.Accelerate(velocity, strafe, false, 12);
        }
        Verify.Check(Horizontal(velocity) <= 12.0001f && velocity.Y == 2, "air strafe cannot exceed takeoff envelope");

        using (var world = Floor())
        {
            for (int i = 0; i < 10; i++) Tick(world);
            Tick(world, crouch: true);
            Verify.Check(world.Player(1).Crouched, "crouch switches collision stance");
            Verify.Near(world.Player(1).Position.Y, 0, .03f, "crouch keeps feet anchored");
            using var checkpoint = world.Save();
            Tick(world);
            Verify.Check(!world.Player(1).Crouched, "uncrouch succeeds with clearance");
            world.Restore(checkpoint);
            Verify.Check(world.Player(1).Crouched, "checkpoint restores crouch state and both shapes");
            world.AddStaticBox(new(0, 1.5f, 0), new(2, .15f, 2));
            for (int i = 0; i < 10; i++) Tick(world);
            Verify.Check(world.Player(1).Crouched, "ceiling blocks uncrouching");
            Verify.Check(world.CanPlacePlayer(world.Player(1).Position, 1, true)
                && !world.CanPlacePlayer(world.Player(1).Position, 1, false), "real low capsule fits below ceiling");
            var camera = world.ConstrainCamera(1, Movement.EyePosition(world.Player(1)) + Vector3.UnitY * .65f);
            Verify.Check(camera.Y < 1.35f, "camera smoothing cannot pass through ceiling");
            for (int i = 0; i < 180; i++) Tick(world, 1);
            Verify.Check(!world.Player(1).Crouched, "standing resumes after leaving low ceiling");
        }
        using (var world = Floor())
        {
            for (int i = 0; i < 10; i++) Tick(world);
            Tick(world, jump: true);
            float peak = 0;
            for (int i = 0; i < 120; i++)
            {
                // Held inputs carry no jump edge, even after landing.
                var input = Input(world.Tick + 1, jump: true).Held;
                world.Step(new Dictionary<int, PlayerInput> { [1] = input });
                var p = world.Player(1); peak = Math.Max(peak, p.Position.Y);
                Trace("jump", i, p.Velocity, p.Position, p.Crouched, p.Grounded);
            }
            Verify.Check(peak is > .60f and < .76f && world.Player(1).Grounded, "jump height and no held-space hopping");
        }
        using (var server = Floor())
        using (var predicted = new PredictedWorld(Definition, 1, server.Snapshot(), prepared: Floor(false)))
        {
            var snapshots = new Queue<PhysicsSnapshot>();
            for (int tick = 1; tick <= 240; tick++)
            {
                var command = Input(tick, tick < 150 ? 1 : -1, crouch: tick is >= 45 and < 110, jump: tick == 140);
                predicted.Advance(command);
                // One deliberately missed server command exercises correction
                // and replay across stance transitions after simulated latency.
                server.Step(new Dictionary<int, PlayerInput> { [1] = tick == 45 ? command with { Crouch = false } : command });
                if (tick % 3 == 0) snapshots.Enqueue(server.Snapshot());
                if (tick > 12 && tick % 3 == 0) predicted.Accept(snapshots.Dequeue());
            }
            while (snapshots.Count > 0) predicted.Accept(snapshots.Dequeue());
            Verify.Check(predicted.Simulation.Player(1) == server.Player(1), "authoritative and predicted movement converge through delayed stance rollback");
        }
        var old = JsonSerializer.Deserialize<PlayerState>("{\"Id\":1,\"Name\":\"Old save\"}", Protocol.Json);
        Verify.Check(!old.Crouched && old.AirSpeedLimit == 0, "old player records default to standing");
        if (tracePath is not null)
        {
            string full = Path.GetFullPath(tracePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, trace.ToString());
        }
        Console.WriteLine(FormattableString.Invariant($"TACTICAL MOVEMENT accelerate={runTicks * Movement.Dt:F3}s release={releaseTicks * Movement.Dt:F3}s counter={counterTicks * Movement.Dt:F3}s"));
    }
}
