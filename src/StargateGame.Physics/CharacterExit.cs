using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    // A bounded local search shared by players and NPCs. Readiness is handled by
    // the caller: an occupied opening must never leave a character frozen in transit.
    public PlayerState ResolveCharacterExit(GateFrame gate, PlayerState desired, out bool launched, int ignorePlayer = 0, Guid ignoreNpc = default)
    {
        lock (NativeGate)
        {
            var ignore = characters.TryGetValue(ignorePlayer, out var c) ? c.InnerBodyID : BodyID.Invalid;
            var npc = npcMotions.Values.FirstOrDefault(n => n.Id == ignoreNpc);
            if (ignoreNpc != Guid.Empty && npc.Id == ignoreNpc) ignore = npcCharacters[npc.Slot].InnerBodyID;
            var shape = desired.Crouched ? crouchShape : characterShape;
            var right = Vector3.Transform(Vector3.UnitX, gate.Rotation);
            bool Clear(Vector3 p) => HasTerrain(p, 2) && ClearShape(shape, p, Quaternion.Identity, ignore);
            launched = false;
            foreach (float forward in new[] { 0f, .6f, 1.2f, 2f })
                foreach (float side in new[] { 0f, -.7f, .7f })
                    foreach (float up in new[] { 0f, .6f })
                    {
                        var p = desired.Position + gate.Normal * forward + right * side + Vector3.UnitY * up;
                        if (Clear(p)) return desired with { Position = p, TransitFrozen = false };
                    }
            launched = true;
            var centre = gate.Centre - Vector3.UnitY * Movement.BodyHeight(desired) / 2 + gate.Normal * (Movement.Radius + .08f);
            var velocity = gate.Normal * 12 + Vector3.UnitY * 4;
            return desired with { Position = centre, Velocity = velocity, ArrivalMomentum = new(velocity.X, 0, velocity.Z),
                ArrivalMomentumActive = true, AirSpeedLimit = 12, TransitFrozen = false, Grounded = false, SupportId = 0, GroundVelocity = default, GroundNormal = default };
        }
    }
}
