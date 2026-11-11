using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Building;

// Decorative stones have ledges and undercuts that should not act as stairs or
// trap the character between opposing contact normals. Keep a continuous wall
// surface over the corner's convex horizontal footprint, at its original height.
public static class CornerCollision
{
    static readonly Lazy<BuildTriangle[]> local = new(Build);

    static BuildTriangle[] Build()
    {
        var definition = BuildingPrefabs.Get(BuildingCorners.Model);
        var points = definition.Triangles.SelectMany(t => t)
            .Select(p => new Vector2(p[0], p[2])).Distinct()
            .OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
            (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
        static List<Vector2> Chain(IEnumerable<Vector2> source)
        {
            List<Vector2> result = [];
            foreach (var point in source)
            {
                while (result.Count >= 2 && Cross(result[^2],result[^1],point) <= 0)
                    result.RemoveAt(result.Count-1);
                result.Add(point);
            }
            return result;
        }
        var lower = Chain(points);
        var upper = Chain(points.Reverse());
        lower.RemoveAt(lower.Count-1);
        upper.RemoveAt(upper.Count-1);
        var hull = lower.Concat(upper).ToArray();
        float bottom = definition.Min[1], top = definition.Max[1];
        Vector3 At(int i, float y) => new(hull[i].X,y,hull[i].Y);
        List<BuildTriangle> triangles = [];
        for (int i=0;i<hull.Length;i++)
        {
            int next=(i+1)%hull.Length;
            var a=At(i,bottom); var b=At(next,bottom);
            var c=At(next,top); var d=At(i,top);
            // Hull is counterclockwise in X/Z: these side normals face out.
            triangles.Add(new(a,c,b)); triangles.Add(new(a,d,c));
        }
        for (int i=1;i+1<hull.Length;i++)
        {
            triangles.Add(new(At(0,top),At(i+1,top),At(i,top)));
            triangles.Add(new(At(0,bottom),At(i,bottom),At(i+1,bottom)));
        }
        return triangles.ToArray();
    }

    public static BuildTriangle[] Mesh(StructureRecord structure, BuildingPiece corner)
    {
        var transform = BuildingPrefabs.Transform(structure,corner);
        return local.Value.Select(t => new BuildTriangle(Vector3.Transform(t.A,transform),
            Vector3.Transform(t.B,transform),Vector3.Transform(t.C,transform))).ToArray();
    }
}
