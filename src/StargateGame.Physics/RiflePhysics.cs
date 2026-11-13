using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    public (Vector3 End, bool Hit, int Body) ShootRifle(int player, float yaw, float pitch)
    {
        lock (NativeGate)
        {
            var p = players[player]; var origin = Movement.EyePosition(p);
            var direction = Aim(yaw, pitch);
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore = BodyID.Invalid; interactionFilter.Buildings = null;
            interactionFilter.Player = characters[player].InnerBodyID;
            if (!system.NarrowPhaseQuery.CastRay(new Ray(origin, direction*Rifle.Range), out var hit, bodyFilter: interactionFilter))
                return (origin+direction*Rifle.Range, false, 0);
            var point = origin+direction*(hit.Fraction*Rifle.Range);
            int id = props.FirstOrDefault(pair => pair.Value == hit.BodyID).Key;
            if (id != 0 && !heldProps.Contains(id) && !dormantProps.ContainsKey(id) && !propClips.ContainsKey(id))
            {
                ReleaseCompletedProp(id);
                system.BodyInterface.AddImpulse(hit.BodyID, direction*2.5f, point);
            }
            return (point, true, id);
        }
    }
}
