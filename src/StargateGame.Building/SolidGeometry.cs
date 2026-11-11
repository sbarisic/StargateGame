using System.Numerics;
namespace StargateGame.Building;

public static class SolidGeometry
{
    public static IEnumerable<BuildTriangle> ExposedBox((BuildBounds Box,Matrix4x4 Transform)[] boxes,int index)
    {
        var current=boxes[index];
        BuildBounds WorldBounds((BuildBounds Box, Matrix4x4 Transform) item) => BuildingGeometry.Bounds(Union([item.Box]).Select(t => new BuildTriangle(Vector3.Transform(t.A,item.Transform),Vector3.Transform(t.B,item.Transform),Vector3.Transform(t.C,item.Transform))));
        var bounds=WorldBounds(current);
        var neighbors=Enumerable.Range(0,boxes.Length).Where(j=>j!=index && bounds.Intersects(WorldBounds(boxes[j]),-.0001f)).ToArray();
        foreach(var triangle in Union([current.Box]))
        {
            var polygon=new[]{triangle.A,triangle.B,triangle.C}.Select(p=>Vector3.Transform(p,current.Transform)).ToList();
            List<List<Vector3>> fragments=[polygon];
            foreach(int j in neighbors)
            {
                if(j==index)continue;
                Matrix4x4.Invert(boxes[j].Transform,out var inv);
                var b=boxes[j].Box;
                List<List<Vector3>> next=[];
                foreach(var fragment in fragments)
                {
                    var inside=fragment.Select(p=>Vector3.Transform(p,inv)).ToList();
                    // Identical exterior planes are emitted only by the first box.
                    var n=Vector3.Normalize(Vector3.Cross(inside[1]-inside[0],inside[2]-inside[0]));
                    bool samePlane=false;
                    for(int axis=0;axis<3;axis++)
                    {
                        float Component(Vector3 p)=>axis==0?p.X:axis==1?p.Y:p.Z;
                        if(inside.All(p=>Math.Abs(Component(p)-Component(b.Max))<.00001f)&&Component(n)>.99f || inside.All(p=>Math.Abs(Component(p)-Component(b.Min))<.00001f)&&Component(n)<-.99f)samePlane=true;
                    }
                    if(samePlane && j>index){next.Add(fragment);continue;}
                    for(int plane=0;plane<6 && inside.Count>=3;plane++)
                    {
                        int axis=plane/2;bool upper=plane%2==1;
                        float C(Vector3 p)=>axis==0?p.X:axis==1?p.Y:p.Z;
                        float D(Vector3 p)=>upper?C(p)-C(b.Max):C(b.Min)-C(p);
                        List<Vector3> retained=[],outside=[];
                        for(int k=0;k<inside.Count;k++)
                        {
                            var a=inside[k];var c=inside[(k+1)%inside.Count];float da=D(a),dc=D(c);
                            if(da<=.000001f)retained.Add(a);else outside.Add(a);
                            if((da> .000001f)!=(dc> .000001f)) {var cross=Vector3.Lerp(a,c,da/(da-dc));retained.Add(cross);outside.Add(cross);}
                        }
                        if(outside.Count>=3)next.Add(outside.Select(p=>Vector3.Transform(p,boxes[j].Transform)).ToList());
                        inside=retained;
                    }
                }
                fragments=next;
            }
            foreach(var f in fragments)for(int k=1;k+1<f.Count;k++)
                if(Vector3.Cross(f[k]-f[0],f[k+1]-f[0]).LengthSquared()>1e-12f)yield return new(f[0],f[k],f[k+1]);
        }
    }
    // Boundary of an axis-aligned box union: never emit buried/shared faces.
    public static BuildTriangle[] Union(IEnumerable<BuildBounds> source)
    {
        Vector3 Round(Vector3 p) => new(MathF.Round(p.X,5),MathF.Round(p.Y,5),MathF.Round(p.Z,5));
        var boxes = source.Select(b=>new BuildBounds(Round(b.Min),Round(b.Max))).ToArray();
        if (boxes.Length == 0) return [];
        var xs = boxes.SelectMany(b => new[] { b.Min.X, b.Max.X }).Distinct().Order().ToArray();
        var ys = boxes.SelectMany(b => new[] { b.Min.Y, b.Max.Y }).Distinct().Order().ToArray();
        var zs = boxes.SelectMany(b => new[] { b.Min.Z, b.Max.Z }).Distinct().Order().ToArray();
        bool[,,] filled = new bool[xs.Length-1, ys.Length-1, zs.Length-1];
        for (int x=0;x<xs.Length-1;x++) for(int y=0;y<ys.Length-1;y++) for(int z=0;z<zs.Length-1;z++)
        {
            var p = new Vector3((xs[x]+xs[x+1])/2,(ys[y]+ys[y+1])/2,(zs[z]+zs[z+1])/2);
            filled[x,y,z] = boxes.Any(b => p.X>b.Min.X && p.X<b.Max.X && p.Y>b.Min.Y && p.Y<b.Max.Y && p.Z>b.Min.Z && p.Z<b.Max.Z);
        }
        List<BuildTriangle> result=[];
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { result.Add(new(a,b,c)); result.Add(new(a,c,d)); }
        bool Has(int x,int y,int z) => x>=0 && y>=0 && z>=0 && x<xs.Length-1 && y<ys.Length-1 && z<zs.Length-1 && filled[x,y,z];
        for(int x=0;x<xs.Length-1;x++) for(int y=0;y<ys.Length-1;y++) for(int z=0;z<zs.Length-1;z++)
        {
            if(!filled[x,y,z])continue;
            Vector3 a=new(xs[x],ys[y],zs[z]), b=new(xs[x+1],ys[y],zs[z]), c=new(xs[x+1],ys[y],zs[z+1]), d=new(xs[x],ys[y],zs[z+1]), up=new(0,ys[y+1]-ys[y],0);
            if(!Has(x,y-1,z))Quad(a,b,c,d);
            if(!Has(x,y+1,z))Quad(a+up,d+up,c+up,b+up);
            if(!Has(x,y,z-1))Quad(a,a+up,b+up,b);
            if(!Has(x+1,y,z))Quad(b,b+up,c+up,c);
            if(!Has(x,y,z+1))Quad(c,c+up,d+up,d);
            if(!Has(x-1,y,z))Quad(d,d+up,a+up,a);
        }
        return result.ToArray();
    }

    public static bool TriangleBox(BuildTriangle triangle, BuildBounds box)
    {
        var center=(box.Min+box.Max)*.5f; var half=(box.Max-box.Min)*.5f;
        var a=triangle.A-center; var b=triangle.B-center; var c=triangle.C-center;
        var edges=new[]{b-a,c-b,a-c};
        bool Separated(Vector3 axis)
        {
            if(axis.LengthSquared()<1e-12f)return false;
            float x=Vector3.Dot(a,axis),y=Vector3.Dot(b,axis),z=Vector3.Dot(c,axis);
            float r=Vector3.Dot(half,Vector3.Abs(axis));
            return Math.Min(x,Math.Min(y,z))>r || Math.Max(x,Math.Max(y,z))< -r;
        }
        if(Separated(Vector3.UnitX)||Separated(Vector3.UnitY)||Separated(Vector3.UnitZ)||Separated(Vector3.Cross(edges[0],edges[1])))return false;
        return !edges.Any(e=>Separated(Vector3.Cross(e,Vector3.UnitX))||Separated(Vector3.Cross(e,Vector3.UnitY))||Separated(Vector3.Cross(e,Vector3.UnitZ)));
    }

    // SAT includes containment, which a surface-only triangle test misses.
    public static bool ConvexBox(Vector3[] vertices, (int A, int B, int C)[] faces, (int A, int B)[] edges, BuildBounds box)
    {
        var center = (box.Min + box.Max) * .5f;
        var half = (box.Max - box.Min) * .5f;
        bool Separated(Vector3 axis)
        {
            if (axis.LengthSquared() < 1e-12f) return false;
            float min = float.PositiveInfinity, max = float.NegativeInfinity;
            foreach (var point in vertices)
            {
                float value = Vector3.Dot(point - center, axis);
                min = Math.Min(min, value); max = Math.Max(max, value);
            }
            float extent = Vector3.Dot(half, Vector3.Abs(axis));
            return min > extent || max < -extent;
        }
        if (Separated(Vector3.UnitX) || Separated(Vector3.UnitY) || Separated(Vector3.UnitZ)) return false;
        foreach (var face in faces)
            if (Separated(Vector3.Cross(vertices[face.B]-vertices[face.A],vertices[face.C]-vertices[face.A]))) return false;
        foreach (var edge in edges)
        {
            var vector = vertices[edge.B]-vertices[edge.A];
            if (Separated(Vector3.Cross(vector,Vector3.UnitX)) || Separated(Vector3.Cross(vector,Vector3.UnitY)) || Separated(Vector3.Cross(vector,Vector3.UnitZ))) return false;
        }
        return true;
    }
}
