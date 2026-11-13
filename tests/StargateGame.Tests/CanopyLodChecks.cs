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
        Verify.Check(CanopyLightLod.Select(512,true)==new CanopyLightLod(24,24,0),"near light quality unchanged");
        Verify.Check(CanopyLightLod.Select(160,true).First==12&&CanopyLightLod.Select(64,true).First==8&&CanopyLightLod.Select(20,true).First==6,"distant light budgets");
        Verify.Check(CanopyLightLod.Select(20,false).First==24&&CanopyLightLod.Select(float.PositiveInfinity,true).First==24,"light baseline and inside crown");
        float previous=24;
        for(float pixels=400;pixels>=1;pixels-=.1f) {
            var light=CanopyLightLod.Select(pixels,true);float cost=light.First+(light.Second-light.First)*light.Blend;
            if(cost>previous+.0001f||previous-cost>.05f)throw new Exception("Light transition discontinuity");previous=cost;
        }
        Verify.Check(true,"smooth monotonic light blend across thresholds");
        Verify.Check(CanopyDepthBounds.Nearest(new(-1),new(1),Vector3.Zero,projection)==-1,"inside proxy uses near depth");
        Verify.Check(CanopyDepthBounds.Nearest(new(-1,-1,-1),new(1,1,.1f),Vector3.Zero,projection)==-1,"crossing proxy uses near depth");
        var low=new Vector3(-2,-3,-15);var high=new Vector3(3,4,-8);
        float nearest=CanopyDepthBounds.Nearest(low,high,Vector3.Zero,projection);
        for(int i=0;i<=10;i++)for(int j=0;j<=10;j++)for(int k=0;k<=10;k++) {
            var v=Vector4.Transform(new Vector4(low+(high-low)*new Vector3(i,j,k)/10,1),projection);
            if(nearest>v.Z/v.W)throw new Exception("Nonconservative depth bound");
        }
        Verify.Check(true,"proxy depth never exceeds any enclosed hit");
        Console.WriteLine($"CANOPY LOD {Verify.Count} CHECKS PASSED");
    }
}
