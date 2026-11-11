using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    readonly SortedDictionary<int, CarryState> carries = [];
    readonly List<ShapeCastResult> carrySweepHits = [];
    public CarryState[] Carries => carries.Values.ToArray();
    public bool IsCarrying(int player) => carries.ContainsKey(player);
    public void DropCarried(int player) => carries.Remove(player);
    void DropCarriedProp(int prop)
    {
        foreach (var c in carries.Values.Where(c => c.Body == prop).ToArray())
            carries.Remove(c.Player);
    }
    static Vector3 Aim(float yaw, float pitch) => new(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch));
    public (PropDefinition? Prop, float Distance, Vector3 Point) PickProp(int player, Vector3 eye, Vector3 aim, float reach = 2.5f)
    {
        lock (NativeGate)
        {
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore = BodyID.Invalid;
            interactionFilter.Player = characters.TryGetValue(player, out var c) ? c.InnerBodyID : BodyID.Invalid;
            if (!system.NarrowPhaseQuery.CastRay(new Ray(eye, aim * reach), out var hit, bodyFilter: interactionFilter))
                return (default, 0, default);
            int id = props.FirstOrDefault(p => p.Value == hit.BodyID).Key;
            if (id == 0 || heldProps.Contains(id) || propClips.ContainsKey(id) && !propEmergences.ContainsKey(id) || dormantProps.ContainsKey(id) || carries.Values.Any(c => c.Body == id))
                return (default, 0, default);
            return (propDefinitions[id], hit.Fraction * reach, eye + aim * (hit.Fraction * reach));
        }
    }
    void CarryCommand(int player, PlayerInput input)
    {
        if (input.Carry == CarryAction.None)
            return;
        if (input.Carry is CarryAction.Drop or CarryAction.Throw)
        {
            if (carries.Remove(player, out var hold) && input.Carry == CarryAction.Throw && props.TryGetValue(hold.Body, out var body))
            {
                var velocity = system.BodyInterface.GetLinearVelocity(body);
                var target = ClampLength(velocity + Aim(input.Yaw, input.Pitch) * 6, 12);
                system.BodyInterface.AddImpulse(body, (target - velocity) * propDefinitions[hold.Body].Mass);
            }
            return;
        }
        if (carries.ContainsKey(player) || players[player].TransitFrozen)
            return;
        var p = players[player];
        var hit = PickProp(player, Movement.EyePosition(p), Aim(input.Yaw, input.Pitch));
        if (hit.Prop is not { } prop || prop.PersistentId != input.GrabItem || p.SupportId == prop.Id)
            return;
        var b = system.BodyInterface;
        var rotation = b.GetRotation(props[prop.Id]);
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -input.Yaw);
        carries[player] = new(player, prop.Id, prop.PersistentId, Vector3.Transform(hit.Point - b.GetPosition(props[prop.Id]), Quaternion.Conjugate(rotation)), Quaternion.Normalize(Quaternion.Conjugate(yaw) * rotation));
        b.ActivateBody(props[prop.Id]);
    }
    static Vector3 ClampLength(Vector3 value, float max) => value.LengthSquared() > max * max ? Vector3.Normalize(value) * max : value;
    void ApplyCarrying()
    {
        var b = system.BodyInterface;
        foreach (var hold in carries.Values.ToArray())
        {
            if (!players.TryGetValue(hold.Player, out var p) || p.TransitFrozen || !props.TryGetValue(hold.Body, out var body) || heldProps.Contains(hold.Body) || completedProps.ContainsKey(hold.Body) || dormantProps.ContainsKey(hold.Body) || p.SupportId == hold.Body)
            {
                carries.Remove(hold.Player);
                continue;
            }
            var def = propDefinitions[hold.Body];
            var position = b.GetPosition(body);
            var rotation = b.GetRotation(body);
            var eye = Movement.EyePosition(p);
            var aim = Aim(p.Yaw, p.Pitch);
            float distance = Math.Max(1.5f, def.HalfExtent.Length() + Movement.Radius + .3f);
            var target = eye + aim * distance;
            if (!HasTerrain(target, def.HalfExtent.Length() + .1f))
            {
                carries.Remove(hold.Player);
                continue;
            }
            // Sweep the visible hull; the hidden portion cannot hit source-world geometry.
            var delta = target - position;
            float error = delta.Length();
            int blocked = error > 1 ? hold.BlockedTicks + 1 : 0;
            if (blocked >= 18)
            {
                carries.Remove(hold.Player);
                continue;
            }
            carries[hold.Player] = hold with
            {
                BlockedTicks = blocked
            };
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore = body;
            interactionFilter.Player = characters[hold.Player].InnerBodyID;
            carrySweepHits.Clear();
            var shape = clippedPropShapes.GetValueOrDefault(hold.Body) ?? originalPropShapes[hold.Body];
            var matrix = Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position + Vector3.Transform(shape.CenterOfMass, rotation)));
            system.NarrowPhaseQuery.CastShape(shape, matrix, delta, Vector3.Zero, CollisionCollectorType.AllHit, carrySweepHits, bodyFilter: interactionFilter);
            float fraction = 1;
            foreach (var hit in carrySweepHits)
            {
                if (propClips.TryGetValue(hold.Body, out var clip) && clip.Distance(hit.ContactPointOn2) < -.005f)
                    continue;
                if (hit.Fraction > 0 || Vector3.Dot(delta, hit.PenetrationAxis) > 0)
                    fraction = Math.Min(fraction, Math.Max(0, hit.Fraction - .01f));
            }
            target = position + delta * fraction;
            const float omega = 4 * MathF.Tau;
            var acceleration = ClampLength((target - position) * (omega * omega) + (p.Velocity - b.GetLinearVelocity(body)) * (2 * omega) + Vector3.UnitY * Movement.Gravity, 30);
            b.AddForce(body, acceleration * def.Mass);
            var desired = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -p.Yaw) * hold.RelativeRotation);
            var difference = Quaternion.Normalize(desired * Quaternion.Conjugate(rotation));
            if (difference.W < 0)
                difference = new(-difference.X, -difference.Y, -difference.Z, -difference.W);
            var axis = new Vector3(difference.X, difference.Y, difference.Z);
            float length = axis.Length();
            var angle = length < 1e-5f ? Vector3.Zero : axis / length * (2 * MathF.Atan2(length, difference.W));
            const float angularOmega = 2 * MathF.Tau;
            var angularAcceleration = ClampLength(angle * (angularOmega * angularOmega) - b.GetAngularVelocity(body) * (2 * angularOmega), 30);
            if (Matrix4x4.Invert(b.GetInverseInertia(body), out var inertia))
                b.AddTorque(body, Vector3.TransformNormal(angularAcceleration, inertia));
        }
    }
    void SetCarries(CarryState[] state)
    {
        carries.Clear();
        foreach (var c in state)
            carries[c.Player] = c;
    }
}
