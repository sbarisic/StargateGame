namespace StargateGame.WorldGen;

/// <summary>Seed-local Perlin/fBm used by the approved regional terrain study.</summary>
// Adapted from SLTerrain::FBMGenerator / SLMath::PerlinNoise2D, Michael Tuck,
// MIT (thirdparty/slterraingeneration/LICENSE). No process-global random state.
internal sealed class RegionalTerrainNoise
{
    readonly int[] permutation = new int[256];
    readonly double offsetX, offsetZ;

    public RegionalTerrainNoise(int seed)
    {
        uint state = unchecked((uint)seed);
        uint Next()
        {
            state += 0x9e3779b9;
            uint v = state; v = (v ^ (v >> 16)) * 0x85ebca6b;
            v = (v ^ (v >> 13)) * 0xc2b2ae35;
            return v ^ (v >> 16);
        }
        for (int i = 0; i < 256; i++) permutation[i] = i;
        for (int i = 255; i > 0; i--)
        {
            int j = (int)(Next() % (uint)(i + 1));
            (permutation[i], permutation[j]) = (permutation[j], permutation[i]);
        }
        offsetX = 20 + Next() / (double)uint.MaxValue * 180;
        offsetZ = 20 + Next() / (double)uint.MaxValue * 180;
    }

    static double Fade(double t) => t*t*t*(t*(t*6-15)+10);
    static double Lerp(double a, double b, double t) => a + (b-a)*t;
    static double Gradient(int hash, double x, double z)
    {
        int h = hash & 15;
        double u = h < 8 ? x : z, v = h < 4 ? z : h is 12 or 14 ? x : 0;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
    double Perlin(double x, double z)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        x -= ix; z -= iz;
        double u = Fade(x), v = Fade(z);
        int a = permutation[ix & 255] + iz, b = permutation[(ix+1) & 255] + iz;
        return (Lerp(Lerp(Gradient(permutation[a & 255],x,z),Gradient(permutation[b & 255],x-1,z),u),
            Lerp(Gradient(permutation[(a+1) & 255],x,z-1),Gradient(permutation[(b+1) & 255],x-1,z-1),u),v)+1)*.5;
    }
    public float Fbm(float x, float z, float wavelength, int octaves = 7, double hurst = 1)
    {
        double sum = 0, total = 0, weight = 1, frequency = 1;
        double gain = Math.Pow(2, -hurst);
        for (int i = 0; i < octaves; i++)
        {
            sum += Perlin(x/wavelength*frequency+offsetX,z/wavelength*frequency+offsetZ)*weight;
            total += weight; frequency *= 2; weight *= gain;
        }
        return (float)(sum/total);
    }
}
