using System.Numerics;

namespace StargateGame.Shared;

public sealed record StaticEntity(string Id, string Model, Vector3 Position)
{
    public float Scale
    {
        get;
        init;
    } = 1;
    public Quaternion Rotation
    {
        get;
        init;
    } = Quaternion.Identity;
    [System.Text.Json.Serialization.JsonIgnore] public Matrix4x4 Transform => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}
public enum PropKind
{
    WoodenCrate, RedBarrel, BlackBarrel, Lantern
}
public enum GroundSurface
{
    Unknown, Grass, Stone, Mud, Gravel, Wood, Metal, Snow, Sand, Water
}
public sealed record PropDefinition(int Id, Vector3 Position, Vector3 HalfExtent, float Mass)
{
    public Guid PersistentId
    {
        get;
        init;
    }
    public PropKind Kind
    {
        get;
        init;
    }
    [System.Text.Json.Serialization.JsonIgnore] public bool IsBarrel => Kind is PropKind.RedBarrel or PropKind.BlackBarrel;
    [System.Text.Json.Serialization.JsonIgnore] public string Model => Kind switch { PropKind.Lantern => "wooden-lantern", PropKind.RedBarrel => "barrel-red", PropKind.BlackBarrel => "barrel-black", _ => "wooden-crate" };
    [System.Text.Json.Serialization.JsonIgnore] public GroundSurface Surface => IsBarrel ? GroundSurface.Metal : GroundSurface.Wood;
    [System.Text.Json.Serialization.JsonIgnore] public bool Valid => Enum.IsDefined(Kind) && HalfExtent == (Kind == PropKind.Lantern ? LanternAsset.HalfExtent : IsBarrel ? new Vector3(.363f, .592f, .363f) : new Vector3(.5f)) && Mass == (Kind == PropKind.Lantern ? 2 : IsBarrel ? 15 : 10);
    [System.Text.Json.Serialization.JsonIgnore] public bool CastsShadows => Kind != PropKind.Lantern;
    [System.Text.Json.Serialization.JsonIgnore] public string Name => Kind == PropKind.Lantern ? "lantern" : IsBarrel ? "barrel" : "crate";
    public static PropDefinition Lantern(int id, Vector3 position) => new(id, position, LanternAsset.HalfExtent, 2) { Kind = PropKind.Lantern };
    public static PropDefinition Barrel(int id, Vector3 position, bool black) => new(id, position, new(.363f, .592f, .363f), 15) { Kind = black ? PropKind.BlackBarrel : PropKind.RedBarrel };
}
public sealed record PlanetDefinition(string Id, string Name, int Seed, float HalfSize, Vector3[] Spawns, StaticEntity[] Entities)
{
    public PlanetGenerationSettings Generation { get; init; } = new();
    public SettlementLayout? Settlements
    {
        get;
        init;
    }
    public SettlementSettings SettlementGeneration
    {
        get;
        init;
    } = new();
    public PlanetEnvironmentDefinition? Environment
    {
        get;
        init;
    }
    public string? Fixture
    {
        get;
        init;
    }
    public Guid UniverseId
    {
        get;
        init;
    }
    public Guid LocationId
    {
        get;
        init;
    }
    public string Preset
    {
        get;
        init;
    } = "legacy";
    public int GeneratorVersion
    {
        get;
        init;
    }
    public string SettingsHash
    {
        get;
        init;
    } = "";
    public PropDefinition[] Props
    {
        get;
        init;
    } = [
        new(1, new(-5,.52f,8), new(.5f), 10), new(2, new(-5,1.54f,8), new(.5f), 10),
        new(3, new(-7,.52f,10), new(.5f), 10), new(4, new(-7,.52f,12), new(.5f), 10)];
    public static PlanetDefinition LegacyRuntime => Default with { Props = Default.Props.Select(p => p with { PersistentId = GateTravel.PropIdentity(Guid.Empty, p.Id) }).ToArray() };
    public static PlanetDefinition Default
    {
        get;
    } = new("p3x-001", "P3X-001 / First landing", 7319, 128,
        Enumerable.Range(0, 8).Select(i => new Vector3(new[] { 0, -3, 3, -6 }[i % 4], 0, 14 + i / 4 * 3)).ToArray(),
        [new("gate-01", "stargate", new Vector3(0, -PlanetSettings.GateEmbedDepth, 0)) { Scale = PlanetSettings.GateScale }, new("dhd-01", "dhd", new Vector3(4, 0, 6))]);
}

public sealed class PlanetWorld(PlanetDefinition definition, ITerrainSource? terrain = null)
{
    public PlanetDefinition Definition
    {
        get;
        private set;
    } = definition;
    public void SetEnvironment(PlanetEnvironmentDefinition environment) => Definition = Definition with { Environment = environment };
    public void SetProps(PropDefinition[] props) => Definition = Definition with { Props = props };
    public const float CellSize = 2;
    // The rendered heightfield and physics share these exact triangles, including the diagonal.
    public float SampleHeight(float x, float z)
    {
        if (terrain is not null)
            return terrain.SampleHeight(x, z);
        float gx = MathF.Floor(x / CellSize) * CellSize, gz = MathF.Floor(z / CellSize) * CellSize;
        float u = (x - gx) / CellSize, v = (z - gz) / CellSize;
        float a = VertexHeight(gx, gz), b = VertexHeight(gx + CellSize, gz);
        float c = VertexHeight(gx, gz + CellSize), d = VertexHeight(gx + CellSize, gz + CellSize);
        return u + v <= 1 ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1 - u) + (b - d) * (1 - v);
    }

    public float VertexHeight(float x, float z)
    {
        if (terrain is not null)
            return terrain.SampleHeight(x, z);
        float distance = MathF.Sqrt(x * x + z * z);
        float blend = Math.Clamp((distance - 18) / 22, 0, 1);
        blend = blend * blend * (3 - 2 * blend);
        float phase = (Definition.Seed % 997) * .01f;
        float gateApproach = .95f * Math.Clamp((6 - MathF.Abs(x)) / 2, 0, 1) * Math.Clamp((6 - MathF.Abs(z)) / 4, 0, 1);
        return gateApproach + blend * (3 + 3 * MathF.Sin(x * .035f + phase) * MathF.Cos(z * .027f)
            + 1.5f * MathF.Sin((x + z) * .065f + phase));
    }

    public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangles()
    {
        float half = Definition.HalfSize;
        for (float z = -half; z < half; z += CellSize)
            for (float x = -half; x < half; x += CellSize)
            {
                Vector3 a = new(x, VertexHeight(x, z), z), b = new(x + CellSize, VertexHeight(x + CellSize, z), z);
                Vector3 c = new(x, VertexHeight(x, z + CellSize), z + CellSize), d = new(x + CellSize, VertexHeight(x + CellSize, z + CellSize), z + CellSize);
                yield return (a, c, b);
                yield return (b, c, d);
            }
    }

    public Vector3 Spawn(int index)
    {
        Vector3 p = Definition.Spawns[index % Definition.Spawns.Length];
        return p with
        {
            Y = SampleHeight(p.X, p.Z)
        };
    }
}
