using System.Numerics;
using JoltPhysicsSharp;
using StargateGame.Shared;
using StargateGame.Building;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    // Changes only when collision bodies are replaced, not on simulation ticks.
    public long CableCollisionRevision => topology;
    Vector3 CableNormal(RayCastResult hit,Vector3 point)
    {
        var locks=system.BodyLockInterface;
        locks.LockRead(hit.BodyID,out var read);
        try{return read.Succeeded ? read.Body.GetWorldSpaceSurfaceNormal(new SubShapeID(hit.subShapeID2),point) : Vector3.UnitY;}
        finally{locks.UnlockRead(read);}
    }
    public bool CableTransform(CableAnchor anchor, out Matrix4x4 transform, BodyState[]? presented = null)
    {
        transform = Matrix4x4.Identity;
        if (anchor.Kind == "world") return HasTerrain(anchor.Point, .1f);
        if (anchor.Kind == "building")
        {
            var piece = PosedChanges.Pieces.FirstOrDefault(p => p.Id == anchor.Owner);
            var structure = piece is null ? null : Changes.Structures.FirstOrDefault(s => s.Id == piece.StructureId);
            if (piece is null || structure is null) return false;
            transform = piece.Kind == PieceKind.Prefab ? BuildingPrefabs.Transform(structure,piece) : BuildingGeometry.Transform(structure);
            if(piece.Kind==PieceKind.Door)
            {
                var pivot=new Vector3(0,0,-.045f);
                transform=Matrix4x4.CreateTranslation(-pivot)*Matrix4x4.CreateRotationY(piece.DoorAngle ?? (piece.Open?MathF.PI/2:0))*Matrix4x4.CreateTranslation(pivot)*DoorGeometry.Root(structure,piece);
            }
            return true;
        }
        if (anchor.Kind == "entity")
        {
            var entity = World.Definition.Entities.FirstOrDefault(e => e.Id == anchor.Entity);
            if (entity is null) return false;
            transform = entity.Transform; return true;
        }
        var prop = World.Definition.Props.FirstOrDefault(p => p.PersistentId == anchor.Owner);
        if (prop is null || dormantProps.ContainsKey(prop.Id) || heldProps.Contains(prop.Id)) return false;
        var body = (presented ?? Bodies()).FirstOrDefault(b => b.Id == prop.Id);
        if (body.Id != prop.Id) return false;
        transform = Matrix4x4.CreateFromQuaternion(body.Rotation) * Matrix4x4.CreateTranslation(body.Position);
        return true;
    }
    public bool CablePoint(CableAnchor anchor, out Vector3 point, out Vector3 normal, BodyState[]? presented = null)
    {
        bool found = CableTransform(anchor,out var transform,presented);
        point = Vector3.Transform(anchor.Point,transform);
        Matrix4x4.Invert(transform,out var inverse);
        normal = Vector3.Normalize(Vector3.TransformNormal(anchor.Normal,Matrix4x4.Transpose(inverse)));
        return found;
    }
    public bool CableOwnerExists(CableAnchor a) => a.Kind switch
    {
        "building" => Changes.Pieces.Any(p=>p.Id==a.Owner),
        "prop" => World.Definition.Props.Any(p=>p.PersistentId==a.Owner) && !completedProps.ContainsKey(World.Definition.Props.First(p=>p.PersistentId==a.Owner).Id),
        "entity" => World.Definition.Entities.Any(e=>e.Id==a.Entity),
        _ => !HasTerrain(a.Point,.1f) || CableRay(a.Point+a.Normal*.1f,a.Point-a.Normal*.1f) is not null
    };
    public (Vector3 Point, Vector3 Normal)? CableRay(Vector3 from, Vector3 to, int player = 0)
    {
        lock (NativeGate)
        {
            var delta=to-from;
            if(delta.LengthSquared()<1e-10f)return null;
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore=BodyID.Invalid; interactionFilter.Buildings=null;
            interactionFilter.Player=characters.TryGetValue(player,out var c)?c.InnerBodyID:BodyID.Invalid;
            if(!system.NarrowPhaseQuery.CastRay(new Ray(from,delta),out var hit,bodyFilter:interactionFilter))return null;
            var point=from+delta*hit.Fraction;
            var normal=CableNormal(hit,point);
            return(point,Vector3.Normalize(normal));
        }
    }
    public CableAnchor? CablePick(Vector3 origin, Vector3 direction, int player, float reach = 20)
    {
        lock(NativeGate)
        {
            interactionFilter ??= Native.Own(new InteractionFilter());
            interactionFilter.Ignore=BodyID.Invalid;interactionFilter.Buildings=null;
            interactionFilter.Player=characters.TryGetValue(player,out var c)?c.InnerBodyID:BodyID.Invalid;
            if(!system.NarrowPhaseQuery.CastRay(new Ray(origin,direction*reach),out var hit,bodyFilter:interactionFilter))return null;
            if(characters.Values.Concat(npcCharacters).Any(c=>c.InnerBodyID==hit.BodyID))return null;
            var point=origin+direction*(reach*hit.Fraction);
            var normal=Vector3.Normalize(CableNormal(hit,point));
            var anchor=new CableAnchor("world",Guid.Empty,"",point,normal);
            var prop=props.FirstOrDefault(p=>p.Value==hit.BodyID);
            if(prop.Key!=0)anchor=anchor with{Kind="prop",Owner=propDefinitions[prop.Key].PersistentId};
            else
            {
                var entity=entityBodies.FirstOrDefault(p=>p.Value==hit.BodyID);
                if(entity.Key is not null)anchor=anchor with{Kind="entity",Entity=entity.Key};
                else
                {
                    var building=BuildingQuery.Ray(origin,direction,reach);
                    if(building.Piece is not null && Vector3.Distance(building.Point,point)<.08f)
                        anchor=anchor with{Kind="building",Owner=building.Piece.Id};
                }
            }
            if(!CableTransform(anchor,out var transform)||!Matrix4x4.Invert(transform,out var inverse))return null;
            return anchor with{Point=Vector3.Transform(point,inverse),Normal=Vector3.Normalize(Vector3.TransformNormal(normal,Matrix4x4.Transpose(transform)))};
        }
    }
}
