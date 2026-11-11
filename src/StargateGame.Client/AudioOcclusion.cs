using System.Numerics;
using StargateGame.Building;
using StargateGame.Physics;
namespace StargateGame.Client;

// Immutable CPU acceleration structure. Built away from the render thread and
// replaced only when complete; never reads a GPU depth map or native body state.
sealed class AudioOcclusion
{
    sealed record Node(Vector3 Min, Vector3 Max, int Start, int Count, Node? Left = null, Node? Right = null);
    readonly BuildTriangle[] triangles;
    readonly Node root;
    public long Bytes => triangles.LongLength * 36 + triangles.LongLength / 6 * 64;
    public AudioOcclusion(IEnumerable<BuildTriangle> input)
    {
        triangles = input.ToArray();
        root = Build(0, triangles.Length);
    }
    Node Build(int start, int count)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            var t = triangles[i];
            min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
            max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
        }
        if (count <= 12)
            return new(min, max, start, count);
        var extent = max - min;
        int axis = extent.X > extent.Y ? (extent.X > extent.Z ? 0 : 2) : (extent.Y > extent.Z ? 1 : 2);
        float Centre(BuildTriangle t) => (t.A[axis] + t.B[axis] + t.C[axis]);
        Array.Sort(triangles, start, count, Comparer<BuildTriangle>.Create((a, b) => Centre(a).CompareTo(Centre(b))));
        int half = count / 2;
        return new(min, max, start, 0, Build(start, half), Build(start + half, count - half));
    }
    public bool Blocked(Vector3 eye, Vector3 target)
    {
        var delta = target - eye;
        float distance = delta.Length();
        if (distance < .3f)
            return false;
        return Hit(root, eye, delta / distance, .05f, distance - .2f);
    }
    bool Hit(Node node, Vector3 origin, Vector3 direction, float near, float far)
    {
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(direction[i]) < 1e-7f)
            {
                if (origin[i] < node.Min[i] || origin[i] > node.Max[i])
                    return false;
                continue;
            }
            float a = (node.Min[i] - origin[i]) / direction[i], b = (node.Max[i] - origin[i]) / direction[i];
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
            if (far < near)
                return false;
        }
        if (node.Left is not null)
            return Hit(node.Left, origin, direction, near, far) || Hit(node.Right!, origin, direction, near, far);
        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            var t = triangles[i];
            var e1 = t.B - t.A;
            var e2 = t.C - t.A;
            var p = Vector3.Cross(direction, e2);
            float det = Vector3.Dot(e1, p);
            if (Math.Abs(det) < 1e-7f)
                continue;
            float inv = 1 / det;
            var s = origin - t.A;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1)
                continue;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(direction, q) * inv;
            if (v < 0 || u + v > 1)
                continue;
            float d = Vector3.Dot(e2, q) * inv;
            if (d >= near && d <= far)
                return true;
        }
        return false;
    }
}
