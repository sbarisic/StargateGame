using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    int pickupStage, pickupMovieFrame;
    double pickupHeldAt, nextPickupFrame;
    readonly HashSet<int> pickupCaptured = [];
    void InspectPickup(double now, ref float strafe, ref float forward)
    {
        if (!args.Contains("--pickup-check") || session is null)
            return;
        var prop = session.World.Definition.Props.First();
        var body = session.Physics.Bodies().Single(b => b.Id == prop.Id);
        var eye = Movement.EyePosition(session.Local);
        var aim = Vector3.Normalize(body.Position - eye);
        yaw = MathF.Atan2(aim.X, -aim.Z);
        pitch = MathF.Asin(aim.Y);
        strafe = forward = 0;
        if (pickupStage == 0)
        {
            var destination = prop.Position + Vector3.UnitZ * 2;
            var delta = destination - session.Local.Position;
            delta.Y = 0;
            if (delta.Length() > .15f)
            {
                delta = Vector3.Normalize(delta);
                strafe = Vector3.Dot(delta, new Vector3(MathF.Cos(yaw), 0, MathF.Sin(yaw)));
                forward = Vector3.Dot(delta, new Vector3(MathF.Sin(yaw), 0, -MathF.Cos(yaw)));
            }
            else if (session.Physics.PickProp(session.Local.Id, eye, aim).Prop is { } picked)
            {
                session.Carry(CarryAction.Grab, picked.PersistentId);
                pickupStage = 1;
            }
        }
        if (pickupStage == 1 && session.Physics.IsCarrying(session.Local.Id))
        {
            pickupHeldAt = now;
            pickupStage = 2;
            Console.WriteLine("PICKUP CHECK holding");
        }
        if (pickupStage == 2)
        {
            yaw = (float)(Math.Sin((now - pickupHeldAt) * .8) * .35);
            pitch = -.15f;
            if (now - pickupHeldAt > 5)
            {
                session.Carry(CarryAction.Throw);
                pickupStage = 3;
                Console.WriteLine("PICKUP CHECK throw");
            }
        }
    }
    void CapturePickup(RenderPass pass, double now)
    {
        if (!args.Contains("--pickup-check") || pickupStage < 2)
            return;
        if (pickupCaptured.Add(pickupStage))
            Capture(pass, Path.Combine(captureDirectory, $"pickup-stage-{pickupStage}.png"));
        if (now < nextPickupFrame || now - pickupHeldAt > 8)
            return;
        Capture(pass, Path.Combine(captureDirectory, $"pickup-{pickupMovieFrame++:D4}.jpg"), true);
        nextPickupFrame = now + 1.0 / 15;
    }
}
