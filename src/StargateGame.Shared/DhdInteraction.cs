using System.Numerics;

namespace StargateGame.Shared;

public readonly record struct DhdHit(int Key, float Distance, Vector3 Point);
public static partial class DhdInteraction
{
    static readonly string[] Names = ["Orb", "Local origin", "Crater", "Virgo", "Bootes", "Centaurus", "Libra", "Serpens Caput", "Norma", "Scorpio", "Cra", "Scutum", "Sagittarius", "Aquila", "Mic", "Capricorn", "Pisces Austrinus", "Equuleus", "Aquarius", "Pegasus", "Sculptor", "Pisces", "Andromeda", "Triangulum", "Aries", "Perseus", "Cetus", "Taurus", "Auriga", "Eridanus", "Orion", "Canis Minor", "Monoceros", "Gemini", "Hydra", "Lynx", "Cancer", "Sextans", "Leo Minor", "Leo"];
    public static string Name(int key) => key is >= 0 and < 40 ? Names[key] : "Unavailable";
    public readonly record struct Surface(int Key, Vector3 A, Vector3 B, Vector3 C);
    static readonly Lazy<Surface[]> geometry = new(Load);
    public static IReadOnlyList<Surface> Surfaces => geometry.Value;
    static Surface[] Load()
    {
        using var stream = typeof(DhdInteraction).Assembly.GetManifestResourceStream("DhdInteraction")!;
        using var r = new BinaryReader(stream);
        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(5)) != "SGDI1")
            throw new InvalidDataException("Invalid DHD picking mesh.");
        var result = new Surface[r.ReadInt32()];
        Vector3 V() => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < result.Length; i++)
            result[i] = new(r.ReadInt32(), V(), V(), V());
        return result;
    }
    public static bool Available(int key) => key is >= 1 and <= 39 && key != 13;
    public static bool WithinReach(StaticEntity entity, Vector3 eye, float reach = 3)
    {
        if (entity.Scale <= 0 || !float.IsFinite(entity.Scale) || reach < 0 || !float.IsFinite(reach) || !float.IsFinite(eye.LengthSquared()))
            return false;
        var localEye = Vector3.Transform(eye - entity.Position, Quaternion.Conjugate(entity.Rotation)) / entity.Scale;
        float distanceSquared = MathF.Pow(reach / entity.Scale, 2);
        return Surfaces.Any(surface => surface.Key >= 0 &&
            Vector3.DistanceSquared(localEye, ClosestPoint(surface, localEye)) <= distanceSquared);
    }

    static Vector3 ClosestPoint(Surface triangle, Vector3 point)
    {
        var ab = triangle.B - triangle.A;
        var ac = triangle.C - triangle.A;
        var ap = point - triangle.A;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0)
            return triangle.A;
        var bp = point - triangle.B;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return triangle.B;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
            return triangle.A + ab * (d1 / (d1 - d3));
        var cp = point - triangle.C;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return triangle.C;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
            return triangle.A + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 >= d3 && d5 >= d6)
            return triangle.B + (triangle.C - triangle.B) * ((d4 - d3) / (d4 - d3 + d5 - d6));
        float inverse = 1 / (va + vb + vc);
        return triangle.A + ab * (vb * inverse) + ac * (vc * inverse);
    }
    public static bool BoxHit(Vector3 eye, Vector3 aim, Vector3 center, Quaternion rotation, Vector3 half, float limit)
    {
        var inverse = Quaternion.Conjugate(rotation);
        var o = Vector3.Transform(eye - center, inverse);
        var d = Vector3.Transform(aim, inverse);
        float near = 0, far = limit - .015f;
        for (int i = 0; i < 3; i++)
        {
            if (Math.Abs(d[i]) < 1e-6f)
            {
                if (Math.Abs(o[i]) > half[i])
                    return false;
                continue;
            }
            float a = (-half[i] - o[i]) / d[i], b = (half[i] - o[i]) / d[i];
            near = Math.Max(near, Math.Min(a, b));
            far = Math.Min(far, Math.Max(a, b));
            if (near > far)
                return false;
        }
        return true;
    }
    public static DhdHit? Pick(StaticEntity entity, Vector3 eye, Vector3 direction, float reach = 3)
        => PickConsole(entity, eye, direction, reach) is { Key: >= 0 } hit ? hit : null;

    /// <summary>Nearest console surface, including the rim, body and gaps between keys.</summary>
    public static DhdHit? PickConsole(StaticEntity entity, Vector3 eye, Vector3 direction, float reach = 3)
    {
        if (!float.IsFinite(direction.LengthSquared()) || Math.Abs(direction.LengthSquared() - 1) > .01f
            || !float.IsFinite(eye.LengthSquared()) || !float.IsFinite(entity.Scale) || entity.Scale <= 0
            || !float.IsFinite(reach) || reach <= 0)
            return null;
        var originalDirection = direction;
        var inverse = Quaternion.Conjugate(entity.Rotation);
        direction = Vector3.Transform(direction, inverse);
        var origin = Vector3.Transform(eye - entity.Position, inverse) / entity.Scale;
        float closest = reach / entity.Scale;
        Surface? selected = null;
        PickNode(tree.Value, origin, direction, ref closest, ref selected);
        return selected is { } hit ? new(hit.Key, closest * entity.Scale, eye + originalDirection * closest * entity.Scale) : null;
    }
    public static float? Ray(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c)
    {
        var e = b - a;
        var f = c - a;
        var p = Vector3.Cross(d, f);
        float det = Vector3.Dot(e, p);
        if (Math.Abs(det) < 1e-8f)
            return null;
        var t = o - a;
        float u = Vector3.Dot(t, p) / det;
        if (u < 0 || u > 1)
            return null;
        var q = Vector3.Cross(t, e);
        float v = Vector3.Dot(d, q) / det;
        if (v < 0 || u + v > 1)
            return null;
        float distance = Vector3.Dot(f, q) / det;
        return distance >= 0 ? distance : null;
    }
}
