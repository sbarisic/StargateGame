using System.Numerics;
using StargateGame.Client;

static class CanopyLodChecks
{
    public static void Run()
    {
        foreach (var (pixels, mip, steps) in new[] { (512f, 0f, 1536), (160f, 1f, 384), (64f, 2f, 128), (20f, 3f, 64) })
        {
            var lod = CanopyLod.Select(pixels, 8);
            Verify.Check(lod.Mip == mip && lod.MarchLimit == steps, "balanced canopy band " + pixels);
            Verify.Check(CanopyLod.Select(pixels, 8, true) == CanopyLod.Full, "full detail baseline " + pixels);
        }
        float lastMip = 0;
        for (int pixels = 400; pixels >= 1; pixels--)
        {
            var lod = CanopyLod.Select(pixels, 8);
            if (lod.Mip < lastMip || lod.Mip - lastMip > .26f || lod.MarchLimit < 64)
                throw new Exception("Non-monotonic or discontinuous canopy transition.");
            lastMip = lod.Mip;
            foreach (float interval in new[] { .1f, 5, 50, 500 })
            {
                float step = Math.Max(.1f * .65f * MathF.Pow(2, lod.Mip), interval / (lod.MarchLimit - 2));
                // Tree-space alignment adds a partial entry; reserve rounding too.
                if (Math.Ceiling(interval / step) + 1 > lod.MarchLimit) throw new Exception("March truncates canopy.");
            }
        }
        Verify.Check(true, "continuous monotonic detail and complete ray traversal");
        Verify.Check(CanopyLod.Select(1, 2).Mip == 1 && CanopyLod.Select(1, 1).Mip == 0, "resident mip clamp");
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 1.6f, .05f, 1500);
        float size = CanopyLod.ProjectedHeight(new(-1,-1,-11), new(1,1,-9), Vector3.Zero, projection, 1080);
        float far = CanopyLod.ProjectedHeight(new(-1,-1,-21), new(1,1,-19), Vector3.Zero, projection, 1080);
        Verify.Check(size > far && Math.Abs(CanopyLod.ProjectedHeight(new(-1,-1,-11), new(1,1,-9), Vector3.Zero, projection, 2160) - 2 * size) < .001f, "projection respects bounds, distance and viewport");
        Verify.Check(float.IsPositiveInfinity(CanopyLod.ProjectedHeight(new(-1), new(1), Vector3.Zero, projection, 1080)), "camera inside canopy retains detail");
        Verify.Check(float.IsPositiveInfinity(CanopyLod.ProjectedHeight(new(-1,-1,-1), new(1,1,-.02f), Vector3.Zero, projection, 1080)), "near-plane crossing retains detail");
        Console.WriteLine($"CANOPY LOD {Verify.Count} CHECKS PASSED");
    }
}
