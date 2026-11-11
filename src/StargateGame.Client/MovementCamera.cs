using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class GameApp
{
    double movementCameraTime;
    Vector3 movementCameraFeet;
    float movementCameraY;
    Guid movementCameraLocation;

    Vector3 MovementCamera(PlayerState player, double now)
    {
        var target = Movement.EyePosition(player);
        float dt = (float)Math.Clamp(now - movementCameraTime, 0, .1);
        bool reset = movementCameraTime == 0 || session!.World.Definition.LocationId != movementCameraLocation
            || Vector3.DistanceSquared(player.Position, movementCameraFeet) > 4;
        // Only vertical presentation is filtered. Airborne motion and mouse input
        // remain immediate, and teleports/world changes discard the old offset.
        if (reset) movementCameraY = target.Y;
        else
        {
            if (!player.Grounded) movementCameraY += player.Position.Y - movementCameraFeet.Y;
            movementCameraY += (target.Y - movementCameraY) * (1 - MathF.Exp(-dt / .04f));
        }
        movementCameraY = Math.Clamp(movementCameraY, player.Position.Y + .35f, target.Y + .65f);
        var desired = new Vector3(target.X, movementCameraY, target.Z);
        desired = session!.Physics.ConstrainCamera(player.Id, desired);
        movementCameraY = desired.Y;
        movementCameraFeet = player.Position;
        movementCameraTime = now;
        movementCameraLocation = session.World.Definition.LocationId;
        return desired;
    }
}
