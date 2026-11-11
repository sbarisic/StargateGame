using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed record GroundCoverSettings
{
    public bool Hybrid { get; init; } = true;
    public bool Enabled
    {
        get;
        init;
    } = true;
    public int DensityOverride
    {
        get;
        init;
    }
    public (int Range, int Density) EffectiveBudget(int quality)
    {
        var b = Hybrid ? HybridBudget(quality) : Budget(quality);
        return (b.Range, DensityOverride > 0 ? DensityOverride : b.Density);
    }
    public float MinHeight
    {
        get;
        init;
    } = .10f;
    public float MaxHeight
    {
        get;
        init;
    } = .28f;
    public float MinFootprint
    {
        get;
        init;
    } = .20f;
    public float MaxFootprint
    {
        get;
        init;
    } = .45f;
    public float PatchStrength
    {
        get;
        init;
    } = .85f;
    public float BodyTerrainBlend
    {
        get;
        init;
    } = .08f;
    public float RootTerrainBlend
    {
        get;
        init;
    } = .25f;
    public static int MiddleRange(int quality) => quality switch { 1 => 20, 2 => 45, _ => 30 };
    public static int DetailRange(int quality) => quality switch { 1 => 8, 2 => 16, _ => 12 };
    public static (int Range, int Density) Budget(int quality) => quality switch { 1 => (64, 12), 2 => (160, 24), _ => (112, 24) };
    public static (int Range, int Density) HybridBudget(int quality) => quality switch { 1 => (128, 12), 2 => (320, 24), _ => (224, 24) };
    // Matches grass.vert. Outer tiles store only the ranked subset that can be visible.
    public static float DistanceDensity(float distance, float range)
    {
        static float Smooth(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }
        return (1 - .8f * Smooth(8, 30, distance)) * (1 - .5f * Smooth(40, range, distance));
    }
}

enum WinterWeather
{
    Auto, Off, LightSnow, Storm
}
sealed record WeatherSettings
{
    public bool DesertDustEnabled
    {
        get;
        init;
    } = true;
    public WinterWeather Mode
    {
        get;
        init;
    } = WinterWeather.Auto;
    public float BlowingSnowDensity
    {
        get;
        init;
    } = .18f;
    public bool GroundMistEnabled
    {
        get;
        init;
    } = true;
    public float GroundMistDensity
    {
        get;
        init;
    } = .12f;
    public float GroundMistBlanketHeight
    {
        get;
        init;
    } = .6f;
    public float GroundMistWispHeight
    {
        get;
        init;
    } = 1.2f;
    public float GroundMistSpeed
    {
        get;
        init;
    } = .3f;
    public static WinterWeather Parse(string value) => value switch
    {
        "auto" => WinterWeather.Auto,
        "off" => WinterWeather.Off,
        "light-snow" => WinterWeather.LightSnow,
        "storm" => WinterWeather.Storm,
        _ => throw new ArgumentException("--weather must be auto, off, light-snow, or storm.")
    };
    public static (int Flakes, int ShelterResolution) Budget(int quality) => quality switch { 1 => (1500, 256), 2 => (8000, 1024), _ => (4000, 512) };
}
readonly record struct WeatherFrame(bool Snow, float Gust, float Snowfall, Vector2 Wind, float SunScale, float AmbientScale, Vector3 Fog, double Time)
{
    // Integral of gust-driven speed: clouds and particles never jump when wind changes.
    public static double WindTravel(int seed, double time, WinterWeather mode)
    {
        double phase = (uint)seed * .013, w1 = Math.Tau / 47, w2 = Math.Tau / 31;
        return mode switch
        {
            WinterWeather.LightSnow => time * 2,
            WinterWeather.Storm => time * 7,
            _ => time * 4.5 - 1.6 / w1 * Math.Cos(time * w1 + phase) - .9 / w2 * Math.Cos(time * w2 + phase * .63)
        };
    }
    public static WeatherFrame Sample(string preset, int seed, double time, WinterWeather mode)
    {
        if (mode == WinterWeather.Off || preset != "icy" && mode == WinterWeather.Auto)
            return new(false, 0, 0, new(1.2f, .4f), 1, 1, default, time);
        double phase = (uint)seed * .013;
        float g = mode switch
        {
            WinterWeather.LightSnow => 0,
            WinterWeather.Storm => 1,
            _ => (float)Math.Clamp(.5 + .32 * Math.Sin(time * Math.Tau / 47 + phase) + .18 * Math.Sin(time * Math.Tau / 31 + phase * .63), 0, 1)
        };
        float density = MathF.Log(2) / (115 - 50 * g);
        return new(true, g, .35f + .65f * g, Vector2.Normalize(new(1, .38f)) * (2 + 5 * g), .35f - .20f * g, .80f - .15f * g,
            new(density * .4f, density * .6f, .012f), time);
    }
}
// Render-clock extrapolation smooths snapshot cadence without changing server time.
sealed class WeatherClock
{
    bool initialized;
    Guid location;
    double time, last;
    public double Update(Guid id, double serverTime, double now)
    {
        if (!initialized || id != location || now < last || now - last > .5 || Math.Abs(serverTime - time) > 2)
        {
            initialized = true;
            location = id;
            time = serverTime;
            last = now;
            return time;
        }
        double dt = Math.Clamp(now - last, 0, .25);
        last = now;
        time += dt;
        time += (serverTime - time) * Math.Min(1, dt * 2);
        return time;
    }
    public void Clear() => initialized = false;
}

readonly record struct GrassTile(int X, int Z)
{
    public const int Size = 8;
    public static GrassTile At(float x, float z) => new((int)MathF.Floor(x / Size), (int)MathF.Floor(z / Size));
    public Vector2 Centre => new(X * Size + 4, Z * Size + 4);
}
readonly record struct CoverExclusion(Vector3 Min, Vector3 Max)
{
    public bool Contains(Vector3 p) => p.X >= Min.X - .12f && p.X <= Max.X + .12f && p.Z >= Min.Z - .12f && p.Z <= Max.Z + .12f && p.Y + .5f >= Min.Y && p.Y <= Max.Y + .1f;
}
sealed record GrassTuft(Vector3 Root, float Height, float Yaw, float Rank, float Shade, float Footprint = .3f);
sealed record GrassTileData(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, Vector4[] Colors, Vector3 Min, Vector3 Max);
static class GroundCover
{
    public static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        return x ^ (x >> 16);
    }
    public static uint PlanetSalt(PlanetDefinition planet)
    {
        uint hash = (uint)planet.Seed;
        foreach (byte b in planet.LocationId.ToByteArray())
            hash = Hash(hash ^ b);
        return hash;
    }
    static float Unit(uint h) => (h & 0xffffff) / 16777216f;
    public static IReadOnlyList<GrassTuft> Candidates(GrassTile tile, TerrainChunkData chunk, PlanetDefinition planet,
        GroundCoverSettings settings, float threshold, IReadOnlyList<CoverExclusion> exclusions, SurfaceMap? ecology = null)
    {
        List<GrassTuft> result = [];
        uint salt = PlanetSalt(planet);
        var gate = planet.Entities.FirstOrDefault(e => e.Model == "stargate");
        for (int z = tile.Z * 8; z < tile.Z * 8 + 8; z++)
            for (int x = tile.X * 8; x < tile.X * 8 + 8; x++)
                for (int index = 0; index < 24; index++)
                {
                    uint h = Hash(unchecked((uint)x * 73856093u ^ (uint)z * 19349663u ^ (uint)index * 83492791u) ^ salt);
                    float rank = Unit(h);
                    if (rank >= threshold)
                        continue;
                    float px = x + Unit(Hash(h + 1)), pz = z + Unit(Hash(h + 2));
                    int ix = x - chunk.Coordinate.X * 64, iz = z - chunk.Coordinate.Z * 64;
                    if (ix < 0 || ix >= 64 || iz < 0 || iz >= 64)
                        continue;
                    float fx = px - x, fz = pz - z;
                    var a = chunk.Height(ix, iz);
                    var b = chunk.Height(ix + 1, iz);
                    var c = chunk.Height(ix, iz + 1);
                    var d = chunk.Height(ix + 1, iz + 1);
                    float y = fx + fz <= 1 ? a + (b - a) * fx + (c - a) * fz : d + (c - d) * (1 - fx) + (b - d) * (1 - fz);
                    var p = new Vector3(px, y - .015f, pz);
                    var normal = chunk.Normal(ix, iz);
                    var material = chunk.Material(ix, iz);
                    float suitability = CoverageAt(p, normal, material, chunk, planet, exclusions);
                    var env = ecology?.Sample(px, pz) ?? new Vector4(0, .25f, y, 1);
                    float patch = SurfaceEcology.Patch(new(px, pz), salt & 65535);
                    float meadow = SurfaceEcology.MeadowCoverage(env, patch, settings.PatchStrength);
                    // A short continuous understorey with denser patches, rather than
                    // isolated identical tufts. Hard exclusions still apply above.
                    suitability *= (ecology?.GrassAt(px, pz) ?? 1) * env.W * float.Lerp(.45f, 1f, meadow) * float.Lerp(1, .40f, env.X) * .55f;
                    if (Unit(Hash(h + 3)) > suitability || suitability <= 0)
                        continue;
                    float growth = Math.Clamp(.15f + env.Y * .35f - env.X * .20f + SurfaceEcology.Smooth(.3f, .75f, patch) * .35f + Unit(Hash(h + 4)) * .25f, 0, 1);
                    result.Add(new(p, float.Lerp(settings.MinHeight, settings.MaxHeight, growth), Unit(Hash(h + 5)) * MathF.Tau, rank, .90f + .10f * Unit(Hash(h + 6)), float.Lerp(settings.MinFootprint, settings.MaxFootprint, Unit(Hash(h + 7)))));
                }
        return result;
    }
    // Physical path reservation only. Vegetation suitability is supplied by
    // the displayed terrain images through SurfaceMap.GrassAt.
    public static float TerrainCoverage(Vector3 normal, Vector4 material)
        => material.X > .04f ? 0 : 1;
    public static float CoverageAt(Vector3 p, Vector3 normal, Vector4 material, TerrainChunkData chunk, PlanetDefinition planet, IReadOnlyList<CoverExclusion> exclusions)
    {
        float terrain = TerrainCoverage(normal, material);
        if (terrain <= 0)
            return 0;
        int x = Math.Clamp((int)MathF.Floor(p.X - chunk.Coordinate.X * 64), 0, 63), z = Math.Clamp((int)MathF.Floor(p.Z - chunk.Coordinate.Z * 64), 0, 63);
        if (float.IsFinite(chunk.WaterHeight(x, z)) || float.IsFinite(chunk.WaterHeight(x + 1, z)) || float.IsFinite(chunk.WaterHeight(x, z + 1)) || float.IsFinite(chunk.WaterHeight(x + 1, z + 1)))
            return 0;
        float value = terrain * Math.Clamp(.78f + material.Z * .22f, .25f, 1);
        var gate = planet.Entities.FirstOrDefault(e => e.Model == "stargate");
        if (gate is not null)
        {
            var d = new Vector2(p.X - gate.Position.X, p.Z - gate.Position.Z);
            if (Math.Abs(d.X) < 3.4f && d.Y > -1.5f && d.Y < 19)
                return 0;
            if (d.Length() < 12)
                value *= .22f;
        }
        if (planet.Spawns.Any(s => Vector2.DistanceSquared(new(s.X, s.Z), new(p.X, p.Z)) < 2.25f) || planet.Entities.Any(e => e.Model == "dhd" && Vector2.DistanceSquared(new(e.Position.X, e.Position.Z), new(p.X, p.Z)) < 1.69f) || exclusions.Any(e => e.Contains(p)))
            return 0;
        return value;
    }
    public static byte[] CoverageMap(TerrainChunkData chunk, PlanetDefinition planet, CoverExclusion[] exclusions, CancellationToken token)
    {
        byte[] result = new byte[64 * 64];
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                token.ThrowIfCancellationRequested();
                float wx = chunk.Coordinate.X * 64 + x + .5f, wz = chunk.Coordinate.Z * 64 + z + .5f;
                result[z * 64 + x] = (byte)(255 * CoverageAt(new(wx, chunk.Sample(wx, wz), wz), chunk.Normal(x, z), chunk.Material(x, z), chunk, planet, exclusions));
            }
        return result;
    }
    public sealed record InstanceData(Vector4[] Records, int Count, Vector3 Min, Vector3 Max, Vector4[]? Surface = null);
    public static InstanceData Instances(GrassTile tile, TerrainChunkData chunk, PlanetDefinition planet, GroundCoverSettings settings, CoverExclusion[] exclusions, CancellationToken token, SurfaceMap? ecology = null, float maximumRank = 1, bool volumeOnly = false)
    {
        var tufts = Candidates(tile, chunk, planet, settings, maximumRank, exclusions, ecology).OrderBy(t => t.Rank).ToArray();
        if (settings.Hybrid && volumeOnly)
        {
            var field = HybridGrass.Surface(tile, chunk, planet, settings, exclusions, ecology, tufts, token);
            // Hybrid rendering consumes only the surface field. Do not generate
            // eight fallback blade roots and six GPU record texels per tuft.
            return new([], tufts.Length, new(tile.X * 8 - .4f, field.Min(p => p.X) - .05f, tile.Z * 8 - .4f),
                new(tile.X * 8 + 8.4f, field.Max(p => p.X) + .9f, tile.Z * 8 + 8.4f), field);
        }
        int texels = Math.Max(128, ((tufts.Length * 6 + 127) / 128) * 128);
        Vector4[] records = new Vector4[texels];
        var min = new Vector3(tile.X * 8, 0, tile.Z * 8);
        var max = min + new Vector3(8, 0, 8);
        for (int i = 0; i < tufts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var t = tufts[i];
            int offset = i * 6;
            records[offset] = new(t.Root, t.Height);
            records[offset + 1] = new(t.Yaw, t.Rank, t.Shade, t.Footprint);
            for (int blade = 0; blade < 8; blade++)
            {
                var root = BladeRoot(t, blade, chunk);
                int x = Math.Clamp((int)MathF.Floor(root.X) - chunk.Coordinate.X * 64, 0, 63), z = Math.Clamp((int)MathF.Floor(root.Z) - chunk.Coordinate.Z * 64, 0, 63);
                // Check each blade too: a clump may straddle a path or foundation edge.
                float y = CoverageAt(root, chunk.Normal(x, z), chunk.Material(x, z), chunk, planet, exclusions) > 0 && (ecology?.Sample(root.X, root.Z).W ?? 1) > .5f ? root.Y : -4096;
                records[offset + 2 + blade / 4][blade % 4] = y;
            }
            int ix = Math.Clamp((int)MathF.Floor(t.Root.X) - chunk.Coordinate.X * 64, 0, 63), iz = Math.Clamp((int)MathF.Floor(t.Root.Z) - chunk.Coordinate.Z * 64, 0, 63);
            records[offset + 4] = chunk.Material(ix, iz);
            records[offset + 5] = new(chunk.Normal(ix, iz), 0);
            min.Y = Math.Min(min.Y, t.Root.Y);
            max.Y = Math.Max(max.Y, t.Root.Y + t.Height);
        }
        var surface = settings.Hybrid ? HybridGrass.Surface(tile, chunk, planet, settings, exclusions, ecology, tufts, token) : null;
        if (surface is not null) { min.Y = surface.Min(p => p.X); max.Y = surface.Max(p => p.X) + .8f; }
        return new(records, tufts.Length, min - new Vector3(.4f, .05f, .4f), max + new Vector3(.4f, .1f, .4f), surface);
    }
    public static Vector3 BladeRoot(GrassTuft tuft, int blade, TerrainChunkData chunk)
    {
        float angle = tuft.Yaw + blade * 2.39996f, radius = tuft.Footprint * (.24f + .05f * (blade % 3));
        float x = tuft.Root.X + MathF.Cos(angle) * radius, z = tuft.Root.Z + MathF.Sin(angle) * radius;
        return new(x, chunk.Sample(x, z) - .015f, z);
    }
    public static GrassTileData Build(GrassTile tile, TerrainChunkData chunk, PlanetDefinition planet, GroundCoverSettings settings, float threshold, CoverExclusion[] exclusions, CancellationToken cancellation)
    {
        List<Vector3> positions = [], normals = [];
        List<Vector2> uv = [];
        List<Vector4> colors = [];
        var min = new Vector3(tile.X * 8, float.MaxValue, tile.Z * 8);
        var max = new Vector3(tile.X * 8 + 8, float.MinValue, tile.Z * 8 + 8);
        foreach (var tuft in Candidates(tile, chunk, planet, settings, threshold, exclusions))
        {
            cancellation.ThrowIfCancellationRequested();
            min.Y = Math.Min(min.Y, tuft.Root.Y);
            max.Y = Math.Max(max.Y, tuft.Root.Y + tuft.Height + .1f);
            for (int blade = 0; blade < 8; blade++)
            {
                float angle = tuft.Yaw + blade * 2.39996f;
                var side = new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle));
                var facing = new Vector3(-side.Z, 0, side.X);
                var root = BladeRoot(tuft, blade, chunk);
                float height = Math.Clamp(tuft.Height * (.78f + blade * .073f), settings.MinHeight, settings.MaxHeight), width = .006f + height * .010f;
                var mid = root + Vector3.UnitY * height * .52f + facing * height * .10f;
                var tip = root + Vector3.UnitY * height + facing * height * .28f;
                void Vertex(Vector3 p, float stem)
                {
                    positions.Add(p);
                    normals.Add(Vector3.Normalize(facing + Vector3.UnitY * .85f));
                    uv.Add(new(tuft.Rank, stem));
                    colors.Add(new(tuft.Shade, tuft.Shade, tuft.Shade, 1));
                }
                Vertex(root - side * width, 0);
                Vertex(mid - side * width * .62f, height * .52f);
                Vertex(root + side * width, 0);
                Vertex(root + side * width, 0);
                Vertex(mid - side * width * .62f, height * .52f);
                Vertex(mid + side * width * .62f, height * .52f);
                Vertex(mid - side * width * .62f, height * .52f);
                Vertex(tip, height);
                Vertex(mid + side * width * .62f, height * .52f);
            }
        }
        if (positions.Count == 0)
        {
            min.Y = 0;
            max.Y = 0;
        }
        return new(positions.ToArray(), normals.ToArray(), uv.ToArray(), colors.ToArray(), min - new Vector3(.3f, 0, .3f), max + new Vector3(.3f, 0, .3f));
    }
}
