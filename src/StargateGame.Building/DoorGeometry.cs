using System.Collections.Concurrent;
using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Building;

public static class DoorGeometry
{
    public sealed record Sweep(int Section, int Degree, Matrix4x4 Pose, BuildBounds Bounds);
    public sealed record Profile(Sweep[] Sweeps, BuildBounds Envelope);
    static readonly ConcurrentDictionary<string,Profile> profiles=[];
    static Vector3 V(float[] p)=>new(p[0],p[1],p[2]);
    public static Profile For(BuildingPiece door)=>profiles.GetOrAdd(door.Kind==PieceKind.Prefab?door.Prefab:"legacy-door", _=>
    {
        var definition=door.Kind==PieceKind.Prefab?BuildingPrefabs.Get(door.Prefab):null;
        var sections=definition is null ? new BuildBounds[]{new(new(0,.002f,-.045f),new(1.08f,2.278f,.045f))}
            : (definition.DoorSections ?? throw new InvalidDataException($"Missing authored door sections: {definition.Id}; regenerate door assets."))
                .Select(s=>new BuildBounds(V(s[0]),V(s[1]))).ToArray();
        var pivot=definition?.DoorPivot is { } pivotPoint?V(pivotPoint):new Vector3(0,0,-.045f);
        List<Sweep> sweeps=[];
        var lo=new Vector3(float.PositiveInfinity);var hi=new Vector3(float.NegativeInfinity);
        for(int i=0;i<90;i++)for(int j=0;j<sections.Length;j++)
        {
            var section=new BuildBounds(sections[j].Min-pivot,sections[j].Max-pivot);
            float half=MathF.PI/360;
            var a=BuildingQuery.TransformBounds(section,Matrix4x4.CreateRotationY(-half));
            var b=BuildingQuery.TransformBounds(section,Matrix4x4.CreateRotationY(half));
            // Chord sagitta bounds the circular arc between the two endpoint poses.
            float radius=Math.Max(new Vector2(section.Min.X,section.Min.Z).Length(),new Vector2(section.Max.X,section.Max.Z).Length());
            float error=radius*(1-MathF.Cos(half))+.00005f;
            var margin=new Vector3(error,0,error);
            var box=new BuildBounds(Vector3.Min(a.Min,b.Min)-margin,Vector3.Max(a.Max,b.Max)+margin);
            var pose=Matrix4x4.CreateRotationY((i+.5f)*MathF.PI/180)*Matrix4x4.CreateTranslation(pivot);
            sweeps.Add(new(j,i,pose,box));
            var bounds=BuildingQuery.TransformBounds(box,pose);lo=Vector3.Min(lo,bounds.Min);hi=Vector3.Max(hi,bounds.Max);
        }
        return new(sweeps.ToArray(),new(lo,hi));
    });
    public static Matrix4x4 Root(StructureRecord structure,BuildingPiece door)=>door.Kind==PieceKind.Prefab
        ?BuildingPrefabs.Transform(structure,door with{Open=false})
        :Matrix4x4.CreateTranslation(.96f,0,0)*Matrix4x4.CreateRotationY(-door.Rotation*MathF.PI/6)*Matrix4x4.CreateTranslation(door.Point.Position+Vector3.UnitY*door.Level*3)*BuildingGeometry.Transform(structure);

    // Door poses preserve the vertical axis; exact segment/AABB distance gives
    // capsule/convex-section contact without a player's enclosing box.
    public static bool CapsuleBox(Vector3 feet,float height,float radius,BuildBounds box)
    {
        float low=feet.Y+radius,high=feet.Y+height-radius;
        float y=high<box.Min.Y?box.Min.Y-high:low>box.Max.Y?low-box.Max.Y:0;
        float x=feet.X-Math.Clamp(feet.X,box.Min.X,box.Max.X),z=feet.Z-Math.Clamp(feet.Z,box.Min.Z,box.Max.Z);
        return x*x+y*y+z*z<radius*radius;
    }
}
