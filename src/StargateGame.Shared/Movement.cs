using System.Numerics;
namespace StargateGame.Shared;

public readonly record struct PlayerInput(long Sequence, float Strafe, float Forward, float Yaw, float Pitch, bool Walk, bool Jump, long TargetTick = 0, bool SwimRise = false, bool SwimDive = false, CarryAction Carry = CarryAction.None, Guid GrabItem = default, bool Crouch = false)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Enum.IsDefined(Carry) && (Carry != CarryAction.Grab || GrabItem != Guid.Empty) && Sequence > 0 && float.IsFinite(Strafe) && float.IsFinite(Forward)
        && MathF.Abs(Strafe) <= 1 && MathF.Abs(Forward) <= 1 && float.IsFinite(Yaw)
        && MathF.Abs(Yaw) <= MathF.PI * 2 && float.IsFinite(Pitch) && MathF.Abs(Pitch) <= 1.55f;
    [System.Text.Json.Serialization.JsonIgnore] public PlayerInput Held => this with { Jump = false, Carry = CarryAction.None, GrabItem = default };
    [System.Text.Json.Serialization.JsonIgnore] public PlayerInput Neutral => this with { Strafe = 0, Forward = 0, Walk = false, Crouch = false, Jump = false, SwimRise = false, SwimDive = false, Carry = CarryAction.None, GrabItem = default };
}
public readonly record struct PlayerState(int Id, string Name, Vector3 Position, Vector3 Velocity, float Yaw, float Pitch, bool Grounded, long LastInput,
    int SupportId = 0, Vector3 GroundNormal = default, Vector3 GroundVelocity = default, bool Swimming = false, float Submersion = 0, GroundSurface Surface = GroundSurface.Unknown, Vector3 ArrivalMomentum = default, bool TransitFrozen = false, bool ArrivalMomentumActive = false,
    bool Crouched = false, float AirSpeedLimit = 0);
public readonly record struct BodyState(int Id, Vector3 Position, Quaternion Rotation, Vector3 LinearVelocity, Vector3 AngularVelocity, bool Sleeping);
public readonly record struct AppliedCommand(int PlayerId, PlayerInput Input, int MissingTicks);
public sealed record PhysicsSnapshot(long Tick, long Epoch, PlayerState[] Players, BodyState[] Bodies, AppliedCommand[] Commands, PropDefinition[]? Registry = null, PropClip[]? Clips = null, GateFrame? TravelGate = null, CarryState[]? Carries = null, PropEntrySupport[]? EntrySupports = null);

public static class Movement
{
    public const float Dt = 1f / 60, Radius = .32f, Height = 1.8f, EyeHeight = 1.65f;
    public const float CrouchHeight = 1.15f, CrouchEyeHeight = 1;
    public const float RunSpeed = 5.4f, WalkSpeed = 2.7f, CrouchSpeed = 1.8f, JumpSpeed = 5.1f, Gravity = 18;
    public const float GroundAcceleration = 7, GroundFriction = 6, StopSpeed = 2, AirAcceleration = 1.5f, AirWishSpeedCap = .6f;
    public static float BodyHeight(PlayerState state) => state.Crouched ? CrouchHeight : Height;
    public static float ViewHeight(PlayerState state) => state.Crouched ? CrouchEyeHeight : EyeHeight;
    public static Vector3 EyePosition(PlayerState state) => state.Position + Vector3.UnitY * ViewHeight(state);
    // Two 10 kg crates resist about 216 N at the world's .6 friction / 18 m/s²
    // gravity. Leave headroom for continuous pushing instead of stick/slip.
    public const float PushStrength = 300;
    public static Vector3 DesiredVelocity(PlayerInput input, bool crouched = false)
    {
        Vector2 move = new(input.Strafe, input.Forward);
        if (move.LengthSquared() > 1)
            move = Vector2.Normalize(move);
        float sin = MathF.Sin(input.Yaw), cos = MathF.Cos(input.Yaw), speed = crouched ? CrouchSpeed : input.Walk ? WalkSpeed : RunSpeed;
        return new((move.X * cos + move.Y * sin) * speed, 0, (move.X * sin - move.Y * cos) * speed);
    }

    // Inputs and integration are shared by prediction and the authoritative world.
    // The caller removes/adds support velocity; gravity and contacts remain in Jolt.
    public static Vector3 Accelerate(Vector3 velocity, Vector3 wish, bool grounded, float airSpeedLimit, Vector3 groundNormal = default)
    {
        float vertical = velocity.Y;
        float wishSpeed = wish.Length();
        if (grounded)
        {
            var normal = groundNormal.Y >= .7f ? Vector3.Normalize(groundNormal) : Vector3.UnitY;
            velocity -= normal * Vector3.Dot(velocity, normal);
            wish -= normal * Vector3.Dot(wish, normal);
            if (wish.LengthSquared() > .000001f) wish = Vector3.Normalize(wish) * wishSpeed;
            float speed = velocity.Length();
            if (speed > 0)
                velocity *= Math.Max(0, speed - Math.Max(speed, StopSpeed) * GroundFriction * Dt) / speed;
        }
        else velocity.Y = 0;
        if (wishSpeed > 0)
        {
            var direction = wish / wishSpeed;
            float target = grounded ? wishSpeed : Math.Min(wishSpeed, AirWishSpeedCap);
            float add = target - Vector3.Dot(velocity, direction);
            float oldSpeed = velocity.Length();
            if (add > 0)
                velocity += direction * Math.Min(add, (grounded ? GroundAcceleration : AirAcceleration) * wishSpeed * Dt);
            // Never erase external/platform/gate momentum, but do not let air input
            // manufacture speed beyond the takeoff envelope.
            float limit = Math.Max(Math.Max(RunSpeed, airSpeedLimit), oldSpeed);
            if (!grounded && velocity.LengthSquared() > limit * limit)
                velocity *= limit / velocity.Length();
        }
        if (!grounded) velocity.Y = vertical;
        return velocity;
    }
}

public enum CarryAction
{
    None, Grab, Drop, Throw
}
public sealed record CarryState(int Player, int Body, Guid PersistentId, Vector3 GrabOffset, Quaternion RelativeRotation, int BlockedTicks = 0);

public sealed record PropEntrySupport(int Body, float Height);
