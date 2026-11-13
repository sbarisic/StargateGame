using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;

static class PropSupportChecks
{
    public static void Run()
    {
        foreach (float offset in new[] { 0f, .25f })
        {
            using var world = new PhysicsWorld(PlanetDefinition.Default with { Entities = [], Props = [] }, false);
            world.AddStaticBox(new(0, -.5f, 0), new(10, .5f, 10));
            world.AddProp(new(1, new(0, .5f, 0), new(.5f), 10));
            for (int i = 0; i < 120; i++) world.Step();
            world.AddPlayer(new(1, "Standing", new(offset, 1.1f, 0), default, 0, 0, false, 0));
            var samples = new List<(Vector3 Player, BodyState Body)>();
            for (int i = 0; i < 360; i++)
            {
                world.Step();
                if (i >= 120) samples.Add((world.Player(1).Position, world.Bodies()[0]));
            }
            float bodySpan = samples.Max(s => s.Body.Position.Y) - samples.Min(s => s.Body.Position.Y);
            float playerSpan = samples.Max(s => s.Player.Y) - samples.Min(s => s.Player.Y);
            float spin = samples.Max(s => s.Body.AngularVelocity.Length());
            Console.WriteLine($"STANDING offset={offset} crateYSpan={bodySpan:F6} playerYSpan={playerSpan:F6} angularSpeed={spin:F6}");
            Verify.Check(world.Player(1).Grounded && world.Player(1).SupportId == 1, "player remains supported by crate");
            Verify.Check(bodySpan < .003f && playerSpan < .003f && spin < .03f, "standing still does not rock or jitter a settled crate");
            var state = world.Snapshot();
            world.Apply(state with { Bodies = [state.Bodies[0] with { LinearVelocity = new(2, 0, 0), Sleeping = false }] });
            float startX = world.Player(1).Position.X;
            for (int i = 0; i < 8; i++) world.Step();
            Verify.Check(world.Player(1).Position.X > startX + .025f, "moving prop still carries its rider");
            float startY = world.Player(1).Position.Y;
            world.Step(new Dictionary<int, PlayerInput> { [1] = new(1, 0, 0, 0, 0, false, true) });
            Verify.Check(!world.Player(1).Grounded && world.Player(1).Position.Y > startY + .05f, "jump releases prop support");
        }
        using var push = new PhysicsWorld(PlanetDefinition.Default with { Entities = [], Props = [] }, false);
        push.AddStaticBox(new(0, -.5f, 0), new(10, .5f, 10));
        push.AddProp(new(1, new(0, .5f, 0), new(.5f), 10));
        push.AddPlayer(new(1, "Pusher", new(0, 0, 1.2f), default, 0, 0, false, 0));
        for (int i = 1; i <= 120; i++)
            push.Step(new Dictionary<int, PlayerInput> { [1] = new(i, 0, 1, 0, 0, false, false) });
        Verify.Check(push.Bodies()[0].Position.Z < -1, "side pushing remains effective without support weight");
    }
}
