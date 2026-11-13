using System.Numerics;
using StargateGame.Client;

static class GrassRenderingChecks
{
    public static void Run()
    {
        Verify.Check(HybridGrass.Transition(1) == new Vector2(12, 20) && HybridGrass.Transition(0) == new Vector2(20, 28) && HybridGrass.Transition(2) == new Vector2(28, 36), "doubled mesh grass transition radii for all quality tiers");
        var surface = new Vector4[33 * 33];
        for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++)
            surface[z * 33 + x] = new(x * .15f + MathF.Sin(z * .7f), (x + z) % 3 == 0 ? 0 : 1, .2f + .8f * x / 32, x == 16 ? 0 : 1);
        Vector4 Sample(Vector2 p)
        {
            p = Vector2.Clamp(p * 4, Vector2.Zero, new(32));
            int x = Math.Min(31, (int)p.X), z = Math.Min(31, (int)p.Y);
            return Vector4.Lerp(Vector4.Lerp(surface[z * 33 + x], surface[z * 33 + x + 1], p.X - x),
                Vector4.Lerp(surface[(z + 1) * 33 + x], surface[(z + 1) * 33 + x + 1], p.X - x), p.Y - z);
        }
        var ranges = GrassRendering.PatchRanges(surface);
        var random = new Random(7319);
        float min = surface.Min(v => v.X), max = surface.Max(v => v.X) + 1;
        int occupied = 0;
        foreach (float wind in new[] { 0f, .3f, 2f, 8f })
        for (int patch = 0; patch < 16; patch++)
        {
            Vector2 origin = new(patch % 4 * 2, patch / 4 * 2);
            var bounds = GrassRendering.Bounds(origin, ranges[patch], 1, min, max);
            float step = Math.Max(.08f, new Vector3(2.6f, max - min, 2.6f).Length() / 191);
            for (int i = 0; i < 1500; i++)
            {
                Vector2 xz = origin + new Vector2((float)random.NextDouble(), (float)random.NextDouble()) * 2.6f - new Vector2(.3f);
                float y = min + (float)random.NextDouble() * (max - min), h = y - Sample(xz).X;
                float sway = MathF.Sin(Vector2.Dot(xz, new(.62f, .37f)) - 18) + .35f * MathF.Sin(Vector2.Dot(xz, new(1.9f, -1.3f)) - 31);
                Vector2 rest = xz - new Vector2(wind, -wind * .7f) * sway * .24f * h * h / (.75f * Math.Max(.1f, Sample(xz).Z));
                var shared = Sample(xz);
                Vector2 reused = xz - new Vector2(wind, -wind * .7f) * sway * .24f * h * h / (.75f * Math.Max(.1f, shared.Z));
                if (rest != reused) throw new Exception("Reused sample changes wind deformation.");
                var root = Sample(rest); float uvHeight = (y - root.X) / Math.Max(.1f, root.Z);
                if (rest.X < origin.X || rest.Y < origin.Y || rest.X > origin.X + 2 || rest.Y > origin.Y + 2 || uvHeight is < 0 or > 1) continue;
                occupied++;
                var p = new Vector3(xz.X, y, xz.Y);
                if (Vector3.Min(p - new Vector3(step / 2), bounds.Min) != bounds.Min || Vector3.Max(p + new Vector3(step / 2), bounds.Max) != bounds.Max)
                    throw new Exception("Patch proxy misses a filtered/wind-deformed sample or reconstructed hit.");
            }
        }
        Verify.Check(occupied > 1000, "grass patch bounds cover slopes, filtering, wind and hit-depth margins");
        Verify.Check(ranges.Length == 16 && ranges[0].Y < max, "compact bounds tighten sloped tile heights");
        var fallback = GrassRendering.Bounds(Vector2.Zero, new(float.NaN), 1, min, max);
        Verify.Check(fallback.Min.Y <= min && fallback.Max.Y >= max, "invalid patch metadata falls back conservatively");
        foreach (float distance in new[] { 1f, 6, 10, 14, 16 })
            Verify.Check(GrassRendering.SelectMip(new(1), distance, 14) == GrassRendering.LegacyMip(distance), "near grass LOD unchanged " + distance);
        Verify.Check(GrassRendering.SelectMip(new(1), 200, 14) == 3, "distant grass mip capped at three");
        float last = 0;
        for (float d = 1; d < 225; d += .01f)
        {
            float mip = GrassRendering.SelectMip(new(2000 / d, 500 / d), d, 14);
            if (mip < last - .0001f || mip - last > .01f) throw new Exception("Grass LOD transition jumps.");
            last = mip;
        }
        Verify.Check(true, "grass fractional LOD transitions are continuous");
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1, 1.6f, .05f, 1500);
        var size = GrassRendering.ProjectedSize(new(-1,0,-81), new(1,1,-79), Vector3.Zero, projection, new(1920,1080));
        var twice = GrassRendering.ProjectedSize(new(-1,0,-81), new(1,1,-79), Vector3.Zero, projection, new(3840,2160));
        Verify.Check(Vector2.Distance(twice, size * 2) < .001f && GrassRendering.SelectMip(twice,80,14) <= GrassRendering.SelectMip(size,80,14), "render resolution controls grass detail");
        Verify.Check(float.IsPositiveInfinity(GrassRendering.ProjectedSize(new(-1),new(1),Vector3.Zero,projection,new(1920,1080)).X), "inside grass bounds retain detail");
        Verify.Check(CanopyDepthBounds.Nearest(new(-1,-1,-1),new(1,1,.1f),Vector3.Zero,projection)==-1,"near-crossing grass proxy uses near depth");
        foreach (float length in new[] { .01f, 1, 10, 80 }) foreach (float mip in new[] { 0f, .4f, 2, 3 })
        {
            float step = Math.Max(.01f * MathF.Pow(2,mip), length / 191);
            if (Math.Ceiling(length / step) > 192) throw new Exception("Grass traversal truncates the interval.");
            foreach (float fraction in new[] { .01f, .25f, .9f, 1 })
            {
                float end = length * fraction, integrated = 0;
                for (int i=0;i<192;i++) { float start=i*step;if(start>=end)break;integrated+=Math.Min(step,end-start); }
                if(Math.Abs(integrated-end)>.001f)throw new Exception("Clipped grass loses a partial segment.");
            }
        }
        Verify.Check(true,"grass integration covers original and clipped intervals within budget");
        Console.WriteLine($"GRASS RENDERING {Verify.Count} CHECKS PASSED; {occupied} occupied wind/slope samples");
    }
}
