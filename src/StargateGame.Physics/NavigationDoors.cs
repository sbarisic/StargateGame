using System.Numerics;
using StargateGame.Shared;
using StargateGame.Building;
namespace StargateGame.Physics;

// Traversal links describe the closed doorway, independently of its moving leaf.
public sealed record NavigationDoor(Guid Id,Vector3 Centre,Vector3 Normal,Vector3 A,Vector3 B,float HalfWidth,bool Open);
public sealed partial class PhysicsWorld
{
    readonly Dictionary<Guid,(BuildingPiece Piece,StructureRecord Structure,NavigationDoor Link)> navigationDoors=[];
    public NavigationDoor[] NavigationDoors()
    {
        var result=new List<NavigationDoor>();
        foreach(var door in Changes.Pieces.Where(BuildingPrefabs.IsDoor))
        {
            var closed=door with{Open=false};var structure=Changes.Structures.Single(s=>s.Id==door.StructureId);
            if(!navigationDoors.TryGetValue(door.Id,out var cached)||cached.Piece!=closed||cached.Structure!=structure)
            {
                var root=DoorGeometry.Root(structure,closed);Matrix4x4.Invert(root,out var inverse);
                var vertices=BuildingGeometry.Mesh(Changes,closed).SelectMany(t=>new[]{t.A,t.B,t.C}).Select(p=>Vector3.Transform(p,inverse)).ToArray();
                if(vertices.Length==0)continue;
                var lo=vertices.Aggregate(new Vector3(float.PositiveInfinity),Vector3.Min);
                var hi=vertices.Aggregate(new Vector3(float.NegativeInfinity),Vector3.Max);
                var centre=new Vector3((lo.X+hi.X)*.5f,lo.Y,(lo.Z+hi.Z)*.5f);
                var envelope=DoorGeometry.For(closed).Envelope;
                var a=new Vector3(centre.X,centre.Y,Math.Min(lo.Z,envelope.Min.Z)-Movement.Radius-.3f);
                var b=new Vector3(centre.X,centre.Y,Math.Max(hi.Z,envelope.Max.Z)+Movement.Radius+.3f);
                var link=new NavigationDoor(door.Id,Vector3.Transform(centre,root),Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,root)),
                    Vector3.Transform(a,root),Vector3.Transform(b,root),(hi.X-lo.X)*.5f,false);
                navigationDoors[door.Id]=cached=(closed,structure,link);
            }
            result.Add(cached.Link with{Open=door.Open && (!hingedDoors.TryGetValue(door.Id,out var moving) || moving.Angle >= MathF.PI / 2 - .01f)});
        }
        foreach(var id in navigationDoors.Keys.Except(result.Select(d=>d.Id)).ToArray())navigationDoors.Remove(id);
        return result.ToArray();
    }
}
