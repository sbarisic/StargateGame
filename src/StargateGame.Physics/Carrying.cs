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
            interactionFilter.Buildings = null;
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
        if (input.PropRotation != Vector2.Zero && carries.TryGetValue(player, out var rotating) && rotating.PhysicsGun && !players[player].TransitFrozen)
        {
            // Camera-relative pitch/yaw; the view remains fixed while E is held.
            var view = Quaternion.CreateFromYawPitchRoll(-input.Yaw, input.Pitch, 0);
            var turn = Quaternion.CreateFromAxisAngle(Vector3.Transform(Vector3.UnitY, view), -input.PropRotation.X)
                * Quaternion.CreateFromAxisAngle(Vector3.Transform(Vector3.UnitX, view), input.PropRotation.Y);
            var yawFrame = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -input.Yaw);
            carries[player] = rotating with { RelativeRotation = Quaternion.Normalize(
                Quaternion.Conjugate(yawFrame) * turn * yawFrame * rotating.RelativeRotation) };
        }
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
        if (input.Carry is CarryAction.PullCloser or CarryAction.PushFarther)
        {
            if (carries.TryGetValue(player, out var current) && current.PhysicsGun)
                carries[player] = current with { Distance = Math.Clamp(current.Distance +
                    (input.Carry == CarryAction.PullCloser ? -1.5f : 1.5f), Movement.PhysicsGunMinDistance, Movement.PhysicsGunRange) };
            return;
        }
        bool gun=input.Carry==CarryAction.PhysicsGrab;
        if (gun && World.Definition.Fixture != "gate-pair") return;
        if (carries.ContainsKey(player) || players[player].TransitFrozen)
            return;
        var p = players[player];
        var hit = PickProp(player, Movement.EyePosition(p), Aim(input.Yaw, input.Pitch),gun ? Movement.PhysicsGunRange : 2.5f);
        if (hit.Prop is not { } prop || prop.PersistentId != input.GrabItem || p.SupportId == prop.Id)
            return;
        // Completed gate transfers leave a prop kinematic until explicitly released.
        // Only a validated dynamic-prop grab reaches here; fixed geometry and
        // props paused for missing collision or active transit stay excluded.
        ReleaseCompletedProp(prop.Id);
        var b = system.BodyInterface;
        b.SetMotionType(props[prop.Id], MotionType.Dynamic, Activation.Activate);
        b.ActivateBody(props[prop.Id]);
        var rotation = b.GetRotation(props[prop.Id]);
        var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -input.Yaw);
        carries[player] = new(player, prop.Id, prop.PersistentId, Vector3.Transform(hit.Point - b.GetPosition(props[prop.Id]), Quaternion.Conjugate(rotation)), Quaternion.Normalize(Quaternion.Conjugate(yaw) * rotation), PhysicsGun:gun, Distance:Math.Clamp(hit.Distance,Movement.PhysicsGunMinDistance,Movement.PhysicsGunRange));
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
            float distance = hold.PhysicsGun ? hold.Distance : Math.Max(1.5f, def.HalfExtent.Length() + Movement.Radius + .3f);
            var target = eye + aim * distance - (hold.PhysicsGun ? Vector3.Transform(hold.GrabOffset,rotation) : Vector3.Zero);
            if (!HasTerrain(target, def.HalfExtent.Length() + .1f))
            {
                if (!hold.PhysicsGun)
                {
                    carries.Remove(hold.Player);
                    continue;
                }
                // Keep the hold at the last loaded position instead of dropping it
                // when the extended ray reaches outside streamed collision.
                target = position;
            }
            // Sweep the visible hull; the hidden portion cannot hit source-world geometry.
            var delta = target - position;
            float error = delta.Length();
            int blocked = !hold.PhysicsGun && error > 1 ? hold.BlockedTicks + 1 : 0;
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
            interactionFilter.Buildings = null;
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
                // Resting contacts must not pin a prop while pulling it away or
                // along the floor. Keep a world-space skin, not 1% of a long ray.
                float intoSurface = Vector3.Dot(delta, hit.PenetrationAxis);
                if (hit.Fraction <= .00001f && intoSurface <= .0001f * Math.Max(1, delta.Length()))
                    continue;
                float skinFraction = .005f / Math.Max(.005f, delta.Length());
                fraction = Math.Min(fraction, Math.Max(0, hit.Fraction - skinFraction));
            }
            target = position + delta * fraction;
            const float omega = 4 * MathF.Tau;
            var acceleration = ClampLength((target - position) * (omega * omega) + (p.Velocity - b.GetLinearVelocity(body)) * (2 * omega) + Vector3.UnitY * Movement.Gravity, 30);
            if (hold.PhysicsGun)
            {
                // Implicit critically damped servo remains stable at the fixed tick
                // even with high stiffness. Retain collision sweeps and mass scaling.
                const float frequency = 90;
                float kp = frequency * frequency, kd = 2 * frequency;
                float divisor = 1 + kd * Movement.Dt + kp * Movement.Dt * Movement.Dt;
                acceleration = ClampLength(((target - position) * kp +
                    (p.Velocity - b.GetLinearVelocity(body)) * (kd + kp * Movement.Dt)) / divisor, 4000)
                    + Vector3.UnitY * Movement.Gravity;
                b.ActivateBody(body);
            }
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
            if (hold.PhysicsGun)
            {
                const float frequency = 60;
                float kp = frequency * frequency, kd = 2 * frequency;
                angularAcceleration = ClampLength((angle * kp - b.GetAngularVelocity(body) *
                    (kd + kp * Movement.Dt)) / (1 + kd * Movement.Dt + kp * Movement.Dt * Movement.Dt), 1000);
            }
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
