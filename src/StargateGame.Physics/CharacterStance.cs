using StargateGame.Shared;
using System.Numerics;
using JoltPhysicsSharp;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    readonly List<ShapeCastResult> cameraSweepHits = [];
    Shape? cameraProbe;
    public Vector3 ConstrainCamera(int id, Vector3 desired)
    {
        lock (NativeGate)
        {
            var origin = Movement.EyePosition(players[id]);
            var delta = desired - origin;
            if (delta.LengthSquared() < .000001f) return origin;
            cameraProbe ??= Own(new SphereShape(.12f));
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore = BodyID.Invalid;
            interactionFilter.Player = characters[id].InnerBodyID;
            cameraSweepHits.Clear();
            system.NarrowPhaseQuery.CastShape(cameraProbe, Matrix4x4.Transpose(Matrix4x4.CreateTranslation(origin)),
                delta, Vector3.Zero, CollisionCollectorType.AllHit, cameraSweepHits, bodyFilter: interactionFilter);
            float fraction = cameraSweepHits.Count == 0 ? 1 : Math.Max(0, cameraSweepHits.Min(h => h.Fraction) - .01f);
            return origin + delta * fraction;
        }
    }

    bool SetStance(int id, bool crouched, bool checkClearance)
    {
        if (players[id].Crouched == crouched)
            return true;
        var shape = crouched ? crouchShape : characterShape;
        // Both translated capsules have their origin at the feet. Growing is
        // transactional: the native helper changes neither shape on obstruction.
        if (!Native.SG_CharacterShape(characters[id].Handle, shape.Handle, system.Handle, allocator,
                CharacterLayer, checkClearance && !crouched))
            return false;
        players[id] = players[id] with { Crouched = crouched };
        return true;
    }
}
