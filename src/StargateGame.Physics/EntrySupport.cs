using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    readonly SortedDictionary<int, PropEntrySupport> entrySupports = [];
    public PropEntrySupport[] EntrySupports => entrySupports.Values.ToArray();
    void SetEntrySupports(PropEntrySupport[] values)
    {
        entrySupports.Clear();
        foreach (var value in values)
            entrySupports.Add(value.Body, value);
    }
    void BeginEntrySupport(int id)
    {
        var b = system.BodyInterface;
        var body = props[id];
        var position = b.GetPosition(body);
        var rotation = b.GetRotation(body);
        var points = PropGeometry.For(propDefinitions[id]).Vertices.Select(v => position + Vector3.Transform(v, rotation)).ToArray();
        float bottom = points.Min(v => v.Y);
        // A floor must actually support the uncut body before a continuation is made.
        interactionFilter ??= Native.Own(new InteractionFilter());
        interactionFilter.Ignore = body;
        interactionFilter.Player = BodyID.Invalid;
        float floor = float.NegativeInfinity;
        foreach (var point in points.Where(p => p.Y < bottom + .04f))
        {
            if (system.NarrowPhaseQuery.CastRay(new Ray(point + Vector3.UnitY * .08f, -Vector3.UnitY * .20f), out var hit, bodyFilter: interactionFilter)
                && system.BodyInterface.GetMotionType(hit.BodyID) == MotionType.Static)
                floor = Math.Max(floor, point.Y + .08f - hit.Fraction * .20f);
        }
        if (float.IsFinite(floor) && Math.Abs(bottom - floor) < .12f)
            entrySupports[id] = new(id, floor);
    }
    void ApplyEntrySupports()
    {
        var b = system.BodyInterface;
        foreach (var support in entrySupports.Values.ToArray())
        {
            if (!props.TryGetValue(support.Body, out var body) || !propClips.ContainsKey(support.Body) || completedProps.ContainsKey(support.Body) || heldProps.Contains(support.Body))
            {
                entrySupports.Remove(support.Body);
                continue;
            }
            var definition = propDefinitions[support.Body];
            var rotation = b.GetRotation(body);
            var position = b.GetPosition(body);
            var points = PropGeometry.For(definition).Vertices.Select(v => Vector3.Transform(v, rotation)).ToArray();
            var inertia = b.GetInverseInertia(body);
            float inverseMass = 1 / definition.Mass;
            // Unilateral velocity constraints at full-hull support points, with
            // gravity anticipation. No position correction or support for other actors.
            for (int iteration = 0; iteration < 4; iteration++)
                foreach (var offset in points)
                {
                    float gap = position.Y + offset.Y - support.Height;
                    if (gap > .035f)
                        continue;
                    var velocity = b.GetLinearVelocity(body) + Vector3.Cross(b.GetAngularVelocity(body), offset);
                    float target = Math.Clamp(-gap * .2f / Movement.Dt, 0, 1);
                    float deficit = target - (velocity.Y - Movement.Gravity * Movement.Dt);
                    if (deficit <= 0)
                        continue;
                    var torque = Vector3.Cross(offset, Vector3.UnitY);
                    float denominator = inverseMass + Vector3.Dot(torque, Vector3.TransformNormal(torque, inertia));
                    float impulse = Math.Min(definition.Mass * 2, deficit / Math.Max(.001f, denominator));
                    b.AddImpulse(body, Vector3.UnitY * impulse, position + offset);
                }
        }
    }
}
