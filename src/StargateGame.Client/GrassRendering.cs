using System.Numerics;

namespace StargateGame.Client;

internal static class GrassRendering
{
    // Min/max ground height and height scale, including linear-filter support.
    public static Vector4[] PatchRanges(ReadOnlySpan<Vector4> surface)
    {
        if (surface.Length != 33 * 33) throw new ArgumentException("Expected a 33x33 grass surface.");
        var result = new Vector4[16];
        for (int patch = 0; patch < 16; patch++)
        {
            int x0 = patch % 4 * 8, z0 = patch / 4 * 8;
            var range = new Vector4(float.PositiveInfinity, float.NegativeInfinity, float.PositiveInfinity, float.NegativeInfinity);
            for (int z = Math.Max(0, z0 - 1); z <= Math.Min(32, z0 + 9); z++)
            for (int x = Math.Max(0, x0 - 1); x <= Math.Min(32, x0 + 9); x++)
            {
                var value = surface[z * 33 + x];
                range.X = Math.Min(range.X, value.X); range.Y = Math.Max(range.Y, value.X);
                range.Z = Math.Min(range.Z, value.Z); range.W = Math.Max(range.W, value.Z);
            }
            result[patch] = range;
        }
        return result;
    }

    public static (Vector3 Min, Vector3 Max) Bounds(Vector2 patch, Vector4 range, float extent, float tileMin, float tileMax)
    {
        // The unchanged march interval bounds every sample in XZ, even under wind.
        // An occupied sample's unbent root is inside the patch, so its Y is bounded
        // by the filtered root field. Threshold reconstruction can move half a step.
        float padding = .5f * Math.Max(.08f, new Vector3(2.6f, tileMax - tileMin, 2.6f).Length() / 191) + .0001f;
        float low = range.X, high = range.Y + extent * Math.Max(.1f, range.W);
        if (!float.IsFinite(low) || !float.IsFinite(high) || low > high) { low = tileMin; high = tileMax; }
        return (new(patch.X - .3f - padding, low - padding, patch.Y - .3f - padding),
                new(patch.X + 2.3f + padding, high + padding, patch.Y + 2.3f + padding));
    }

    public static float LegacyMip(float distance) => Math.Clamp(MathF.Log2(Math.Max(1, distance / 32)), 0, 2);
    public static float SelectMip(Vector2 projectedPixels, float distance, float transitionFar)
    {
        float original = LegacyMip(distance);
        if (!float.IsFinite(projectedPixels.X) || !float.IsFinite(projectedPixels.Y)) return original;
        float footprint = Math.Clamp(MathF.Log2(Math.Max(128 / Math.Max(1, projectedPixels.X), 32 / Math.Max(1, projectedPixels.Y))), 0, 3);
        // Keep every point of a two-metre patch unchanged through the mesh overlap.
        float t = Math.Clamp((distance - 2 - transitionFar) / transitionFar, 0, 1); t = t * t * (3 - 2 * t);
        return float.Lerp(original, Math.Max(original, footprint), t);
    }

    public static Vector2 ProjectedSize(Vector3 min, Vector3 max, Vector3 eye, Matrix4x4 vp, Vector2 resolution)
    {
        if (Vector3.Min(eye, min) == min && Vector3.Max(eye, max) == max) return new(float.PositiveInfinity);
        Vector2 low = new(float.PositiveInfinity), high = new(float.NegativeInfinity);
        for (int i = 0; i < 8; i++)
        {
            var p = Vector4.Transform(new Vector4((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z, 1), vp);
            if (p.W <= .0001f || p.Z <= -p.W || !float.IsFinite(p.W)) return new(float.PositiveInfinity);
            var xy = new Vector2(p.X, p.Y) / p.W; low = Vector2.Min(low, xy); high = Vector2.Max(high, xy);
        }
        return (high - low) * resolution * .5f;
    }
}
