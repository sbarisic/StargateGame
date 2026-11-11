using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

// Interpolate by server tick, not by time since the last packet arrived.
sealed class RemotePresentation
{
    readonly Queue<PhysicsSnapshot> frames = [];
    public void Clear() => frames.Clear();
    public void Add(PhysicsSnapshot frame)
    {
        if (frames.Count > 0 && frames.Last().Epoch != frame.Epoch)
            frames.Clear();
        frames.Enqueue(frame);
        while (frames.Count > 16)
            frames.Dequeue();
    }
    public IEnumerable<PlayerState> Sample(double tick, int localId)
    {
        if (frames.Count == 0)
            yield break;
        var from = frames.Peek();
        var to = from;
        foreach (var frame in frames)
        {
            to = frame;
            if (frame.Tick >= tick)
                break;
            from = frame;
        }
        float alpha = to.Tick > from.Tick ? (float)Math.Clamp((tick - from.Tick) / (to.Tick - from.Tick), 0, 1) : 1;
        foreach (var latest in frames.Last().Players)
        {
            if (latest.Id == localId)
                continue;
            var a = from.Players.FirstOrDefault(p => p.Id == latest.Id);
            var b = to.Players.FirstOrDefault(p => p.Id == latest.Id);
            if (a.Id == 0 || b.Id == 0)
            {
                yield return latest;
                continue;
            }
            yield return b with
            {
                Crouched = alpha < 1 ? a.Crouched : b.Crouched,
                Position = Vector3.DistanceSquared(a.Position, b.Position) < 4 ? Vector3.Lerp(a.Position, b.Position, alpha) : b.Position,
                Yaw = a.Yaw + MathF.IEEERemainder(b.Yaw - a.Yaw, MathF.Tau) * alpha,
                Pitch = float.Lerp(a.Pitch, b.Pitch, alpha)
            };
        }
    }
}
