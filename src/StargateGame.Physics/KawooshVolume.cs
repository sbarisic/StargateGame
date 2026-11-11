using System.Numerics;
using System.Runtime.CompilerServices;
using StargateGame.Shared;
using StargateGame.Building;
namespace StargateGame.Physics;

// Closed liquid volume, separate from spray/mist. BVH rejects unrelated triangles.
public sealed class KawooshVolume
{
    const int Rings = 24, Sectors = 16;
    sealed class Node
    {
        public BuildBounds Bounds;
        public Node? Left, Right;
        public (int A, int B, int C)[] Indices = [];
    }
    GateFrame gateFrame;
    readonly Node root;
    readonly Vector3[] vertices = new Vector3[(Rings + 1) * Sectors + 1];
    Vector3 insidePoint;
    static readonly Vector2[] samples = Enumerable.Range(0, (Rings + 1) * Sectors).Select(i => new Vector2(MathF.Cos(i % Sectors * MathF.Tau / Sectors), MathF.Sin(i % Sectors * MathF.Tau / Sectors)) * (KawooshShape.Radius * (i / Sectors) / Rings)).ToArray();
    public BuildBounds Bounds => root.Bounds;
    public KawooshVolume(GateFrame gate, GateEffectFrame frame)
    {
        root = Build(0, Rings, 0, Sectors);
        Update(gate, frame);
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Update(GateFrame gate, GateEffectFrame frame)
    {
        gateFrame = gate;
        float scale = gate.Radius / KawooshShape.Radius;
        for (int i = 0; i < samples.Length; i++)
            vertices[i] = gate.World(KawooshShape.Position(samples[i], frame) * scale);
        vertices[^1] = gate.Centre;
        insidePoint = gate.World(new Vector3(0, 0, .02f * frame.Extension) * scale);
        Refit(root);
    }
    static Node Build(int r0, int r1, int a0, int a1)
    {
        var n = new Node();
        int dr = r1 - r0, da = a1 - a0;
        if (dr * da <= 4)
        {
            List<(int, int, int)> faces = [];
            for (int r = r0; r < r1; r++)
                for (int a = a0; a < a1; a++)
                {
                    int b = (a + 1) % Sectors, p = r * Sectors + a, q = (r + 1) * Sectors + a, t = (r + 1) * Sectors + b;
                    faces.Add((p, q, t));
                    if (r > 0)
                        faces.Add((p, t, r * Sectors + b));
                    if (r == Rings - 1)
                        faces.Add(((Rings + 1) * Sectors, Rings * Sectors + b, Rings * Sectors + a));
                }
            n.Indices = faces.ToArray();
            return n;
        }
        if (dr >= da)
        {
            int m = (r0 + r1) / 2;
            n.Left = Build(r0, m, a0, a1);
            n.Right = Build(m, r1, a0, a1);
        }
        else
        {
            int m = (a0 + a1) / 2;
            n.Left = Build(r0, r1, a0, m);
            n.Right = Build(r0, r1, m, a1);
        }
        return n;
    }
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    void Refit(Node n)
    {
        if (n.Left is not null)
        {
            Refit(n.Left);
            Refit(n.Right!);
            n.Bounds = new(Vector3.Min(n.Left.Bounds.Min, n.Right!.Bounds.Min), Vector3.Max(n.Left.Bounds.Max, n.Right.Bounds.Max));
            return;
        }
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var f in n.Indices)
        {
            min = Vector3.Min(min, Vector3.Min(vertices[f.A], Vector3.Min(vertices[f.B], vertices[f.C])));
            max = Vector3.Max(max, Vector3.Max(vertices[f.A], Vector3.Max(vertices[f.B], vertices[f.C])));
        }
        n.Bounds = new(min, max);
    }
    BuildTriangle Face((int A, int B, int C) f) => new(vertices[f.A], vertices[f.B], vertices[f.C]);
    static float? Ray(Vector3 o, Vector3 d, BuildTriangle t)
    {
        var e = t.B - t.A;
        var f = t.C - t.A;
        var p = Vector3.Cross(d, f);
        float det = Vector3.Dot(e, p);
        if (Math.Abs(det) < 1e-8f)
            return null;
        var s = o - t.A;
        float u = Vector3.Dot(s, p) / det;
        if (u < -.00001f || u > 1.00001f)
            return null;
        var q = Vector3.Cross(s, e);
        float v = Vector3.Dot(d, q) / det;
        if (v < -.00001f || u + v > 1.00001f)
            return null;
        float distance = Vector3.Dot(f, q) / det;
        return distance >= 0 ? distance : null;
    }
    static bool Edge(Vector3 a, Vector3 b, BuildTriangle t) => Ray(a, b - a, t) is { } d && d <= 1;
    static bool Cross(BuildTriangle a, BuildTriangle b) => Edge(a.A, a.B, b) || Edge(a.B, a.C, b) || Edge(a.C, a.A, b) || Edge(b.A, b.B, a) || Edge(b.B, b.C, a) || Edge(b.C, b.A, a);
    static bool Overlap(BuildBounds a, BuildBounds b) => a.Intersects(b, -.0001f);
    bool Touch(Node n, BuildTriangle t, BuildBounds bounds)
    {
        if (!Overlap(n.Bounds, bounds))
            return false;
        if (n.Left is not null)
            return Touch(n.Left, t, bounds) || Touch(n.Right!, t, bounds);
        foreach (var f in n.Indices)
            if (Cross(Face(f), t))
                return true;
        return false;
    }
    static bool RayBox(BuildBounds b, Vector3 o, Vector3 d)
    {
        float near = 0, far = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float a = (b.Min[i] - o[i]) / d[i], c = (b.Max[i] - o[i]) / d[i];
            near = Math.Max(near, Math.Min(a, c));
            far = Math.Min(far, Math.Max(a, c));
        }
        return near <= far;
    }
    static readonly Vector3 ray = Vector3.Normalize(new Vector3(.173f, .371f, 1));
    void Hits(Node n, Vector3 p, List<float> hits)
    {
        if (!RayBox(n.Bounds, p, ray))
            return;
        if (n.Left is not null)
        {
            Hits(n.Left, p, hits);
            Hits(n.Right!, p, hits);
        }
        else
            foreach (var t in n.Indices)
                if (Ray(p, ray, Face(t)) is { } d)
                    hits.Add(d);
    }
    static bool Odd(List<float> hits)
    {
        hits.Sort();
        int count = 0;
        float last = -1;
        foreach (float h in hits)
            if (h - last > .0001f)
            {
                count++;
                last = h;
            }
        return count % 2 == 1;
    }
    public bool Contains(Vector3 point)
    {
        if (gateFrame.Distance(point) <= 0)
            return false;
        List<float> hits = [];
        Hits(root, point, hits);
        return Odd(hits);
    }
    public bool Intersects(BuildTriangle[] mesh)
    {
        if (mesh.Length == 0 || !Overlap(Bounds, BuildingGeometry.Bounds(mesh)))
            return false;
        if (mesh.All(t => gateFrame.Distance(t.A) <= 0 && gateFrame.Distance(t.B) <= 0 && gateFrame.Distance(t.C) <= 0))
            return false;
        foreach (var t in mesh)
            if (Touch(root, t, BuildingGeometry.Bounds([t])))
                return true;
        if (mesh.Any(t => Contains(t.A)))
            return true;
        List<float> hits = [];
        foreach (var t in mesh)
            if (Ray(insidePoint, ray, t) is { } d)
                hits.Add(d);
        return Odd(hits);
    }
    public bool IntersectsProp(PropDefinition definition, BodyState previous, BodyState current)
    {
        float radius = definition.HalfExtent.Length();
        var sweptBounds = new BuildBounds(Vector3.Min(previous.Position, current.Position) - new Vector3(radius), Vector3.Max(previous.Position, current.Position) + new Vector3(radius));
        if (!Overlap(Bounds, sweptBounds))
            return false;
        var hull = PropGeometry.For(definition);
        float travel = Vector3.Distance(previous.Position, current.Position);
        int steps = Math.Clamp((int)MathF.Ceiling(Math.Max(travel / .08f, MathF.Acos(Math.Clamp(Math.Abs(Quaternion.Dot(previous.Rotation, current.Rotation)), 0, 1)) * 2 / .08f)), 1, 128);
        for (int i = 0; i <= steps; i++)
        {
            float f = i / (float)steps;
            var rotation = Quaternion.Slerp(previous.Rotation, current.Rotation, f);
            var position = Vector3.Lerp(previous.Position, current.Position, f);
            var vertices = hull.Vertices.Select(v => position + Vector3.Transform(v, rotation)).ToArray();
            if (Intersects(hull.Faces.Select(t => new BuildTriangle(vertices[t.A], vertices[t.B], vertices[t.C])).ToArray()))
                return true;
        }
        return false;
    }
}
