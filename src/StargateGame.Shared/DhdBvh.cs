using System.Numerics;
namespace StargateGame.Shared;

public static partial class DhdInteraction
{
    sealed class Node
    {
        public Vector3 Min, Max;
        public Node? Left, Right;
        public Surface[] Faces = [];
    }
    static readonly Lazy<Node> tree = new(() => Build(Surfaces.ToArray()));
    static Node Build(Surface[] faces)
    {
        var n = new Node { Min = new(float.PositiveInfinity), Max = new(float.NegativeInfinity) };
        foreach (var f in faces)
        {
            n.Min = Vector3.Min(n.Min, Vector3.Min(f.A, Vector3.Min(f.B, f.C)));
            n.Max = Vector3.Max(n.Max, Vector3.Max(f.A, Vector3.Max(f.B, f.C)));
        }
        if (faces.Length <= 8)
        {
            n.Faces = faces;
            return n;
        }
        var size = n.Max - n.Min;
        int axis = size.X > size.Y ? (size.X > size.Z ? 0 : 2) : (size.Y > size.Z ? 1 : 2);
        Array.Sort(faces, (a, b) => ((a.A[axis] + a.B[axis] + a.C[axis]) / 3).CompareTo((b.A[axis] + b.B[axis] + b.C[axis]) / 3));
        int split = faces.Length / 2;
        n.Left = Build(faces[..split]);
        n.Right = Build(faces[split..]);
        return n;
    }
    static void PickNode(Node n, Vector3 o, Vector3 d, ref float closest, ref Surface? selected)
    {
        float near = 0, far = closest;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Math.Abs(d[axis]) < 1e-8f)
            {
                if (o[axis] < n.Min[axis] || o[axis] > n.Max[axis])
                    return;
                continue;
            }
            float a = (n.Min[axis] - o[axis]) / d[axis], b = (n.Max[axis] - o[axis]) / d[axis];
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
            if (near > far)
                return;
        }
        if (n.Left is not null)
        {
            PickNode(n.Left, o, d, ref closest, ref selected);
            PickNode(n.Right!, o, d, ref closest, ref selected);
            return;
        }
        foreach (var f in n.Faces)
            if (Ray(o, d, f.A, f.B, f.C) is { } distance && distance < closest)
            {
                closest = distance;
                selected = f;
            }
    }
}
