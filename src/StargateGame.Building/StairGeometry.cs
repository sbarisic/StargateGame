using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Building;

// Authored flight profiles, independent of render bounds (which include rails).
public static class StairGeometry
{
    readonly record struct Flight(float Left, float Right, float Start, float End, float Bottom, float Top)
    {
        public float Height(float z) => Bottom + (Top - Bottom) * Math.Clamp((z - Start) / (End - Start), 0, 1);
        public bool Contains(Vector3 p) => p.X >= Left - .002f && p.X <= Right + .002f &&
            p.Z >= Math.Min(Start, End) - .002f && p.Z <= Math.Max(Start, End) + .002f;
    }

    public static bool IsStair(BuildingPiece p) => p.Kind == PieceKind.Stairs ||
        p.Kind == PieceKind.Prefab && p.Prefab is "kit-stair-interior-simple" or "kit-stairs-exterior-straight" or "fort-stairs";

    static Flight[] Flights(BuildingPiece p) => p.Kind == PieceKind.Stairs
        ? [.. (p.FoundationBottom > 0 ? new Flight[] { new(.1f,1.3f,-.6f,0,-.15f,.15f) } : []),
            new(.1f,1.3f,0,2.25f,.15f,1.5f), new(.1f,1.3f,2.25f,2.5f,1.5f,1.5f),
            new(1.7f,2.9f,2.75f,2.5f,1.5f,1.65f), new(1.7f,2.9f,2.5f,.25f,1.65f,3), new(1.7f,2.9f,.25f,0,3,3)]
        : p.Prefab switch
        {
            "kit-stair-interior-simple" => [new(-.8381f,.8381f,-2.277f,1.692f,.210f,3.02459f), new(-.8381f,.8381f,1.692f,2.30977f,3.02459f,3.02459f)],
            "kit-stairs-exterior-straight" => [new(-.9f,.9f,-1.017f,.602f,.003f,.818f), new(-.9f,.9f,.602f,1.039f,.818f,.818f)],
            "fort-stairs" => [new(-.505f,.505f,-1.365f,1.192f,.284f,2.828f), new(-.505f,.505f,1.192f,1.442f,2.828f,2.828f)],
            _ => []
        };

    public static BuildTriangle[] PlayerMesh(StructureRecord structure, BuildingPiece piece)
    {
        var flights = Flights(piece);
        if (flights.Length == 0) return BuildingGeometry.Mesh(structure,piece);
        var identity = new StructureRecord(Guid.Empty,Vector3.Zero,0);
        var localPiece = piece with { Offset = Vector3.Zero, Point = default, Rotation = 0, Level = 0 };
        var original = BuildingGeometry.Mesh(identity,localPiece);
        List<BuildTriangle> result = [];
        foreach (var t in original)
        {
            var n = Vector3.Cross(t.B-t.A,t.C-t.A);
            if (n.LengthSquared()<1e-12f) continue;
            n=Vector3.Normalize(n);
            if (n.Y < -.1f || Math.Abs(n.X) > .8f) { result.Add(t); continue; }
            // Clip only the portion within each walking strip. A landing can
            // span both flights, so classifying its whole triangle by its center
            // would remove valid support beside the ramp.
            List<List<Vector3>> fragments = [[t.A,t.B,t.C]];
            foreach (var flight in flights)
            {
                List<List<Vector3>> outside = [];
                foreach (var fragment in fragments)
                {
                    var inside=fragment;
                    Func<Vector3,float>[] planes = [p=>flight.Left-p.X,p=>p.X-flight.Right,
                        p=>Math.Min(flight.Start,flight.End)-p.Z,p=>p.Z-Math.Max(flight.Start,flight.End),
                        p=>p.Y-flight.Bottom-(flight.Top-flight.Bottom)*(p.Z-flight.Start)/(flight.End-flight.Start)-.04f];
                    foreach (var distance in planes)
                    {
                        if (inside.Count<3) break;
                        List<Vector3> keep=[],discard=[];
                        for (int i=0;i<inside.Count;i++)
                        {
                            var a=inside[i]; var b=inside[(i+1)%inside.Count];
                            float da=distance(a),db=distance(b);
                            if (da<=1e-6f) keep.Add(a); else discard.Add(a);
                            if ((da>1e-6f)!=(db>1e-6f))
                            {
                                var crossing=Vector3.Lerp(a,b,da/(da-db));
                                keep.Add(crossing); discard.Add(crossing);
                            }
                        }
                        if (discard.Count>=3) outside.Add(discard);
                        inside=keep;
                    }
                }
                fragments=outside;
            }
            foreach (var polygon in fragments)
                for (int i=1;i+1<polygon.Count;i++)
                    if (Vector3.Cross(polygon[i]-polygon[0],polygon[i+1]-polygon[0]).LengthSquared()>1e-12f)
                        result.Add(new(polygon[0],polygon[i],polygon[i+1]));
        }
        foreach (var f in flights)
        {
            Vector3 a=new(f.Left,f.Bottom,f.Start), b=new(f.Right,f.Bottom,f.Start);
            Vector3 c=new(f.Right,f.Top,f.End), d=new(f.Left,f.Top,f.End);
            if (f.End > f.Start) { result.Add(new(a,d,c)); result.Add(new(a,c,b)); }
            else { result.Add(new(a,b,c)); result.Add(new(a,c,d)); }
        }
        var transform = piece.Kind == PieceKind.Prefab ? BuildingPrefabs.Transform(structure,piece) :
            Matrix4x4.CreateRotationY(-piece.Rotation*MathF.PI/6) *
            Matrix4x4.CreateTranslation(piece.Point.Position+Vector3.UnitY*piece.Level*3) * BuildingGeometry.Transform(structure);
        return result.Select(t=>new BuildTriangle(Vector3.Transform(t.A,transform),Vector3.Transform(t.B,transform),Vector3.Transform(t.C,transform))).ToArray();
    }
}
