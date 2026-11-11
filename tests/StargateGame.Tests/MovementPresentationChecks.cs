using System.Numerics;
using StargateGame.Client;
using StargateGame.Shared;

static class MovementPresentationChecks
{
    static PhysicsSnapshot State(long tick, Vector3 position, Quaternion? rotation = null, long epoch = 1) => new(tick, epoch,
        [new(1, "Walker", position, new(4, 0, 0), 0, 0, true, tick)],
        [new(1, position + new Vector3(0, .5f, -1), rotation ?? Quaternion.Identity, new(4, 0, 0), default, false)], []);
    public static void Run()
    {
        var a = State(60, Vector3.Zero);
        var b = State(61, new(4 * Movement.Dt, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, .1f));
        var visual = new PredictionPresentation();
        foreach (int rate in new[] { 30, 60, 120, 144, 240 })
        {
            float maxError = 0, maxRelative = 0;
            Vector3 last = default;
            int stopped = 0;
            for (int frame = 0; frame < rate; frame++)
            {
                double time = frame / (double)rate, tick = 60 + time * 60;
                long current = (long)Math.Floor(tick) + 1;
                var previous = State(current - 1, new((current - 61) * 4 * Movement.Dt, 0, 0));
                var next = State(current, new((current - 60) * 4 * Movement.Dt, 0, 0));
                var pose = visual.Sample(previous, next, 1, tick, time);
                maxError = Math.Max(maxError, Math.Abs(pose.Player.Position.X - (float)time * 4));
                maxRelative = Math.Max(maxRelative, Vector3.Distance(pose.Bodies[0].Position - pose.Player.Position, new(0, .5f, -1)));
                if (frame > 0 && pose.Player.Position == last)
                    stopped++;
                last = pose.Player.Position;
            }
            Verify.Check(maxError < .0001f && stopped == 0, $"{rate} Hz rendering samples continuous 60 Hz motion without held frames");
            Verify.Check(maxRelative < .0001f, $"{rate} Hz player and pushed/supporting box share the same interpolation phase");
        }
        var half = PredictionPresentation.Interpolate(a, b, 1, 60.5);
        var crouched = b with { Players = [b.Players[0] with { Crouched = true }] };
        var low = PredictionPresentation.Interpolate(a, crouched, 1, 60.5).Player;
        Verify.Check(low.Crouched && Movement.ViewHeight(low) == Movement.CrouchEyeHeight, "local stance remains authoritative while feet interpolate");
        Verify.Check(Math.Abs(Quaternion.Dot(half.Bodies[0].Rotation, Quaternion.CreateFromAxisAngle(Vector3.UnitY, .05f))) > .99999f, "box rotation interpolates with translation");
        var before = visual.Sample(a, b, 1, 60.5, 1);
        var correctedA = State(60, new(.15f, 0, 0));
        var correctedB = State(61, new(.15f + 4 * Movement.Dt, 0, 0));
        var after = PredictionPresentation.Interpolate(correctedA, correctedB, 1, 60.5);
        visual.Correct(before, after, 1);
        var same = visual.Sample(correctedA, correctedB, 1, 60.5, 1);
        Verify.Check(Vector3.Distance(same.Player.Position, before.Player.Position) < 1e-6 && Vector3.Distance(same.Bodies[0].Position, before.Bodies[0].Position) < 1e-6, "reconciliation preserves the current rendered player and box pose");
        var settled = visual.Sample(correctedA, correctedB, 1, 60.5, 1.25);
        Verify.Check(Vector3.Distance(settled.Player.Position, after.Player.Position) < .002f, "small corrections decay without a linear stop/start seam");
        visual.Clear();
        Verify.Check(visual.Sample(correctedA, correctedB, 1, 60.5, 1).Player == after.Player, "epoch reset drops visual error history");
        var teleport = State(61, new(10, 0, 0));
        Verify.Check(PredictionPresentation.Interpolate(a, teleport, 1, 60.5).Player.Position.X == 10, "teleports do not interpolate through solid geometry");
        var clock = new PredictionClock();
        clock.Anchor(60, 0, true);
        double prior = clock.PredictionTime(0);
        bool monotonic = true, bounded = true;
        for (int i = 1; i <= 500; i++)
        {
            double now = i * .002;
            if (i % 25 == 0)
            {
                clock.Anchor(60 + now * 60 + (i % 50 == 0 ? .8 : -.8), now);
                clock.SetRoundTrip(i % 50 == 0 ? .095 : .105, now);
            }
            double value = clock.PredictionTime(now), step = value - prior;
            monotonic &= step > 0;
            bounded &= step < .14;
            prior = value;
        }
        Verify.Check(monotonic && bounded, "clock and input lead slew through ping jitter without tick-phase jumps");
        clock.Anchor(600, 2);
        Verify.Check(Math.Abs(clock.Estimate(2) - 600) < .001, "long load/stall clock discontinuity resynchronizes promptly");
        var remote = new RemotePresentation();
        remote.Add(a);
        remote.Add(b);
        var first = remote.Sample(60.5, 2).Single().Position;
        remote.Add(State(64, new(4 * 4 * Movement.Dt, 0, 0)));
        Verify.Check(Vector3.Distance(first, remote.Sample(60.5, 2).Single().Position) < 1e-6, "new packet arrival does not restart remote interpolation");
        remote.Clear();
        remote.Add(State(120, new(9, 0, 0), epoch: 2));
        Verify.Check(remote.Sample(60, 2).Single().Position.X == 9, "remote interpolation resets at topology/location boundaries");
    }
}
