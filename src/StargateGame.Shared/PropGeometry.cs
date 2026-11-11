using System.Numerics;
namespace StargateGame.Shared;

// One convex description for native dynamics, picking and membrane slicing.
public static class PropGeometry
{
    public sealed record Hull(Vector3[] Vertices, (int A, int B)[] Edges, (int A, int B, int C)[] Faces);
    public static Hull Build(Vector3 half, bool barrel)
    {
        List<Vector3> vertices = [];
        List<(int, int)> edges = [];
        List<(int, int, int)> faces = [];
        if (!barrel)
        {
            for (int i = 0; i < 8; i++)
                vertices.Add(new((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z));
            for (int i = 0; i < 8; i++)
                for (int a = 1; a <= 4; a *= 2)
                    if (i < (i ^ a))
                        edges.Add((i, i ^ a));
            foreach (var q in new[] { new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 } })
            {
                faces.Add((q[0], q[1], q[2]));
                faces.Add((q[0], q[2], q[3]));
            }
        }
        else
        {
            const int sides = 32;
            for (int r = 0; r < 4; r++)
                for (int i = 0; i < sides; i++)
                {
                    float a = i * MathF.Tau / sides, scale = r is 0 or 3 ? .94f : 1;
                    float y = r switch
                    {
                        0 => -half.Y,
                        1 => -half.Y + .035f,
                        2 => half.Y - .035f,
                        _ => half.Y
                    };
                    vertices.Add(new(MathF.Cos(a) * half.X * scale, y, MathF.Sin(a) * half.Z * scale));
                    edges.Add((r * sides + i, r * sides + (i + 1) % sides));
                    if (r < 3)
                    {
                        int p = r * sides + i, q = r * sides + (i + 1) % sides;
                        edges.Add((p, p + sides));
                        faces.Add((p, q, p + sides));
                        faces.Add((q, q + sides, p + sides));
                    }
                }
            for (int i = 1; i < sides - 1; i++)
            {
                faces.Add((0, i + 1, i));
                faces.Add((96, 96 + i, 96 + i + 1));
            }
        }
        return new(vertices.ToArray(), edges.ToArray(), faces.ToArray());
    }
    static readonly Hull BarrelHull = Build(new(.363f, .592f, .363f), true);
    public static Hull For(PropDefinition prop) => prop.IsBarrel && prop.Valid ? BarrelHull : Build(prop.HalfExtent, prop.IsBarrel);
    public static bool RayHit(Vector3 eye, Vector3 aim, BodyState body, PropDefinition prop, float limit, GateFrame? clip = null)
    {
        var inverse = Quaternion.Inverse(body.Rotation);
        var origin = Vector3.Transform(eye - body.Position, inverse);
        var direction = Vector3.Transform(aim, inverse);
        float near = 0, far = limit;
        var hull = For(prop);
        bool Plane(Vector3 n, float distance)
        {
            float a = distance - Vector3.Dot(n, origin), b = Vector3.Dot(n, direction);
            if (Math.Abs(b) < 1e-7f)
                return a >= 0;
            float t = a / b;
            if (b > 0)
                far = Math.Min(far, t);
            else
                near = Math.Max(near, t);
            return near <= far;
        }
        foreach (var (a, b, c) in hull.Faces)
        {
            var n = Vector3.Normalize(Vector3.Cross(hull.Vertices[b] - hull.Vertices[a], hull.Vertices[c] - hull.Vertices[a]));
            if (Vector3.Dot(n, hull.Vertices[a]) < 0)
                n = -n;
            if (!Plane(n, Vector3.Dot(n, hull.Vertices[a])))
                return false;
        }
        if (clip is { } gate)
        {
            var n = Vector3.Transform(-gate.Normal, inverse);
            if (!Plane(n, gate.Distance(body.Position)))
                return false;
        }
        return far >= 0 && near < limit;
    }
}
