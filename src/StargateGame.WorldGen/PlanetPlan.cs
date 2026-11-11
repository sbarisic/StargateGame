using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

/// <summary>A bounded drainage plan shared by all detailed chunks of one planet.</summary>
public sealed class PlanetPlan
{
    public const int Grid = RegionalLandscape.Grid, Step = RegionalLandscape.Step;
    public RegionalLandscape Landscape { get; }
    readonly CancellationToken cancellation;
    readonly float[] elevation = new float[Grid * Grid];
    readonly int[] lakeIds = new int[Grid * Grid], riverIds = new int[Grid * Grid];
    readonly float[] original = new float[Grid * Grid], lakeLevel = new float[Grid * Grid];
    readonly int[] downstream = new int[Grid * Grid], accumulation = new int[Grid * Grid];
    readonly bool[] channels = new bool[Grid * Grid];
    readonly float[] headDistance = new float[Grid * Grid];
    readonly float[] riverElevation = new float[Grid * Grid];
    public double OriginalChannelLength
    {
        get;
        private set;
    }
    public double ChannelLength => Enumerable.Range(0, channels.Length).Where(i => channels[i]).Sum(i => (double)ReachLength(i));
    public ReadOnlyMemory<bool> Channels => channels;
    readonly PlanetGenerationSettings settings;
    readonly FastNoiseLite noise, detail, warp, forest;
    public int Seed
    {
        get;
    }
    public int Version
    {
        get;
    }
    public string Preset
    {
        get;
    }
    public Vector3 Gate
    {
        get;
        private set;
    }
    public SettlementPlan Settlements
    {
        get;
        private set;
    } = null!;
    public PlanetDefinition Definition
    {
        get;
        private set;
    } = null!;
    public ReadOnlyMemory<int> Downstream => downstream;
    public ReadOnlyMemory<int> FlowAccumulation => accumulation;
    public ReadOnlyMemory<float> DrainageHeight => elevation;
    public PlanetPlan(int seed, string preset, int version = PlanetSettings.Version, SettlementSettings? settlements = null, PlanetGenerationSettings? generation = null, CancellationToken cancellation = default, Action<string>? progress = null, string? cacheDirectory = null)
    {
        if (!PlanetSettings.Presets.Contains(preset))
            throw new ArgumentException("Unknown planet preset.");
        if (!PlanetSettings.Supports(version))
            throw new ArgumentException("Unsupported generator version.");
        settings = generation ?? new();
        settings.Validate();
        this.cancellation = cancellation;
        Version = version;
        Seed = seed;
        Preset = preset;
        noise = new(seed);
        noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
        noise.SetFrequency(.005f);
        noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        noise.SetFractalOctaves(4);
        warp = new(unchecked(seed + 53887));
        warp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
        warp.SetDomainWarpAmp(18);
        warp.SetFrequency(.009f);
        detail = new(unchecked(seed + 120937));
        detail.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
        detail.SetFrequency(.055f);
        forest = new(unchecked(seed + 847219));
        forest.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2S);
        forest.SetFrequency(.006f);
        forest.SetFractalType(FastNoiseLite.FractalType.FBm);
        forest.SetFractalOctaves(2);
        Landscape = new(seed, preset, settings, cancellation, progress, cacheDirectory);
        Array.Copy(Landscape.Heights, elevation, elevation.Length);
        Array.Copy(elevation, original, elevation.Length);
        Array.Fill(lakeLevel, float.NaN);
        Drainage();
        FindLakes();
        SelectChannels();
        progress?.Invoke("Habitats");
        ChooseGate();
        ExcludeGateLakes();
        BuildRiverProfile();
        progress?.Invoke("Settlements");
        Settlements = new SettlementPlan(seed, preset, Gate, UnsettledSample, settlements ?? new());
        Vector3 centre = Gate;
        Definition = new($"planet-{preset}-{seed}", $"{char.ToUpperInvariant(preset[0]) + preset[1..]} / {seed}", seed, WorldDomain.HalfSize,
            Enumerable.Range(0, 8).Select(i => centre + new Vector3((i % 4 - 1.5f) * 2, 0, 4 + (i / 4) * 2)).ToArray(),
            [new("gate-01", "stargate", centre + new Vector3(0, -PlanetSettings.GateEmbedDepth, -5)) { Scale = PlanetSettings.GateScale }, new("dhd-01", "dhd", centre + new Vector3(4, 0, 1))])
        {
            Generation = settings,
            SettlementGeneration = settlements ?? new(),
            Settlements = Settlements.Layout,
            GeneratorVersion = Version,
            Preset = preset,
            SettingsHash = PlanetSettings.Hash(seed, preset, Version, settlements, settings),
            Props = [new(1,centre+new Vector3(-5,.52f,2),new(.5f),10),new(2,centre+new Vector3(-5,1.54f,2),new(.5f),10),
                PropDefinition.Barrel(3,centre+new Vector3(-7,.612f,4),false),PropDefinition.Barrel(4,centre+new Vector3(-7,.612f,6),true)]
        };
    }
    static float Smooth(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * t * (t * (t * 6 - 15) + 10);
    }
    void Drainage()
    {
        // Use the same routes for erosion, habitat moisture and rendered rivers.
        // Keep the filled routing surface separately for basin spill levels.
        var order = RegionalHydrology.Route(original, Grid, Step, downstream, accumulation,
            cancellation, elevation);
        foreach (int i in order)
            riverIds[i] = downstream[i] < 0 ? Grid * Grid + 2 + i : riverIds[downstream[i]];
        for (int i = 0; i < channels.Length; i++)
            channels[i] = accumulation[i] >= 160 && downstream[i] >= 0;
        OriginalChannelLength = ChannelLength;
    }
    float ReachLength(int i) => downstream[i] < 0 ? 0 : Step * MathF.Sqrt(MathF.Pow(i % Grid - downstream[i] % Grid, 2) + MathF.Pow(i / Grid - downstream[i] / Grid, 2));
    void SelectChannels()
    {
        int threshold = Preset switch
        {
            "icy" => 1360,
            "desert" => 1600,
            _ => 1280
        };
        for (int i = 0; i < channels.Length; i++)
            channels[i] = accumulation[i] >= threshold && downstream[i] >= 0;
        // Preserve flowing lake outlets. Closed lakes below their spill level do not
        // invent new rivers: only basins fed by a selected catchment require an outlet.
        bool[] protectedReach = new bool[channels.Length];
        foreach (int i in Enumerable.Range(0, channels.Length).Where(i => accumulation[i] >= threshold && float.IsFinite(lakeLevel[i]) && downstream[i] >= 0 && !float.IsFinite(lakeLevel[downstream[i]])))
            for (int j = i; j >= 0 && downstream[j] >= 0 && !protectedReach[j]; j = downstream[j])
            {
                channels[j] = true;
                protectedReach[j] = true;
            }
        int[] incoming = new int[channels.Length];
        for (int i = 0; i < channels.Length; i++)
            if (channels[i])
                incoming[downstream[i]]++;
        foreach (int head in Enumerable.Range(0, channels.Length).Where(i => channels[i] && incoming[i] == 0).ToArray())
        {
            List<int> branch = [];
            float length = 0;
            int j = head;
            while (j >= 0 && channels[j] && (j == head || incoming[j] == 1))
            {
                branch.Add(j);
                length += ReachLength(j);
                j = downstream[j];
            }
            if (length < 48 && !branch.Any(i => protectedReach[i]))
                foreach (int i in branch)
                    channels[i] = false;
        }
        foreach (int i in Enumerable.Range(0, channels.Length).Where(i => channels[i]).OrderBy(i => accumulation[i]))
            if (downstream[i] >= 0)
                headDistance[downstream[i]] = Math.Max(headDistance[downstream[i]], headDistance[i] + ReachLength(i));
    }
    void FindLakes()
    {
        if (Preset == "desert")
            return;
        bool[] visited = new bool[original.Length];
        for (int start = 0; start < original.Length; start++)
        {
            if ((start & 4095) == 0) cancellation.ThrowIfCancellationRequested();
            if (Landscape.Ocean[start] != 0 || visited[start] || elevation[start] - original[start] < .25f)
                continue;
            List<int> basin = [];
            float spill = elevation[start];
            Queue<int> pending = new();
            pending.Enqueue(start);
            visited[start] = true;
            while (pending.TryDequeue(out int i))
            {
                basin.Add(i);
                int x = i % Grid, z = i / Grid;
                foreach (var (dx, dz) in BasinNeighbors)
                {
                    int nx = x + dx, nz = z + dz;
                    if (nx <= 0 || nz <= 0 || nx >= Grid - 1 || nz >= Grid - 1)
                        continue;
                    int j = nz * Grid + nx;
                    // Include shallow margins, but do not merge neighbouring
                    // depressions whose outlets are at different elevations.
                    if (!visited[j] && Landscape.Ocean[j] == 0 &&
                        elevation[j] - original[j] > .01f && Math.Abs(elevation[j] - spill) < .02f)
                    {
                        visited[j] = true;
                        pending.Enqueue(j);
                    }
                }
            }
            if (basin.Count < 16 || basin.Max(i => spill - original[i]) < .5f)
                continue;
            float level = basin.Min(i => elevation[i]) - .05f;
            foreach (int i in basin)
                if (Landscape.Ocean[i] == 0 && original[i] < level)
                {
                    lakeLevel[i] = level;
                    lakeIds[i] = start + 2;
                }
        }
    }
    void BuildRiverProfile()
    {
        // Priority-flood heights select routes, but filled drainage heights must
        // not become raised riverbeds. Incise the route into the original ground,
        // retaining actual lake surfaces and a gentle downhill grade at outlets.
        for (int i = 0; i < riverElevation.Length; i++)
            riverElevation[i] = float.IsFinite(lakeLevel[i]) ? lakeLevel[i] : original[i] - .08f;
        foreach (int i in Enumerable.Range(0, channels.Length).Where(i => channels[i]).OrderBy(i => accumulation[i]))
        {
            int p = downstream[i];
            if (p >= 0 && !float.IsFinite(lakeLevel[p]))
                riverElevation[p] = Math.Min(riverElevation[p], riverElevation[i] - ReachLength(i) * .002f);
        }
    }
    void ExcludeGateLakes()
    {
        HashSet<int> rejected = [];
        for (int z = 0; z < Grid; z++)
            for (int x = 0; x < Grid; x++)
                if (float.IsFinite(lakeLevel[z * Grid + x]) && Vector2.Distance(new(x * Step - WorldDomain.HalfSize, z * Step - WorldDomain.HalfSize), new(Gate.X, Gate.Z + 8)) < 45)
                    rejected.Add(lakeIds[z * Grid + x]);
        for (int i = 0; i < lakeLevel.Length; i++)
            if (rejected.Contains(lakeIds[i]))
                lakeLevel[i] = float.NaN;
    }
    (float Level, int Id) Lake(float x, float z)
    {
        int gx = Math.Clamp((int)MathF.Floor((x + WorldDomain.HalfSize) / Step), 0, Grid - 2), gz = Math.Clamp((int)MathF.Floor((z + WorldDomain.HalfSize) / Step), 0, Grid - 2);
        float level = float.PositiveInfinity;
        int id = 0;
        // Evaluate the containing terrain cell, not a nearest-cell square mask.
        // This lets the shoreline intersect the same interpolated basin bed.
        for (int dz = 0; dz <= 1; dz++)
            for (int dx = 0; dx <= 1; dx++)
            {
                int i = (gz + dz) * Grid + gx + dx;
                if (float.IsFinite(lakeLevel[i]) && lakeLevel[i] < level)
                {
                    level = lakeLevel[i];
                    id = lakeIds[i];
                }
            }
        return (id == 0 ? float.NaN : level, id);
    }
    float Regional(float x, float z, bool raw = false)
    {
        float gx = Math.Clamp((x + WorldDomain.HalfSize) / Step, 0, Grid - 1), gz = Math.Clamp((z + WorldDomain.HalfSize) / Step, 0, Grid - 1);
        int ix = Math.Min(Grid - 2, (int)gx), iz = Math.Min(Grid - 2, (int)gz);
        float u = gx - ix, v = gz - iz;
        var field = raw ? original : elevation;
        float a = field[iz * Grid + ix], b = field[iz * Grid + ix + 1], c = field[(iz + 1) * Grid + ix], d = field[(iz + 1) * Grid + ix + 1];
        return float.Lerp(float.Lerp(a, b, u), float.Lerp(c, d, u), v);
    }
    (float Distance, float Height, float Width, int Id, float Taper) River(float x, float z)
    {
        int gx = (int)Math.Floor((x + WorldDomain.HalfSize) / Step), gz = (int)Math.Floor((z + WorldDomain.HalfSize) / Step);
        float best = float.PositiveInfinity, height = 0, width = 0, taper = 0, weightSum = 0, weightedHeight = 0;
        int id = 0;
        for (int dz = -3; dz <= 3; dz++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int nx = gx + dx, nz = gz + dz;
                if (nx < 0 || nz < 0 || nx >= Grid || nz >= Grid)
                    continue;
                int i = nz * Grid + nx;
                if (!channels[i])
                    continue;
                int p = downstream[i];
                var a = new Vector2(nx * Step - WorldDomain.HalfSize, nz * Step - WorldDomain.HalfSize);
                var b = new Vector2((p % Grid) * Step - WorldDomain.HalfSize, (p / Grid) * Step - WorldDomain.HalfSize);
                float t = Math.Clamp(Vector2.Dot(new Vector2(x, z) - a, b - a) / Vector2.DistanceSquared(a, b), 0, 1);
                float dist = Vector2.Distance(new(x, z), Vector2.Lerp(a, b, t));
                if (dist < best)
                {
                    id = riverIds[i];
                    best = dist;
                    height = riverElevation[i] + (riverElevation[p] - riverElevation[i]) * t;
                    taper = Smooth((headDistance[i] + t * ReachLength(i)) / 24);
                    width = Math.Clamp(MathF.Sqrt(accumulation[i]) * .075f, 1.1f, 4) * taper;
                }
                if (dist < 8)
                {
                    // Compact, continuous overlap removes the nearest-reach height
                    // jumps at bends/confluences. The 12 m search includes every
                    // four-metre segment with support inside this eight-metre kernel.
                    float kernel = 1 - dist * dist / 64, weight = kernel * kernel * kernel / (1 + dist * dist);
                    weightedHeight += (riverElevation[i] + (riverElevation[p] - riverElevation[i]) * t) * weight;
                    weightSum += weight;
                }
            }
        if (weightSum > 0)
            height = weightedHeight / weightSum;
        return (best, height, width, id, taper);
    }
    void ChooseGate()
    {
        float best = float.PositiveInfinity;
        Vector3 chosen = new(32, Regional(32, 24), 24);
        for (int z = -1200; z <= 1200; z += 32)
            for (int x = -1200; x <= 1200; x += 32)
            {
                if (Landscape.OceanAt(x,z) || Landscape.Height(x,z) < Landscape.SeaLevel + 5 || float.IsFinite(Lake(x,z).Level) || River(x, z).Distance < 20)
                    continue;
                float lo = float.MaxValue, hi = float.MinValue;
                List<float> samples = [];
                for (int dz = -12; dz <= 12; dz += 6)
                    for (int dx = -12; dx <= 12; dx += 6)
                    {
                        float h = Regional(x + dx, z + dz);
                        lo = Math.Min(lo, h);
                        hi = Math.Max(hi, h);
                        samples.Add(h);
                    }
                float score = (hi - lo) * 8 + MathF.Sqrt(x * x + z * z) * .003f;
                if (score >= best)
                    continue;
                best = score;
                samples.Sort();
                chosen = new(x, samples[samples.Count / 2], z);
            }
        Gate = chosen with
        {
            Y = MathF.Round(chosen.Y * 1000) / 1000
        };
    }
    static readonly (int X, int Z)[] ThermalNeighbors = [(-1, 0), (1, 0), (0, -1), (0, 1)];
    static readonly (int X, int Z)[] BasinNeighbors = [(-1,-1),(0,-1),(1,-1),(-1,0),(1,0),(-1,1),(0,1),(1,1)];
    float RawHeight(int x, int z)
    {
        float wx = x, wz = z;
        warp.DomainWarp(ref wx, ref wz);
        float n = detail.GetNoise(wx, wz), ridge = 1 - Math.Abs(n);
        float slope = Math.Abs(Regional(x + 2, z) - Regional(x - 2, z)) + Math.Abs(Regional(x, z + 2) - Regional(x, z - 2));
        return Regional(x, z, true) + (n * .65f + (ridge - .6f) * .35f) * Math.Clamp(slope * .3f, .08f, 1.4f);
    }
    /// <summary>Ground before channels, lakes and the gate clearing are applied.</summary>
    public float NaturalHeight(int x, int z)
    {
        float raw = RawHeight(x, z), h = raw;
        // One bounded thermal relaxation pass with a 35-degree talus. Each
        // output uses the same world-space neighbor stencil, including halos.
        const float talus = .7002f;
        float flow = 0;
        foreach (var (dx, dz) in ThermalNeighbors)
        {
            float delta = RawHeight(x + dx, z + dz) - raw;
            flow += MathF.CopySign(Math.Max(0, Math.Abs(delta) - talus), delta);
        }
        h += Math.Clamp(flow * .125f, -.35f, .35f);
        return h;
    }
    public (int Height, int Water) Sample(int x, int z)
    {
        var s = UnsettledSample(x, z);
        if (s.Water != int.MinValue)
            return s;
        return ((int)MathF.Round(Settlements.Height(x, z, s.Height / 1000f) * 1000), s.Water);
    }
    public (int Height, int Water) UnsettledSample(int x, int z)
    {
        cancellation.ThrowIfCancellationRequested();
        float h = NaturalHeight(x, z);
        var r = River(x, z);
        float water = float.NaN;
        if (r.Distance < r.Width + 4)
        {
            float bank = Smooth((r.Distance - r.Width) / 4);
            float cross = Math.Clamp(r.Distance / Math.Max(.01f, r.Width), 0, 1);
            float bed = r.Height - (.25f + .55f * MathF.Sqrt(1 - cross * cross)) * r.Taper;
            float carved = h + (bed + (h - bed) * bank - h) * r.Taper;
            h = Math.Min(h, carved);
            if (Preset != "desert" && h < r.Height && r.Distance < r.Width + .75f)
                water = Math.Clamp(r.Height + settings.WaterOffset, r.Height - 3, r.Height + 3);
        }
        var lake = Lake(x, z);
        if (float.IsFinite(lake.Level))
        {
            float level = lake.Level;
            // Preserve detailed shoreline relief rather than replacing the
            // whole bed with the coarse regional triangles at the waterline.
            if (h < level)
            {
                h = Math.Max(level - 12, h);
                // Keep water inside the existing basin; never flood across its spill level.
                water = Math.Min(level, level + settings.WaterOffset);
            }
        }
        if (Landscape.OceanAt(x,z) && h < Landscape.SeaLevel) water = Landscape.SeaLevel;
        if (settings.WaterOffset != 0 && float.IsFinite(water) && water <= h) water = float.NaN;
        float distance = Vector2.Distance(new(x, z), new(Gate.X, Gate.Z));
        float blend = Smooth((distance - 11) / 8);
        h = Gate.Y + (h - Gate.Y) * blend;
        if (distance < 22)
            water = float.NaN;
        // Reserve a gentle southern approach out of the pad without an annular rim.
        float ax = Math.Abs(x - Gate.X), az = z - Gate.Z;
        if (ax < 4 && az > 10 && az < 32)
        {
            float target = Gate.Y + (Regional(Gate.X, Gate.Z + 32) - Gate.Y) * Smooth((az - 10) / 22);
            h = target + (h - target) * Smooth(ax / 4);
            water = float.NaN;
        }
        int mm = (int)MathF.Round(h * 1000);
        int wm = float.IsNaN(water) ? int.MinValue : (int)MathF.Round(water * 1000);
        if (wm != int.MinValue)
            wm = Math.Clamp(wm, mm, mm + 2048000);
        return (mm, wm);
    }
    public float SurfaceWetness(float x,float z,bool water) => water ? 1 :
        (Preset == "desert" ? .12f : 1) * Math.Clamp(1-River(x,z).Distance/18,0,.85f);
    public (WaterKind Kind, int Id) WaterBodyAt(int x, int z, int surface)
    {
        if (surface == int.MinValue) return (WaterKind.None, 0);
        if (Landscape.OceanAt(x,z) && surface == (int)MathF.Round(Landscape.SeaLevel*1000))
            return (WaterKind.Ocean, int.MaxValue);
        var lake = Lake(x,z);
        if (float.IsFinite(lake.Level) && surface == (int)MathF.Round(Math.Min(lake.Level, lake.Level + settings.WaterOffset)*1000))
            return (WaterKind.Lake, lake.Id);
        return (WaterKind.River, River(x,z).Id);
    }
    public TerrainChunkData BuildChunk(ChunkCoordinate c)
    {
        if (!c.Valid)
            throw new ArgumentOutOfRangeException(nameof(c));
        int[] heights = new int[TerrainChunkData.Count], water = new int[TerrainChunkData.Count], regions = new int[TerrainChunkData.Count];
        byte[] materials = new byte[TerrainChunkData.Count * 4], roads = new byte[TerrainChunkData.Count], environment = new byte[TerrainChunkData.Count * 9], kinds = new byte[TerrainChunkData.Count];
        for (int z = -1; z <= 65; z++)
            for (int x = -1; x <= 65; x++)
            {
                int i = (z + 1) * 67 + x + 1;
                if (x == -1) cancellation.ThrowIfCancellationRequested();
                var s = Sample(c.X * 64 + x, c.Z * 64 + z);
                heights[i] = s.Height;
                water[i] = s.Water;
                var body = WaterBodyAt(c.X*64+x,c.Z*64+z,s.Water);
                regions[i] = body.Id;
                kinds[i] = (byte)body.Kind;
            }
        for (int z = -1; z <= 65; z++)
            for (int x = -1; x <= 65; x++)
            {
                int i = (z + 1) * 67 + x + 1;
                float dx = (heights[(z + 1) * 67 + Math.Min(66, x + 2)] - heights[(z + 1) * 67 + Math.Max(0, x)]) / 2000f;
                float dz = (heights[Math.Min(66, z + 2) * 67 + x + 1] - heights[Math.Max(0, z) * 67 + x + 1]) / 2000f;
                float rock = Math.Clamp(MathF.Sqrt(dx * dx + dz * dz) * 1.2f, 0, 1);
                float wet = SurfaceWetness(c.X*64+x,c.Z*64+z,water[i]!=int.MinValue);
                roads[i] = Settlements.Road(c.X * 64 + x, c.Z * 64 + z);
                int ri = (int)(rock * 255);
                materials[i * 4] = (byte)(Settlements.PlotCoverage(c.X * 64 + x, c.Z * 64 + z) ? 255 : 0);
                materials[i * 4 + 1] = (byte)ri;
                materials[i * 4 + 2] = (byte)(wet * 255);
                var e = Landscape.Environment(c.X*64+x,c.Z*64+z, MathF.Sqrt(dx*dx+dz*dz), wet);
                static byte B(float v) => (byte)Math.Clamp((int)MathF.Round(v*255),0,255);
                environment[i*9] = B(e.Moisture); environment[i*9+1] = B(e.Exposure); environment[i*9+2] = B(e.Soil);
                environment[i*9+3] = B(e.Sediment/16); environment[i*9+4] = B(e.Forest); environment[i*9+5] = (byte)e.Geology;
                environment[i*9+6] = (byte)e.Habitat; environment[i*9+7] = B(e.Snow);
                environment[i*9+8]=B(Landscape.Drainage(c.X*64+x,c.Z*64+z));
                materials[i*4+2]=B(Math.Max(wet,e.Moisture*.55f));
                materials[i * 4 + 3] = B(e.Snow);
            }
        return new(c, heights, water, materials, Candidates(c).ToArray(), regions, roads, environment, kinds);
    }
    public static ulong Hash(int seed, int x, int z, int category)
    {
        ulong v = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)x * 0xBF58476D1CE4E5B9UL ^ (ulong)(uint)z * 0x94D049BB133111EBUL ^ (ulong)category);
        v = (v ^ (v >> 30)) * 0xBF58476D1CE4E5B9UL;
        v = (v ^ (v >> 27)) * 0x94D049BB133111EBUL;
        return v ^ (v >> 31);
    }
    /// <summary>Broad forest patches in world space; independent of chunk arrival and LOD.</summary>
    public float ForestCover(float x, float z)
    {
        if (Preset == "desert")
            return 0;
        return Landscape.Environment(x,z).Forest;
    }
    IEnumerable<PlacementCandidate> Candidates(ChunkCoordinate chunk)
    {
        for (int category = 0; category < 2; category++)
            for (int z = chunk.Z * 8; z < chunk.Z * 8 + 8; z++)
                for (int x = chunk.X * 8; x < chunk.X * 8 + 8; x++)
                {
                    ulong id = Hash(Seed, x, z, category + 1);
                    bool forestTree = category == 0 && Preset != "desert";
                    int px = x * 8 + 1 + (int)((id >> 16) % 6), pz = z * 8 + 1 + (int)((id >> 24) % 6);
                    if (forestTree)
                    {
                        // One jittered root per 8 m cell gives at least 6 m separation.
                        // Meadows remain open; coherent patches support closed stands.
                        float chance = Preset == "temperate" ? .015f + .9f * ForestCover(px, pz) : .01f + .72f * ForestCover(px, pz);
                        if (id % 10000 >= Math.Clamp(chance * settings.TreeDensity, 0, 1) * 10000)
                            continue;
                    }
                    else
                    {
                        if (id % 100 >= Math.Min(100, (category == 0 ? 2 * settings.TreeDensity : 28 * settings.RockDensity)))
                            continue;
                        // Keep rock and sparse desert candidate selection unchanged.
                        bool wins = true;
                        for (int dz = -1; dz <= 1; dz++)
                            for (int dx = -1; dx <= 1; dx++)
                                if ((dx != 0 || dz != 0) && Hash(Seed, x + dx, z + dz, category + 1) < id)
                                    wins = false;
                        if (!wins)
                            continue;
                    }
                    if (Settlements.Excludes(px, pz, category == 0 ? 5 : 3))
                        continue;
                    var s = Sample(px, pz);
                    var habitat = Landscape.Environment(px,pz);
                    if(category==0 && habitat.Habitat==Habitat.Beach)continue;
                    if (category == 1 && habitat.Habitat is Habitat.Meadow or Habitat.Arid or Habitat.Beach && id % 7 != 0) continue;
                    if (s.Water != int.MinValue || Vector2.Distance(new(px, pz), new(Gate.X, Gate.Z)) < 28 || Math.Abs(px - Gate.X) < 7 && pz > Gate.Z && pz < Gate.Z + 36)
                        continue;
                    if (category == 0 && Preset == "desert" && River(px, pz).Distance > 18)
                        continue;
                    float slope = Math.Abs(Sample(px + 1, pz).Height - Sample(px - 1, pz).Height) / 2000f + Math.Abs(Sample(px, pz + 1).Height - Sample(px, pz - 1).Height) / 2000f;
                    if (slope > (category == 0 ? .5f : 1.2f))
                        continue;
                    yield return new(id, category == 0 ? "tree" : "rock", new(px, s.Height / 1000f, pz), (id >> 32) % 62832 / 10000f, category == 0 && (id >> 40) % 5 == 0 ? .35f + (id >> 48) % 250 / 1000f : .8f + (id >> 48) % 600 / 1000f);
                }
    }
}
