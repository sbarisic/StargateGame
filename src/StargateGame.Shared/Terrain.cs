using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace StargateGame.Shared;

public readonly record struct ChunkCoordinate(int X, int Z) : IComparable<ChunkCoordinate>
{
    public const int Size = 64, Samples = 65;
    public bool Valid => X >= -WorldDomain.HalfChunks && X < WorldDomain.HalfChunks && Z >= -WorldDomain.HalfChunks && Z < WorldDomain.HalfChunks;
    public int CompareTo(ChunkCoordinate b) => Z != b.Z ? Z.CompareTo(b.Z) : X.CompareTo(b.X);
    public static ChunkCoordinate At(float x, float z) => new((int)MathF.Floor(x / Size), (int)MathF.Floor(z / Size));
    public static IEnumerable<ChunkCoordinate> All => Enumerable.Range(-WorldDomain.HalfChunks, WorldDomain.ChunkCount).SelectMany(z => Enumerable.Range(-WorldDomain.HalfChunks, WorldDomain.ChunkCount).Select(x => new ChunkCoordinate(x, z)));
    public IEnumerable<ChunkCoordinate> Around(int radius)
    {
        for (int z = Z - radius; z <= Z + radius; z++)
            for (int x = X - radius; x <= X + radius; x++)
            {
                var c = new ChunkCoordinate(x, z);
                if (c.Valid)
                    yield return c;
            }
    }
}

public static class PlanetSettings
{
    public const int Version = 10;
    public const int SceneryVersion = 4;
    public static bool Supports(int version) => version == Version;
    // Match the supplied outer-diameter reference, preserving the authored ring proportions.
    public const float GateScale = 6.7056f / 6.127384f;
    public const float GateEmbedDepth = 1.25f * GateScale;
    public static readonly string[] Presets = ["temperate", "desert", "icy"];
    public static string Hash(int seed, string preset, int version = Version, SettlementSettings? settlements = null, PlanetGenerationSettings? generation = null) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"worldgen:{version}:{seed}:{preset}:4096:64:1:mm:water-4:regional-6:scenery-{SceneryVersion}:settlements-{SettlementSettings.Version}-{SettlementSettings.LayoutRevision}:{SettlementTemplates.Hash}:{(settlements ?? new()).Type}:{(settlements ?? new()).Style}{":generation:" + (generation ?? new()).Fingerprint}"))));
    public static void Validate(PlanetDefinition d)
    {
        if (d is not null && d.Fixture is not null)
        {
            GraphicsFixture.Validate(d);
            return;
        }
        d?.SettlementGeneration.Validate();
        d?.Generation.Validate();
        d?.Environment?.Validate();
        d?.Settlements?.Validate();
        if (d is null || d.Spawns is null || d.Entities is null || d.Props is null || d.Name is null || d.Name.Length > 96 || d.Entities.Any(e => e is null) || d.Props.Any(p => p is null) || !Supports(d.GeneratorVersion) || !Presets.Contains(d.Preset) || d.HalfSize != WorldDomain.HalfSize || d.SettingsHash != Hash(d.Seed, d.Preset, d.GeneratorVersion, d.SettlementGeneration, d.Generation) ||
           d.Id != $"planet-{d.Preset}-{d.Seed}" || d.Spawns.Length != 8 || d.Entities.Length != 2 || d.Props.Length > 64)
            throw new InvalidDataException("Unsupported planet settings.");
        static bool Position(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z) && Math.Abs(p.X) <= WorldDomain.HalfSize && Math.Abs(p.Z) <= WorldDomain.HalfSize && Math.Abs(p.Y) <= 2048;
        if (d.Spawns.Any(p => !Position(p)) || d.Entities.Any(e => !Position(e.Position) || e.Scale != (e.Model == "stargate" ? GateScale : 1f)) || d.Props.Any(p => !Position(p.Position)) ||
           !d.Entities.Select(e => e.Model).Order().SequenceEqual(new[] { "dhd", "stargate" }) ||
            d.Props.Any(p => p.Id is < 1 or > 64) || d.Props.Select(p => p.Id).Distinct().Count() != d.Props.Length || d.Props.Any(p => !p.Valid))
            throw new InvalidDataException("Invalid planet objects.");
    }
}

public readonly record struct PlacementCandidate(ulong Id, string Category, Vector3 Position, float Yaw, float Scale);
public sealed record SceneryAsset(string Id, string Category, string Model, float Density, float MinScale, float MaxScale, float MaxSlope, string? CollisionProxy);
public sealed record SceneryCatalogue(int Version, SceneryAsset[] Assets)
{
    public static SceneryCatalogue Empty
    {
        get;
    } = new(1, []);
}

public interface ITerrainSource
{
    float SampleHeight(float x, float z);
    bool TryGetChunk(ChunkCoordinate coordinate, out TerrainChunkData chunk);
}

/// <summary>Immutable canonical samples. A one-sample halo supplies shared border normals.</summary>
public sealed class TerrainChunkData
{
    public const int HaloSize = 67, Count = HaloSize * HaloSize;
    readonly int[] heights, water, waterBodies;
    readonly byte[] materials, roads, environment, waterKinds;
    readonly PlacementCandidate[] placements;
    public ChunkCoordinate Coordinate
    {
        get;
    }
    public ReadOnlyMemory<int> Heights => heights;
    public ReadOnlyMemory<int> Water => water;
    public ReadOnlyMemory<int> WaterBodies => waterBodies;
    public ReadOnlyMemory<byte> Materials => materials;
    public ReadOnlyMemory<byte> Roads => roads;
    public ReadOnlyMemory<byte> Environment => environment;
    public ReadOnlyMemory<byte> WaterKinds => waterKinds;
    // Material R is the canonical graded-plot flag; the rendered R channel carries roads.
    public bool GradedPlot(int x, int z) => materials[((z + 1) * HaloSize + x + 1) * 4] != 0;
    public byte Road(int x, int z) => roads[(z + 1) * HaloSize + x + 1];
    public IReadOnlyList<PlacementCandidate> Placements
    {
        get;
    }
    public TerrainChunkData(ChunkCoordinate c, int[] heights, int[] water, byte[] materials, PlacementCandidate[] placements, int[]? waterBodies = null, byte[]? roads = null, byte[]? environment = null, byte[]? waterKinds = null)
    {
        if (!c.Valid || heights.Length != Count || water.Length != Count || materials.Length != Count * 4 || placements.Length > 512)
            throw new InvalidDataException("Invalid terrain chunk dimensions.");
        if (heights.Any(h => h is < -2048000 or > 2048000))
            throw new InvalidDataException("Invalid terrain height.");
        for (int i = 0; i < Count; i++)
            if (water[i] != int.MinValue && (water[i] < heights[i] || water[i] - heights[i] > 2048000))
                throw new InvalidDataException("Invalid water depth.");
        waterBodies ??= water.Select(w => w == int.MinValue ? 0 : 1).ToArray();
        if (waterBodies.Length != Count || waterBodies.Where((id, i) => id < 0 || (water[i] == int.MinValue ? id != 0 : id == 0)).Any())
            throw new InvalidDataException("Invalid water region.");
        roads ??= new byte[Count];
        if (roads.Length != Count)
            throw new InvalidDataException("Invalid road coverage.");
        this.roads = (byte[])roads.Clone();
        environment ??= new byte[Count * 9];
        waterKinds ??= water.Select(w => (byte)(w == int.MinValue ? WaterKind.None : WaterKind.River)).ToArray();
        if (environment.Length != Count * 9 || waterKinds.Length != Count || waterKinds.Any(k => k > (byte)WaterKind.Ocean))
            throw new InvalidDataException("Invalid terrain environment dimensions.");
        this.environment = (byte[])environment.Clone(); this.waterKinds = (byte[])waterKinds.Clone();
        this.waterBodies = (int[])waterBodies.Clone();
        this.heights = (int[])heights.Clone();
        this.water = (int[])water.Clone();
        this.materials = (byte[])materials.Clone();
        this.placements = (PlacementCandidate[])placements.Clone();
        Placements = Array.AsReadOnly(this.placements);
        Coordinate = c;
    }
    public int WaterBody(int x, int z) => waterBodies[(z + 1) * HaloSize + x + 1];
    public WaterKind WaterType(int x, int z) => (WaterKind)waterKinds[(z + 1) * HaloSize + x + 1];
    public float Drainage(int x,int z) => environment[((z+1)*HaloSize+x+1)*9+8]/255f;
    public LandscapeEnvironment Landscape(int x, int z)
    {
        int i = ((z + 1) * HaloSize + x + 1) * 9;
        return new(environment[i]/255f, environment[i+1]/255f, environment[i+2]/255f, environment[i+3]/255f*16,
            environment[i+4]/255f, (Geology)environment[i+5], (Habitat)environment[i+6], environment[i+7]/255f);
    }
    public float Height(int x, int z) => heights[(z + 1) * HaloSize + x + 1] / 1000f;
    public float WaterHeight(int x, int z)
    {
        int w = water[(z + 1) * HaloSize + x + 1];
        return w == int.MinValue ? float.NaN : w / 1000f;
    }
    public Vector3 Normal(int x, int z) => Vector3.Normalize(new(Height(x - 1, z) - Height(x + 1, z), 2, Height(x, z - 1) - Height(x, z + 1)));
    public Vector3 Point(int x, int z) => new(Coordinate.X * 64 + x, Height(x, z), Coordinate.Z * 64 + z);
    public Vector4 Material(int x, int z)
    {
        int i = ((z + 1) * HaloSize + x + 1) * 4;
        return new(roads[i / 4] / 255f, materials[i + 1] / 255f, materials[i + 2] / 255f, materials[i + 3] / 255f);
    }
    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangles()
    {
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                var a = Point(x, z);
                var b = Point(x + 1, z);
                var c = Point(x, z + 1);
                var d = Point(x + 1, z + 1);
                yield return (a, c, b);
                yield return (b, c, d);
            }
    }
    public float Sample(float x, float z)
    {
        float lx = Math.Clamp(x - Coordinate.X * 64, 0, 64), lz = Math.Clamp(z - Coordinate.Z * 64, 0, 64);
        int ix = Math.Min(63, (int)lx), iz = Math.Min(63, (int)lz);
        float u = lx - ix, v = lz - iz;
        float a = Height(ix, iz), b = Height(ix + 1, iz), c = Height(ix, iz + 1), d = Height(ix + 1, iz + 1);
        return u + v <= 1 ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1 - u) + (b - d) * (1 - v);
    }
}

public sealed record TerrainMetrics(int Pending, int Cached, long CacheBytes, double GenerationMilliseconds, int Colliders, long Revision, int CollisionCacheHits = 0, double CollisionCookMilliseconds = 0, double ChunkGenerationMilliseconds = 0);
