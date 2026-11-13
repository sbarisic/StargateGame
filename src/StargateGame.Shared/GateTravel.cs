using System.Numerics;
using System.Security.Cryptography;
using System.Text;
namespace StargateGame.Shared;

public readonly record struct GateFrame(Vector3 Centre, Quaternion Rotation, float Radius)
{
    public Vector3 Normal => Vector3.Transform(Vector3.UnitZ, Rotation);
    public Vector3 Local(Vector3 p) => Vector3.Transform(p - Centre, Quaternion.Inverse(Rotation));
    public Vector3 World(Vector3 p) => Centre + Vector3.Transform(p, Rotation);
    public float Distance(Vector3 p) => Vector3.Dot(p - Centre, Normal);
    public static GateFrame From(StaticEntity gate)
    {
        if (Vector3.DistanceSquared(Vector3.Transform(Vector3.UnitY, gate.Rotation), Vector3.UnitY) > .0001f)
            throw new InvalidOperationException("Gate travel requires upright gates.");
        return new(Vector3.Transform(new Vector3(0, 3.063692f, 0), gate.Transform), gate.Rotation, 2.30f * gate.Scale);
    }
    public Quaternion MappingTo(GateFrame destination)
    {
        if (Math.Abs(Radius - destination.Radius) > .001f)
            throw new InvalidOperationException("Gate apertures must have equal scale.");
        return Quaternion.Normalize(destination.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI) * Quaternion.Inverse(Rotation));
    }
}
public enum PropClipMode
{
    Entry, Emergence
}
public sealed record PropClip(int Body, GateFrame Gate, PropClipMode Mode = PropClipMode.Entry, Guid Connection = default);
public enum TransitPhase
{
    Captured, Preparing, Ready, Committing, Released, Returning, Cancelled
}
public sealed record TransitNotice(Guid Id, Guid Connection, Guid Source, Guid Destination, TransitPhase Phase, long CaptureTick, long Transfer = 0, string? Reason = null);
public static class GateTravel
{
    public const double MinimumTransitSeconds = 2;
    public const long MaximumConnectionTicks = 38 * 60 * 60;
    public const float EmergenceLead = .02f;
    public static bool ConnectionExpired(long now, long openTick) => now - openTick >= MaximumConnectionTicks;
    // Shared by the clipped collider and authoritative handoff. Never leave
    // an empty collision proxy waiting for a different capture plane.
    public const float PropCompletionDepth = .002f;
    public static Guid PropIdentity(Guid location, int slot) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"StargateGame/prop/v1/{location:D}/{slot}")).AsSpan(0, 16));
    public static bool PlayerCrossing(GateFrame gate, Vector3 previous, Vector3 current, float height = Movement.Height)
    {
        if (Vector3.DistanceSquared(previous, current) > 100)
            return false;
        var a = gate.Local(previous + Vector3.UnitY * height * .5f);
        var b = gate.Local(current + Vector3.UnitY * height * .5f);
        // Trigger at the leading capsule surface, before the gate rim can stop
        // the controller. Also recover an inward-moving actor already touching it.
        float plane = Movement.Radius + .06f;
        if (b.Z >= a.Z - .00001f || b.Z > plane || a.Z < -Movement.Radius)
            return false;
        var p = Vector3.Lerp(a, b, Math.Clamp((a.Z - plane) / (a.Z - b.Z), 0, 1));
        float y = Math.Max(0, Math.Abs(p.Y) - (height * .5f - Movement.Radius));
        return p.X * p.X + y * y <= MathF.Pow(gate.Radius - .015f, 2);
    }
    public static (float Min, float Max, bool Fits) Extents(GateFrame gate, BodyState body, PropDefinition definition)
    {
        var hull = PropGeometry.For(definition);
        var points = hull.Vertices.Select(v => gate.Local(body.Position + Vector3.Transform(v, body.Rotation))).ToArray();
        float min = points.Min(p => p.Z), max = points.Max(p => p.Z);
        bool fits = true;
        float radius2 = (gate.Radius - .015f) * (gate.Radius - .015f);
        // Only the section actually crossing the opening must clear its rim.
        // Hidden corners can move outside the projected circle after passing through.
        foreach (var (a, b) in hull.Edges)
        {
            var p = points[a];
            var q = points[b];
            if ((p.Z < 0) != (q.Z < 0))
            {
                var intersection = Vector3.Lerp(p, q, p.Z / (p.Z - q.Z));
                if (intersection.X * intersection.X + intersection.Y * intersection.Y > radius2)
                    fits = false;
            }
            if (Math.Abs(p.Z) < .00001f && p.X * p.X + p.Y * p.Y > radius2)
                fits = false;
        }
        return (min, max, fits);
    }
    public static bool SweptFits(GateFrame gate, BodyState a, BodyState b, PropDefinition definition)
    {
        float travel = Vector3.Distance(a.Position, b.Position) + 2 * MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(a.Rotation, b.Rotation)), 0, 1)) * definition.HalfExtent.Length();
        if (travel > 10)
            return false;
        int steps = Math.Clamp((int)MathF.Ceiling(travel / .025f), 1, 512);
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            var e = Extents(gate, b with
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t)
            }, definition);
            if (e.Min < .02f && e.Max > -.02f && !e.Fits)
                return false;
        }
        return true;
    }
    public static BodyState Exit(GateFrame source, GateFrame destination, BodyState body, PropDefinition definition)
    {
        var r = source.MappingTo(destination);
        var result = body with
        {
            Position = destination.Centre + Vector3.Transform(body.Position - source.Centre, r),
            Rotation = Quaternion.Normalize(r * body.Rotation),
            LinearVelocity = Vector3.Transform(body.LinearVelocity, r),
            AngularVelocity = Vector3.Transform(body.AngularVelocity, r),
            Sleeping = false
        };
        // The receiving plane emits the leading edge, not an already cleared body.
        // Preserve the exact transformed velocity, including a reverse component.
        float leading = Extents(destination, result, definition).Max;
        return result with
        {
            Position = result.Position + destination.Normal * (EmergenceLead - leading)
        };
    }
    public static PlayerState Exit(GateFrame source, GateFrame destination, PlayerState player)
    {
        var r = source.MappingTo(destination);
        var p = destination.Centre + Vector3.Transform(player.Position - source.Centre, r);
        p += destination.Normal * Math.Max(0, Movement.Radius + .03f - destination.Distance(p));
        var forward = Vector3.Transform(new Vector3(MathF.Sin(player.Yaw), 0, -MathF.Cos(player.Yaw)), r);
        float yaw = MathF.Atan2(forward.X, -forward.Z);
        var velocity = Vector3.Transform(player.Velocity, r);
        return player with
        {
            Position = p,
            Yaw = yaw,
            Velocity = velocity,
            ArrivalMomentum = new(velocity.X, 0, velocity.Z),
            TransitFrozen = false,
            ArrivalMomentumActive = true,
            Grounded = false,
            SupportId = 0,
            GroundVelocity = default,
            GroundNormal = default
        };
    }
    public static Vector3[] ClippedVertices(GateFrame gate, BodyState body, PropDefinition definition)
    {
        var hull = PropGeometry.For(definition);
        var distances = hull.Vertices.Select(v => gate.Distance(body.Position + Vector3.Transform(v, body.Rotation))).ToArray();
        List<Vector3> points = [];
        for (int i = 0; i < distances.Length; i++)
            if (distances[i] >= 0)
                points.Add(hull.Vertices[i]);
        foreach (var (a, b) in hull.Edges)
            if ((distances[a] < 0) != (distances[b] < 0))
                points.Add(Vector3.Lerp(hull.Vertices[a], hull.Vertices[b], distances[a] / (distances[a] - distances[b])));
        return points.Distinct().ToArray();
    }
}
