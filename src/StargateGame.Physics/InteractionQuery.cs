using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
using StargateGame.Building;

namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    readonly Dictionary<string, BodyID> entityBodies = [];
    sealed class InteractionFilter : BodyFilter
    {
        public BodyID Ignore = BodyID.Invalid, Player = BodyID.Invalid;
        public HashSet<BodyID>? Buildings;
        protected override bool ShouldCollide(BodyID id) => id != Ignore && id != Player && !(Buildings?.Contains(id) ?? false);
        protected override bool ShouldCollideLocked(Body body) => body.ObjectLayer != StairPlayerLayer;
    }
    InteractionFilter? interactionFilter;
    readonly HashSet<BodyID> interactionBuildings = [];
    readonly BuildingQuery buildingQuery = new();
    public BuildingQuery BuildingQuery { get { buildingQuery.Update(Changes); return buildingQuery; } }
    // Buildings are tested individually by BuildingQuery, never ignored by batch.
    public bool BuildingVisibilityBlocked(Vector3 eye, Vector3 aim, float distance, int player, Guid ignore = default)
        => BuildingQuery.Ray(eye, aim, distance, ignore).Piece is not null ||
           InteractionBlocked(eye, aim, distance, ignorePlayer: player, ignoreBuildings: true);
    // Native broad/narrow phase uses the live clipped shapes, not reconstructed meshes.
    public Vector3? SpawnSurface(Vector3 origin, Vector3 direction, float distance, int player)
    {
        lock (NativeGate)
        {
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore = BodyID.Invalid;
            interactionFilter.Player = characters.TryGetValue(player, out var c) ? c.InnerBodyID : BodyID.Invalid;
            interactionFilter.Buildings = null;
            return system.NarrowPhaseQuery.CastRay(new Ray(origin,direction*distance),out var hit,bodyFilter:interactionFilter)
                ? origin+direction*(distance*hit.Fraction) : null;
        }
    }
    public bool InteractionBlocked(Vector3 origin, Vector3 direction, float distance, string? ignoreEntity = null, int ignorePlayer = 0, int ignoreProp = 0, bool ignoreBuildings = false)
    {
        if (distance <= 0)
            return false;
        lock (NativeGate)
        {
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore = ignoreEntity is not null ? entityBodies.GetValueOrDefault(ignoreEntity, BodyID.Invalid) : props.GetValueOrDefault(ignoreProp, BodyID.Invalid);
            interactionFilter.Player = characters.TryGetValue(ignorePlayer, out var c) ? c.InnerBodyID : BodyID.Invalid;
            interactionFilter.Buildings = ignoreBuildings ? interactionBuildings : null;
            var ray = new Ray(origin, direction * distance);
            RayCastResult hit = default;
            return system.NarrowPhaseQuery.CastRay(ray, out hit, bodyFilter: interactionFilter);
        }
    }
}
