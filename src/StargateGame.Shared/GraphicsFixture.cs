using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace StargateGame.Shared;

/// <summary>Small deterministic developer worlds. Never materialized into universe saves.</summary>
public static class GraphicsFixture
{
    public const int Version = 9;
    // A medium village occupies the flat east side, clear of the gate, stairs
    // and water stations. Keep plot 1 at the original interior bookmark.
    static SettlementLayout SandboxVillage()
    {
        List<SettlementPlot> plots = [];
        List<SettlementRoad> roads = [];
        (float X, float Z, int Rotation)[] homes =
        [
            (25,25,0), (45,27,1), (66,24,11),
            (29,47,11), (50,49,0), (72,46,1),
            (23,69,1), (47,71,11), (68,68,0),
            (29,91,0), (52,93,1), (75,90,11)
        ];
        // Sample shallow quadratic curves into the shared road representation,
        // so paving and vegetation use exactly the same curved footprint.
        SettlementRoad[] Curve(Vector3 a, Vector3 b, float bend, float width)
        {
            var delta = b-a;
            float length = delta.Length();
            if (length < .01f) return [];
            var control = (a+b)*.5f + new Vector3(-delta.Z,0,delta.X)/length*bend;
            int count = Math.Clamp((int)MathF.Ceiling(length/3), 3, 20);
            List<SettlementRoad> segments = [];
            var last = a;
            for (int i=1;i<=count;i++)
            {
                float t=(float)i/count, u=1-t;
                var point=u*u*a+2*u*t*control+t*t*b;
                segments.Add(new(last,point,width*.7f,true));
                last=point;
            }
            return segments.ToArray();
        }
        Vector3 previous = new(13,0,10);
        for (int row = 0; row < 4; row++)
        {
            float z = 16 + row * 22;
            Vector3 a = new(14 + row % 2 * 3,0,z), b = new(72,0,z+1);
            roads.AddRange(Curve(previous,a,row % 2 == 0 ? 2 : -2,4)); previous=a;
            // Only two through-lanes; alternate rows have short dead-end spurs.
            if (row % 2 != 0) b = new(34,0,z+1);
            var lanes = Curve(a,b,row % 2 == 0 ? -4 : 2.5f,3.5f);
            roads.AddRange(lanes);
            for (int column = 0; column < 3; column++)
            {
                int index = row * 3 + column, id = index + 1;
                var h = homes[index];
                string model = $"settlement-mega-house-{index % 4 + 1}";
                Vector3 position = new(h.X,0,h.Z);
                plots.Add(new(id,1,model,position,h.Rotation));
                // Four houses keep grass approaches instead of adding a path
                // to every door and recreating a dense street grid.
                if (row % 2 != 0 && column > 0) continue;
                var rotation = Matrix4x4.CreateRotationY(-h.Rotation * MathF.PI / 6);
                var entrance = SettlementTemplates.Get(model).Entrances[0];
                var outside = Vector3.Transform(new Vector3(entrance[0],0,entrance[2]),rotation)+position;
                var outward = Vector3.TransformNormal(-Vector3.UnitZ,rotation);
                var threshold = outside - outward * .8f;
                var porch = outside + outward * 1.2f;
                Vector3 nearest = default; float best = float.MaxValue;
                foreach (var lane in lanes)
                {
                    var direction = lane.B-lane.A;
                    var point = lane.A+direction*Math.Clamp(Vector3.Dot(porch-lane.A,direction)/direction.LengthSquared(),0,1);
                    float distance = Vector3.DistanceSquared(porch,point);
                    if(distance<best){best=distance;nearest=point;}
                }
                roads.AddRange(Curve(threshold,porch,0,1.6f));
                roads.AddRange(Curve(porch,nearest,column % 2 == 0 ? .8f : -.8f,1.6f));
            }
        }
        return new(SettlementSettings.Version, "village", "mega",
            [new(1, new(48, 0, 59), "village", 12, 12)], plots.ToArray(), roads.ToArray(), []);
    }
    public static bool Forest(PlanetDefinition d) => d.Fixture == "gate-pair" && d.Preset == "temperate";
    public static float ForestPathX(float z) => 30 + (-z - 20) * .25f + 3 * MathF.Sin((-z - 20) * .065f);
    public static float ForestPathDistance(float x, float z) => z is <= -17 and >= -86 ? Math.Abs(Math.Abs(x) - ForestPathX(z)) : float.PositiveInfinity;
    static readonly PlacementCandidate[] forestTrees = BuildForest();
    static PlacementCandidate[] BuildForest()
    {
        static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352d;
            x ^= x >> 15;
            x *= 0x846ca68b;
            return x ^ (x >> 16);
        }
        static float Unit(uint x) => (Hash(x) & 65535) / 65535f;
        List<(uint Rank, Vector3 P, float Yaw, ulong Id)> candidates = [];
        for (int z = -85; z <= 62; z += 9)
            for (int x = -85; x <= 85; x += 9)
            {
                uint seed = Hash(unchecked((uint)x * 73856093u ^ (uint)z * 19349663u) ^ 7319u);
                var p = new Vector3(x + (Unit(seed + 1) - .5f) * 2, 0, z + (Unit(seed + 2) - .5f) * 2);
                // Village footprints plus crown clearance; retain the forest
                // stations to the north and west.
                if (p.X > 0 && p.X < 88 && p.Z > 4 && p.Z < 124)
                    continue;
                // Twenty-one metres of crown clearance for the enlarged world trees.
                if (Math.Abs(p.X) < 45 && p.Z > -38 && p.Z < 44)
                    continue;
                if (Math.Abs(p.X) < 36 && p.Z > -34)
                    continue;
                if (ForestPathDistance(p.X, p.Z) < 3.3f)
                    continue;
                if (Math.Abs(p.X) > 83 + 3 * MathF.Sin(p.Z * .07f) || p.Z < -83 + 3 * MathF.Sin(p.X * .08f))
                    continue;
                ulong id = ((ulong)Hash(seed + 5) << 32) | Hash(seed + 6);
                candidates.Add((Hash(seed + 7), p, Unit(seed + 8) * MathF.Tau, id));
            }
        return candidates.OrderBy(t => t.Rank).Take(140).Select((t, i) =>
            new PlacementCandidate((t.Id & ~0x300UL) | ((ulong)(i % 4) << 8), "tree", t.P, t.Yaw, 1)).ToArray();
    }
    public static bool IsFixture(PlanetDefinition d) => d.Fixture is "graphics-lab" or "gate-pair";
    public static bool Contains(ChunkCoordinate c) => c.X is >= -2 and < 2 && c.Z is >= -2 and < 2;
    public static ChunkCoordinate[] Chunks => ChunkCoordinate.All.Where(Contains).ToArray();
    static Guid Id(string text) => new(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 16));
    public static Guid EntityId(PlanetDefinition world, string entity) => Id(world.LocationId + ":" + entity);
    public static PlanetDefinition Create(string scene, string preset = "temperate")
    {
        if (scene is not ("graphics-lab" or "gate-pair") || !PlanetSettings.Presets.Contains(preset))
            throw new ArgumentException("Unknown graphics fixture.");
        string key = $"fixture:{Version}:{scene}:{preset}";
        List<StaticEntity> entities = [];
        void Gate(string suffix, Vector3 p, float yaw)
        {
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
            entities.Add(new("gate-" + suffix, "stargate", p - Vector3.UnitY * PlanetSettings.GateEmbedDepth)
            {
                Scale = PlanetSettings.GateScale,
                Rotation = rotation
            });
            entities.Add(new("dhd-" + suffix, "dhd", p + Vector3.Transform(new Vector3(4, 0, 6), rotation))
            {
                Rotation = rotation
            });
        }
        if (scene == "gate-pair")
        {
            Gate("a", new(-6, 0, 0), 0);
            Gate("b", new(6, 0, 0), 0);
        }
        else
            Gate("a", new(-35, 0, -35), 0);
        return new("fixture-" + scene + "-" + preset, scene + " / " + preset, 7319, 100,
            Enumerable.Range(0, 8).Select(i => scene == "gate-pair" ? new Vector3(-3 + i % 4 * 2, 0, 9 + i / 4 * 2) : new Vector3(i % 4 * 2, 0, 8 + i / 4 * 2)).ToArray(), entities.ToArray())
        {
            Fixture = scene,
            Settlements = scene == "gate-pair" ? SandboxVillage() : null,
            GeneratorVersion = PlanetSettings.Version,
            Preset = preset,
            UniverseId = Id("graphics-fixtures"),
            LocationId = Id(key),
            SettingsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
            Props = new PropDefinition[] { new(1, scene == "gate-pair" ? new(-9, .52f, 7) : new(8, .52f, 3), new(.5f), 10), PropDefinition.Barrel(2, scene == "gate-pair" ? new(-7, .612f, 8) : new(10, .612f, 3), false) }.Select(p => p with { PersistentId = Id(key + ":prop:" + p.Id) }).ToArray()
        };
    }
    public static void Validate(PlanetDefinition d)
    {
        if (!IsFixture(d) || !PlanetSettings.Presets.Contains(d.Preset))
            throw new InvalidDataException("Unknown fixture.");
        var expected = Create(d.Fixture!, d.Preset);
        // Layout is immutable. Props and developer time/weather are live state,
        // including in the definition accompanying a door/building transfer.
        d.Environment?.Validate();
        if (System.Text.Json.JsonSerializer.Serialize(d with
        {
            Props = expected.Props,
            Environment = expected.Environment
        }, Protocol.Json) != System.Text.Json.JsonSerializer.Serialize(expected, Protocol.Json))
            throw new InvalidDataException("Fixture definition does not match this build.");
    }
    static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
    public static float Height(PlanetDefinition d, float x, float z)
    {
        if (d.Fixture == "gate-pair")
            return x is > -12 and < 12 && z is > 55 and < 78 ? -1.5f * Smooth(55, 60, z) * (1 - Smooth(73, 78, z)) * (1 - Smooth(8, 12, Math.Abs(x))) : 0;
        float hill = 8 * Smooth(25, 60, x) * Smooth(10, 40, z);
        float river = Math.Abs(z - 45 - 3 * MathF.Sin(x * .05f));
        float channel = river < 7 ? -1.4f * (1 - Smooth(3, 7, river)) : 0;
        return hill + channel;
    }
    public static TerrainChunkData BuildChunk(PlanetDefinition d, ChunkCoordinate c)
    {
        if (!Contains(c))
            throw new ArgumentOutOfRangeException(nameof(c));
        int[] h = new int[TerrainChunkData.Count], w = Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray();
        byte[] m = new byte[h.Length * 4];
        for (int z = -1; z <= 65; z++)
            for (int x = -1; x <= 65; x++)
            {
                int i = (z + 1) * 67 + x + 1;
                float wx = c.X * 64 + x, wz = c.Z * 64 + z, height = Height(d, wx, wz);
                h[i] = (int)MathF.Round(height * 1000);
                if (height < -.35f)
                    w[i] = -350;
                m[i * 4] = d.Fixture == "gate-pair" ? (byte)0 : (byte)180;
                if (d.Settlements is { } village && wx >= 9 && wx <= 86 && wz >= 8 && wz <= 100)
                {
                    float road = 0;
                    var point = new Vector2(wx, wz);
                    foreach (var lane in village.Roads)
                    {
                        var a = new Vector2(lane.A.X, lane.A.Z);
                        var delta = new Vector2(lane.B.X, lane.B.Z) - a;
                        float t = Math.Clamp(Vector2.Dot(point - a, delta) / Math.Max(delta.LengthSquared(), .001f), 0, 1);
                        float distance = Vector2.Distance(point, a + delta * t);
                        road = Math.Max(road, 1 - Smooth(lane.Width * .5f, lane.Width * .5f + .7f, distance));
                    }
                    m[i * 4] = (byte)MathF.Round(road * 255);
                }
                m[i * 4 + 1] = 45;
                m[i * 4 + 2] = 25;
                m[i * 4 + 3] = 5;
            }
        List<PlacementCandidate> trees = [];
        if (Forest(d))
            trees.AddRange(forestTrees.Where(t => ChunkCoordinate.At(t.Position.X, t.Position.Z) == c));
        if (d.Fixture == "graphics-lab")
            for (int z = -78; z <= -52; z += 9)
                for (int x = 35; x <= 70; x += 9)
                    if (ChunkCoordinate.At(x, z) == c)
                        trees.Add(new((ulong)(10000 + ((z + 78) / 9 * 4 + (x - 35) / 9) * 257), "tree", new(x, Height(d, x, z), z), x * .17f % MathF.Tau, 1));
        return new(c, h, w, m, trees.ToArray());
    }
}
