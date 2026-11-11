using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

readonly record struct PresentedPrediction(PlayerState Player, BodyState[] Bodies);

// Only presentation is blended. Collision and prediction always use the exact
// fixed-step state, with the player and pushed/supporting bodies on one timeline.
sealed class PredictionPresentation
{
    Vector3 playerError;
    readonly Dictionary<int, (Vector3 Position, Quaternion Rotation)> bodyErrors = [];
    double correctedAt;
    const double HalfLife = .035;
    public void Clear()
    {
        playerError = Vector3.Zero;
        bodyErrors.Clear();
    }
    public static PresentedPrediction Interpolate(PhysicsSnapshot previous, PhysicsSnapshot current, int id, double tick)
    {
        float alpha = previous.Epoch == current.Epoch && current.Tick > previous.Tick ? (float)Math.Clamp((tick - previous.Tick) / (current.Tick - previous.Tick), 0, 1) : 1;
        var player = current.Players.Single(p => p.Id == id);
        var oldPlayer = previous.Players.FirstOrDefault(p => p.Id == id);
        if (oldPlayer.Id == id && Vector3.DistanceSquared(oldPlayer.Position, player.Position) < 4)
            player = player with
            {
                Position = Vector3.Lerp(oldPlayer.Position, player.Position, alpha)
            };
        var bodies = new BodyState[current.Bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            var b = current.Bodies[i];
            var old = previous.Bodies.FirstOrDefault(p => p.Id == b.Id);
            bodies[i] = old.Id == b.Id && Vector3.DistanceSquared(old.Position, b.Position) < 4
                ? b with
                {
                    Position = Vector3.Lerp(old.Position, b.Position, alpha),
                    Rotation = Quaternion.Slerp(old.Rotation, b.Rotation, alpha)
                } : b;
        }
        return new(player, bodies);
    }
    public PresentedPrediction Sample(PhysicsSnapshot previous, PhysicsSnapshot current, int id, double tick, double now)
    {
        var pose = Interpolate(previous, current, id, tick);
        float weight = (float)Math.Pow(.5, Math.Max(0, now - correctedAt) / HalfLife);
        for (int i = 0; i < pose.Bodies.Length; i++)
        {
            var b = pose.Bodies[i];
            if (bodyErrors.TryGetValue(b.Id, out var e))
                pose.Bodies[i] = b with
                {
                    Position = b.Position + e.Position * weight,
                    Rotation = Quaternion.Normalize(b.Rotation * Quaternion.Slerp(Quaternion.Identity, e.Rotation, weight))
                };
        }
        return pose with
        {
            Player = pose.Player with
            {
                Position = pose.Player.Position + playerError * weight
            }
        };
    }
    public void Correct(PresentedPrediction before, PresentedPrediction after, double now)
    {
        var delta = before.Player.Position - after.Player.Position;
        playerError = delta.LengthSquared() < 1 ? delta : Vector3.Zero;
        bodyErrors.Clear();
        foreach (var b in after.Bodies)
        {
            var old = before.Bodies.FirstOrDefault(p => p.Id == b.Id);
            if (old.Id == b.Id && Vector3.DistanceSquared(old.Position, b.Position) < 1)
                bodyErrors[b.Id] = (old.Position - b.Position, Quaternion.Normalize(Quaternion.Inverse(b.Rotation) * old.Rotation));
        }
        correctedAt = now;
    }
}
