using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    readonly Dictionary<string, BodyID> entityBodies = [];
    sealed class InteractionFilter : BodyFilter
    {
        public BodyID Ignore = BodyID.Invalid, Player = BodyID.Invalid;
        protected override bool ShouldCollide(BodyID id) => id != Ignore && id != Player;
        protected override bool ShouldCollideLocked(Body body) => true;
    }
    InteractionFilter? interactionFilter;
    // Native broad/narrow phase uses the live clipped shapes, not reconstructed meshes.
    public bool InteractionBlocked(Vector3 origin, Vector3 direction, float distance, string? ignoreEntity = null, int ignorePlayer = 0, int ignoreProp = 0)
    {
        if (distance <= 0)
            return false;
        lock (NativeGate)
        {
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore = ignoreEntity is not null ? entityBodies.GetValueOrDefault(ignoreEntity, BodyID.Invalid) : props.GetValueOrDefault(ignoreProp, BodyID.Invalid);
            interactionFilter.Player = characters.TryGetValue(ignorePlayer, out var c) ? c.InnerBodyID : BodyID.Invalid;
            var ray = new Ray(origin, direction * distance);
            RayCastResult hit = default;
            return system.NarrowPhaseQuery.CastRay(ray, out hit, bodyFilter: interactionFilter);
        }
    }
}
