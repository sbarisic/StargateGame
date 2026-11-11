using System.Numerics;

namespace StargateGame.Client;

// Independent of graphics resources: mip zero always means the finest resident level.
internal readonly record struct CanopyLod(float Mip, int MarchLimit, int Band)
{
    public static CanopyLod Full => new(0, 1536, 0);

    public static float ProjectedHeight(Vector3 min, Vector3 max, Vector3 eye, Matrix4x4 viewProjection, int viewportHeight, float near = .05f)
    {
        if (eye.X >= min.X && eye.X <= max.X && eye.Y >= min.Y && eye.Y <= max.Y && eye.Z >= min.Z && eye.Z <= max.Z)
            return float.PositiveInfinity;
        float lower = float.PositiveInfinity, upper = float.NegativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector4((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z, 1);
            var clip = Vector4.Transform(corner, viewProjection);
            if (!float.IsFinite(clip.W) || clip.W <= near) return float.PositiveInfinity;
            lower = Math.Min(lower, clip.Y / clip.W); upper = Math.Max(upper, clip.Y / clip.W);
        }
        return (upper - lower) * viewportHeight * .5f;
    }

    public static CanopyLod Select(float pixels, int residentLevels, bool baseline = false)
    {
        if (baseline || !float.IsFinite(pixels)) return Full;
        // A total 15% transition width around each threshold; no per-instance history.
        static float Blend(float pixels, float threshold)
        {
            float t = Math.Clamp((threshold * 1.075f - pixels) / (threshold * .15f), 0, 1);
            return t * t * (3 - 2 * t);
        }
        float a = Blend(pixels, 256), b = Blend(pixels, 112), c = Blend(pixels, 40);
        return new(Math.Min(a + b + c, Math.Max(0, residentLevels - 1)),
            (int)MathF.Ceiling(1536 - 1152 * a - 256 * b - 64 * c),
            pixels >= 256 ? 0 : pixels >= 112 ? 1 : pixels >= 40 ? 2 : 3);
    }
}
