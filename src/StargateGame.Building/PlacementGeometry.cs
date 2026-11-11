using System.Numerics;
using System.Runtime.CompilerServices;
using StargateGame.Shared;

namespace StargateGame.Building;

// Triangle surfaces plus containment, rather than a convex footprint spanning
// every opening and disconnected component in an assembly.
public static class PlacementGeometry
{
    sealed record Solid(BuildTriangle[] Faces, BuildBounds Bounds, Vector3[] Points);
    static readonly ConditionalWeakTable<BuildTriangle[], Solid[]> solids = new();
    static Solid[] Components(BuildTriangle[] mesh)
    {
        var parents=Enumerable.Range(0,mesh.Length).ToArray();
        int Root(int i) { while (parents[i]!=i) { parents[i]=parents[parents[i]]; i=parents[i]; } return i; }
        var vertices=new Dictionary<(int,int,int),int>();
        for (int i=0;i<mesh.Length;i++)
            foreach (var p in new[]{mesh[i].A,mesh[i].B,mesh[i].C})
            {
                var key=((int)MathF.Round(p.X*10000),(int)MathF.Round(p.Y*10000),(int)MathF.Round(p.Z*10000));
                if (vertices.TryGetValue(key,out int other)) parents[Root(i)]=Root(other);
                else vertices[key]=i;
            }
        return Enumerable.Range(0,mesh.Length).GroupBy(Root).Select(g=>
        {
            var faces=g.Select(i=>mesh[i]).ToArray();
            var points=faces.SelectMany(t=>new[]{t.A,t.B,t.C}).Distinct().ToArray();
            return new Solid(faces,BuildingGeometry.Bounds(faces),points);
        }).ToArray();
    }

    static float? Hit(Vector3 origin,Vector3 direction,BuildTriangle t)
    {
        var e=t.B-t.A; var f=t.C-t.A; var h=Vector3.Cross(direction,f);
        float det=Vector3.Dot(e,h);
        if (Math.Abs(det)<1e-9f) return null;
        var s=origin-t.A; float u=Vector3.Dot(s,h)/det;
        var q=Vector3.Cross(s,e); float v=Vector3.Dot(direction,q)/det;
        if (u<0 || v<0 || u+v>1) return null;
        return Vector3.Dot(f,q)/det;
    }

    static bool Inside(Vector3 p,Solid solid,float epsilon)
    {
        if (p.X<=solid.Bounds.Min.X+epsilon || p.X>=solid.Bounds.Max.X-epsilon ||
            p.Y<=solid.Bounds.Min.Y+epsilon || p.Y>=solid.Bounds.Max.Y-epsilon ||
            p.Z<=solid.Bounds.Min.Z+epsilon || p.Z>=solid.Bounds.Max.Z-epsilon) return false;
        // Two non-axis-aligned rays avoid treating a shared edge as a volume.
        foreach (var ray in new[]{Vector3.Normalize(new Vector3(1,.317f,.193f)),Vector3.Normalize(new Vector3(-.231f,.419f,1))})
        {
            var hits=solid.Faces.Select(t=>Hit(p,ray,t)).Where(d=>d is >= 0).Select(d=>d!.Value).Order().ToArray();
            int count=0; float last=float.NegativeInfinity;
            foreach (float d in hits)
            {
                if (d<epsilon) return false;
                if (d-last>1e-5f) { count++; last=d; }
            }
            if (count%2==0) return false;
        }
        return true;
    }

    public static bool Overlaps(BuildTriangle[] a,BuildTriangle[] b,float epsilon=.002f)
    {
        if (!BuildingGeometry.Bounds(a).Intersects(BuildingGeometry.Bounds(b),epsilon)) return false;
        foreach (var x in solids.GetValue(a,Components))
            foreach (var y in solids.GetValue(b,Components))
            {
                if (!x.Bounds.Intersects(y.Bounds,epsilon)) continue;
                // Surface crossings require penetration on both sides of the
                // other face, so coplanar touching surfaces remain legal.
                bool Cross(Solid first,Solid second)
                {
                    foreach (var t in first.Faces)
                        foreach (var face in second.Faces)
                        {
                            if (!BuildingGeometry.Bounds([t]).Intersects(BuildingGeometry.Bounds([face]),-1e-6f)) continue;
                            var n=Vector3.Cross(face.B-face.A,face.C-face.A);
                            if (n.LengthSquared()<1e-12f) continue;
                            n=Vector3.Normalize(n);
                            var points=new[]{t.A,t.B,t.C};
                            var distances=points.Select(p=>Vector3.Dot(p-face.A,n)).ToArray();
                            if (distances.Min()>=-epsilon || distances.Max()<=epsilon) continue;
                            for (int i=0;i<3;i++)
                            {
                                var delta=points[(i+1)%3]-points[i];
                                if (Hit(points[i],delta,face) is { } h && h>0 && h<1) return true;
                            }
                        }
                    return false;
                }
                if (Cross(x,y)||Cross(y,x)) return true;
                if (x.Points.Any(p=>Inside(p,y,epsilon)) || y.Points.Any(p=>Inside(p,x,epsilon))) return true;
                // Identical solids can have every vertex on the other boundary.
                if (x.Faces.Any(t=>Inside((t.A+t.B+t.C)/3,y,epsilon)) ||
                    y.Faces.Any(t=>Inside((t.A+t.B+t.C)/3,x,epsilon))) return true;
                if (Inside((x.Bounds.Min+x.Bounds.Max)/2,x,epsilon) && Inside((x.Bounds.Min+x.Bounds.Max)/2,y,epsilon)) return true;
            }
        return false;
    }

    public static BuildTriangle[] Occupied(StructureRecord structure,BuildingPiece piece)
    {
        if (piece.Kind!=PieceKind.Prefab) return BuildingGeometry.Mesh(structure,piece);
        var definition=BuildingPrefabs.Get(piece.Prefab);
        if (definition.Role is not ("floor" or "foundation")) return BuildingPrefabs.Mesh(structure,piece);
        // Some floor assets contain only top/bottom faces. Their authored
        // thickness still reserves a closed placement volume.
        var transform=BuildingPrefabs.Transform(structure,piece);
        var box=new BuildBounds(new(definition.Min[0],definition.Min[1],definition.Min[2]),new(definition.Max[0],definition.Max[1],definition.Max[2]));
        return SolidGeometry.Union([box]).Select(t=>new BuildTriangle(Vector3.Transform(t.A,transform),Vector3.Transform(t.B,transform),Vector3.Transform(t.C,transform))).ToArray();
    }

    public static BuildTriangle[] Occupied(WorldChanges state,BuildingPiece piece) =>
        BuildingBaseCourses.ReplacesOwner(state,piece.Id) ? BuildingGeometry.Mesh(state,piece) :
        Occupied(state.Structures.Single(s=>s.Id==piece.StructureId),piece);

    public static bool FoundationContact(BuildingPiece piece,BuildingPiece foundation)
    {
        if (piece.StructureId!=foundation.StructureId || piece.Kind!=PieceKind.Prefab || foundation.Kind!=PieceKind.Prefab) return false;
        var p=BuildingPrefabs.Get(piece.Prefab); var f=BuildingPrefabs.Get(foundation.Prefab);
        if (f.Role!="foundation" || p.Role is not ("wall" or "window" or "doorway" or "floor" or "corner")) return false;
        float top=foundation.Offset.Y+f.Max[1];
        return Math.Abs(piece.Offset.Y-top)<=.025f && piece.Offset.Y+p.Min[1]>=top-.025f;
    }

    // Thin floor finishes may meet the wall at its foot, just as they may
    // meet its top. Restrict this to the shared datum and a perimeter wall;
    // a wall through the middle of a floor must still reject placement.
    public static bool WallBaseContact(BuildingPiece floor, BuildingPiece wall)
    {
        if (floor.StructureId != wall.StructureId || floor.Kind != PieceKind.Prefab || wall.Kind != PieceKind.Prefab) return false;
        var f = BuildingPrefabs.Get(floor.Prefab);
        var w = BuildingPrefabs.Get(wall.Prefab);
        bool corner = wall.Prefab == BuildingCorners.Model || w.Role == "corner";
        if (f.Role != "floor" || !corner && w.Role is not ("wall" or "window" or "doorway")) return false;
        float datum = wall.Offset.Y;
        if (Math.Abs(floor.Offset.Y - datum) > .025f ||
            floor.Offset.Y + f.Min[1] < datum - .035f ||
            floor.Offset.Y + f.Max[1] > datum + .035f) return false;
        var inverseRotation = Matrix4x4.CreateRotationY(floor.Rotation * MathF.PI / 6);
        var center = Vector3.Transform(wall.Offset - floor.Offset, inverseRotation);
        var along = Vector3.TransformNormal(Vector3.UnitX,
            Matrix4x4.CreateRotationY((floor.Rotation - wall.Rotation) * MathF.PI / 6));
        bool edgeX = Math.Min(Math.Abs(center.X - f.Min[0]), Math.Abs(center.X - f.Max[0])) <= .025f;
        bool edgeZ = Math.Min(Math.Abs(center.Z - f.Min[2]), Math.Abs(center.Z - f.Max[2])) <= .025f;
        return corner ? edgeX && edgeZ : edgeX && Math.Abs(along.X) < .01f || edgeZ && Math.Abs(along.Z) < .01f;
    }

    public static bool WallTopContact(BuildingPiece floor,BuildingPiece wall)
    {
        if (floor.StructureId!=wall.StructureId || floor.Kind!=PieceKind.Prefab || wall.Kind!=PieceKind.Prefab) return false;
        var f=BuildingPrefabs.Get(floor.Prefab); var w=BuildingPrefabs.Get(wall.Prefab);
        if (f.Role is not ("floor" or "landing") || w.Role is not ("wall" or "window" or "doorway" or "corner") && wall.Prefab != BuildingCorners.Model) return false;
        float top=wall.Offset.Y+w.Height;
        return Math.Abs(floor.Offset.Y-top)<=.025f && floor.Offset.Y+f.Min[1]>=top-.035f;
    }
}
