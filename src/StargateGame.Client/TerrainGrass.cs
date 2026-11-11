using System.Numerics;

namespace StargateGame.Client;

// CPU counterpart of terrainMaterial's layer selection. Eligibility comes from
// the selected images, not an independent biome, slope or habitat veto. Micro
// height blending can redistribute these layers but cannot introduce a new one.
static class TerrainGrass
{
    public static float Coverage(string preset, Vector2 position, Vector3 normal, Vector4 material,
        Vector4 environment, Vector2 habitat, uint seed, ReadOnlySpan<float> layers)
    {
        float S(float a, float b, float x) => SurfaceEcology.Smooth(a, b, x);
        float canopy = Math.Max(environment.X, habitat.X);
        float wet = environment.Z > -4000 ? Math.Max(environment.Y, material.Z * .45f) : material.Z * .45f;
        float bank = S(.4f, .85f, wet), patch = SurfaceEcology.Patch(position, seed);
        float large = SurfaceEcology.Noise(position / 43, seed + 51);
        float cliff = S(.55f, 1, material.Y), dry = S(.5f, .78f, patch), shade = S(.15f, .8f, canopy);
        Span<float> w = stackalloc float[8]; w.Clear();
        int rock;
        if (preset == "desert")
        {
            rock = 6; float sand = S(.15f, 2.5f, habitat.Y) * (1 - wet);
            w[0] = sand * (1 - large); w[1] = sand * large;
            w[2] = (1 - sand) * (1 - wet) * (1 - large); w[3] = (1 - sand) * (1 - wet) * large;
            w[4] = cliff * (1 - cliff) * 2; w[5] = cliff * .35f; w[7] = bank;
        }
        else if (preset == "icy")
        {
            rock = 5; w[0] = (1 - dry) * (1 - shade * .5f) * (1 - wet * .5f);
            w[1] = large * .6f; w[2] = dry * .7f; w[3] = cliff * (1 - cliff) * 2;
            w[4] = wet * .45f * (1 - shade); w[6] = shade * dry * .65f; w[7] = wet * dry * .5f;
        }
        else
        {
            rock = 7; w[0] = (1 - shade) * (1 - wet) * (1 - dry + .25f + large * .5f);
            w[2] = shade * (1 - wet * .7f); w[3] = shade * wet;
            w[4] = bank * (1 - cliff) * (1 - large); w[5] = bank * large;
            w[6] = dry * (1 - shade) * (1 - wet);
        }
        int a = 0, b = 0; float first = -1, second = -1, third = 0;
        for (int i = 0; i < 8; i++)
        {
            if (i == rock) continue;
            if (w[i] > first) { third = second; second = first; b = a; first = w[i]; a = i; }
            else if (w[i] > second) { third = second; second = w[i]; b = i; }
            else third = Math.Max(third, w[i]);
        }
        float aw = Math.Max(.001f, first - Math.Max(0, third)), bw = Math.Max(0, second - Math.Max(0, third));
        float result = (layers[a] * aw + layers[b] * bw) / (aw + bw) * (1 - cliff) + layers[rock] * cliff;
        if (preset is not ("desert" or "icy"))
        {
            if (habitat.X < 0) result = float.Lerp(result, layers[1], Math.Clamp(-habitat.X, 0, 1));
            result *= 1 - material.W * S(.45f, .85f, normal.Y);
        }
        return Math.Clamp(result, 0, 1);
    }
}
