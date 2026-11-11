using System.Diagnostics;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using FishGfx.Graphics;
using StargateGame.Shared;
using StargateGame.WorldGen;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    Texture[]? terrainPbr;
    Texture? surfaceField;
    string? terrainPalette;
    string? testMeadowMaterial;
    public void SetTestMeadowMaterial(string? material)
    {
        if (testMeadowMaterial == material) return;
        if (material is not (null or "rocky-rugged-terrain-pbr")) throw new ArgumentException("Unsupported relief baseline material.");
        testMeadowMaterial = material; ClearTerrainMaterials();
    }
    int terrainSize;
    readonly Dictionary<ChunkCoordinate, SurfaceMap> surfaceMaps = [];
    readonly Dictionary<ChunkCoordinate, (int Revision, Task<SurfaceMap> Task)> surfaceJobs = [];
    readonly Dictionary<ChunkCoordinate, SurfaceCanopy[]> surfaceCanopies = [];
    CancellationTokenSource surfaceCancellation = new();
    WorldChanges? surfaceState;
    int surfaceRevision;
    float[] terrainMetres = new float[11], terrainGain = new float[11], terrainNormalStrength = new float[11], terrainHeightStrength = new float[11];
    float[] terrainRelief = new float[11];
    float[] terrainGrass = new float[11];
    public bool TerrainParallaxEnabled
    {
        get;
        set;
    } = true;
    public double TerrainGpuMilliseconds => gpuTimer.TerrainMilliseconds;
    public long TerrainMaterialBytes => (terrainPbr is null ? 0 : (long)terrainSize * terrainSize * 4 * 3 * 11 * 4 / 3) + (surfaceField is null ? 0 : 2048L * 2048 * 16);
    public int SurfaceMapCount => surfaceMaps.Count;
    public double SurfaceCpuMilliseconds
    {
        get;
        private set;
    }
    void ClearTerrainMaterials(bool preservePalette = false)
    {
        surfaceCancellation.Cancel();
        surfaceCancellation.Dispose();
        surfaceCancellation = new();
        if (!preservePalette)
        {
            foreach (var t in terrainPbr ?? [])
                t.Dispose();
            terrainPbr = null;
            terrainPalette = null;
            ClearWarmPalette();
        }
        surfaceField?.Dispose();
        surfaceField = null;
        surfaceMaps.Clear();
        surfaceJobs.Clear();
        surfaceCanopies.Clear();
        surfaceState = null;
        surfaceRevision++;
    }
    Task<TerrainPaletteData>? paletteWork;
    CancellationTokenSource? paletteStop;
    string? warmPalette;
    int warmPaletteSize, paletteUpload;
    Texture[]? warmPaletteMaps;
    int PaletteSize => Settings.Quality switch { GraphicsQuality.Low => 512, GraphicsQuality.High => 2048, _ => 1024 };
    static string PalettePreset(string preset) => preset == "legacy" ? "temperate" : preset;
    void ClearWarmPalette()
    {
        paletteStop?.Cancel();
        paletteStop?.Dispose();
        paletteStop = null;
        if (paletteWork is { } task)
            _ = task.ContinueWith(t =>
            {
                _ = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        paletteWork = null;
        warmPalette = null;
        paletteUpload = 0;
        paletteLevelData = null;
        paletteLease?.Dispose(); paletteLease = null;
        paletteRow = 0;
        foreach (var map in warmPaletteMaps ?? [])
            map.Dispose();
        warmPaletteMaps = null;
    }
    void RequestPalette(string preset)
    {
        preset = PalettePreset(preset);
        int size = PaletteSize;
        if (warmPalette == preset && warmPaletteSize == size)
            return;
        ClearWarmPalette();
        warmPalette = preset;
        warmPaletteSize = size;
        paletteStop = new();
        var token = paletteStop.Token;
        string root = Asset("terrain", "pbr");
        paletteWork = Task.Run(() => TerrainPaletteData.Load(root, preset, size, token, modelAssets, testMeadowMaterial), token);
    }
    Texture[] AllocatePalette(int size)
    {
        Texture[] maps = new Texture[3];
        try
        {
            float aniso = graphics.Capabilities.SupportsAnisotropy ? Math.Min(8, graphics.Capabilities.MaximumAnisotropy) : 1;
            for (int i = 0; i < 3; i++)
                maps[i] = graphics.CreateTexture(new TextureDescriptor(size, size, TextureFormat.RGBA8Unorm, dimension: TextureDimension.Texture2DArray, mipLevels: 1 + (int)MathF.Log2(size), arrayLayers: 11, sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.Repeat, TextureWrap.Repeat, anisotropy: aniso)));
            return maps;
        }
        catch
        {
            foreach (var map in maps)
                map?.Dispose();
            throw;
        }
    }
    byte[]? paletteLevelData;
    CookedAssets.StagingLease? paletteLease;
    int paletteRow;
    bool UploadPaletteLevel(TerrainPaletteData data)
    {
        var level = data.Levels[paletteUpload];
        if (paletteLevelData is null)
        {
            if (level.AssetKey is not null)
            {
                if (!modelAssets.TryTakeLease(level.AssetKey, out var lease)) return false;
                paletteLease = lease;
                paletteLevelData = lease.Bytes;
            }
            else paletteLevelData = level.Bytes;
            if (paletteLevelData.Length != level.Side * level.Side * 4) throw new InvalidDataException("Invalid terrain mip dimensions.");
        }
        int rows = Math.Min(level.Side - paletteRow, Math.Max(1, 256 * 1024 / (level.Side * 4)));
        warmPaletteMaps![level.Map].Write<byte>(paletteLevelData.AsSpan(paletteRow * level.Side * 4, rows * level.Side * 4),
            TextureDataFormat.RGBA8Unorm, new TextureArrayRegion(0, paletteRow, level.Layer, level.Side, rows, 1), level.Mip);
        paletteRow += rows;
        if (paletteRow == level.Side) { paletteRow = 0; paletteLevelData = null; paletteLease?.Dispose(); paletteLease = null; paletteUpload++; }
        return true;
    }
    void WarmTerrainPalette(string preset, double budgetMs)
    {
        if (terrainPbr is not null && terrainPalette == PalettePreset(preset) && terrainSize == PaletteSize)
            return;
        RequestPalette(preset);
        if (budgetMs <= 0 || paletteWork?.IsCompletedSuccessfully != true)
            return;
        var watch = Stopwatch.StartNew();
        var data = paletteWork.Result;
        if (warmPaletteMaps is null)
        {
            warmPaletteMaps = AllocatePalette(data.Size);
            return;
        }
        while (paletteUpload < data.Levels.Length && watch.Elapsed.TotalMilliseconds < budgetMs)
            if (!UploadPaletteLevel(data)) break;
    }
    void PrepareTerrainMaterials()
    {
        if (terrainPbr is null)
        {
            // A complete, small palette is usable while final material mips stream.
            terrainPbr = AllocatePalette(1);
            byte[][] pixels = [ [90, 100, 85, 255], [128, 128, 255, 128], [255, 200, 0, 255] ];
            for (int map = 0; map < 3; map++)
                for (int layer = 0; layer < 11; layer++)
                    terrainPbr[map].Write<byte>(pixels[map], TextureDataFormat.RGBA8Unorm, new TextureArrayRegion(0, 0, layer, 1, 1, 1));
            Array.Fill(terrainMetres, 1);
            Array.Fill(terrainGain, 1);
            terrainSize = 1;
        }
        int size = PaletteSize;
        string preset = PalettePreset(World.Definition.Preset);
        if (terrainPbr is not null && terrainSize == size && terrainPalette == preset)
            return;
        RequestPalette(preset);
        var watch = Stopwatch.StartNew();
        if (paletteWork?.IsFaulted == true) paletteWork.GetAwaiter().GetResult();
        if (paletteWork?.IsCompletedSuccessfully != true || warmPaletteMaps is null || paletteUpload != paletteWork.Result.Levels.Length)
            return;
        var data = paletteWork.Result;
        foreach (var t in terrainPbr ?? [])
            t.Dispose();
        terrainPbr = warmPaletteMaps;
        warmPaletteMaps = null;
        terrainSize = size;
        terrainPalette = preset;
        terrainMetres = data.Metres;
        terrainGain = data.Gain;
        terrainNormalStrength = data.Normal;
        terrainHeightStrength = data.Height;
        terrainRelief = data.Relief;
        terrainGrass = data.GrassCoverage;
        // Surface/grass fields derived from a previous palette cannot be reused.
        surfaceRevision++;
        surfaceMaps.Clear();
        ClearGroundCover();
        Console.WriteLine($"Terrain palette {preset}/{size}: activation {watch.Elapsed.TotalMilliseconds:F1} ms");
        ClearWarmPalette();
    }
    void PrepareSurfaceField(Vector3 eye, double used)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (terrainStore is null || terrainPalette != PalettePreset(World.Definition.Preset))
                return;
            if (surfaceField is null)
            {
                surfaceField = graphics.CreateTexture(new TextureDescriptor(2048, 2048, TextureFormat.RGBA32Float, sampling: new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge)));
                surfaceField.Write<Vector4>(Enumerable.Repeat(new Vector4(0, 0, -4096, 0), 2048 * 2048).ToArray(), TextureDataFormat.RGBA32Float);
            }
            if (!ReferenceEquals(surfaceState, buildingState))
            {
                surfaceState = buildingState;
                surfaceRevision++;
                surfaceMaps.Clear();
                surfaceCanopies.Clear();
            }
            foreach(var old in surfaceMaps.Keys.Where(k=>!terrainStore.TryGetChunk(k,out _)).ToArray()){surfaceMaps.Remove(old);surfaceCanopies.Remove(old);}
            foreach (var c in terrainStore.Chunks.OrderBy(c => Vector2.DistanceSquared(new(c.Coordinate.X * 64 + 32, c.Coordinate.Z * 64 + 32), new(eye.X, eye.Z))))
            {
                if (surfaceJobs.Count >= 2)
                    break;
                if (surfaceMaps.ContainsKey(c.Coordinate) || surfaceJobs.ContainsKey(c.Coordinate))
                    continue;
                var obstacles = CoverObstacles(new(c.Coordinate.X * 8, c.Coordinate.Z * 8), 64);
                if (obstacles is null)
                    continue;
                var neighbours = c.Coordinate.Around(1).Where(n => n.Valid).Select(n => terrainStore.TryGetChunk(n, out var v) ? v : null).Where(v => v is not null).ToDictionary(v => v!.Coordinate, v => v!);
                List<SurfaceCanopy> crowns = [];
                foreach (var pair in neighbours)
                {
                    if (!surfaceCanopies.TryGetValue(pair.Key, out var trees))
                        surfaceCanopies[pair.Key] = trees = TreeScenery.Resolve(pair.Value, World.Definition.Preset, World.Definition.Fixture).Where(t => !removedScenery.Contains(("tree", t.Id))).Select(t => new SurfaceCanopy(new((t.Min.X + t.Max.X) / 2, (t.Min.Z + t.Max.Z) / 2), Vector2.Max(new(1), new((t.Max.X - t.Min.X) * .5f, (t.Max.Z - t.Min.Z) * .5f)))).ToArray();
                    crowns.AddRange(trees);
                }
                var token = surfaceCancellation.Token;
                var canopy = crowns.ToArray();
                var planet = World.Definition;
                var grassLayers = terrainGrass;
                surfaceJobs[c.Coordinate] = (surfaceRevision, Task.Run(() => SurfaceEcology.Build(c, neighbours, canopy, obstacles, token, planet, grassLayers), token));
            }
            int published = 0;
            foreach (var key in surfaceJobs.Keys.ToArray())
            {
                var job = surfaceJobs[key];
                // Admit one completed field even during continuous terrain uploads.
                // Otherwise the shared budget can starve all grass indefinitely.
                if (!job.Task.IsCompleted || published > 0 && clock.Elapsed.TotalMilliseconds + used > .5)
                    continue;
                surfaceJobs.Remove(key);
                if (job.Task.IsCanceled || job.Revision != surfaceRevision)
                    continue;
                var map = job.Task.GetAwaiter().GetResult();
                surfaceMaps[key] = map;
                surfaceField.Write<Vector4>(map.Pixels(), TextureDataFormat.RGBA32Float, new TextureRegion(key.X * 32 + 1024, key.Z * 32 + 1024, 32, 32));
                published++;
            }
        }
        finally
        {
            SurfaceCpuMilliseconds = clock.Elapsed.TotalMilliseconds;
        }
    }
    void SurfaceUniforms(ShaderProgram p)
    {
        p.SetUniform("uSurfaceField", 25);
        p.SetUniform("uSurfaceEnabled", surfaceField is null ? 0 : 1);
        p.SetUniform("uSurfaceSeed", (int)(GroundCover.PlanetSalt(World.Definition) & 65535));
    }
    IDisposable BindSurface() => (surfaceField ?? white).Bind(25);
    IDisposable BindTerrainPbr(ShaderProgram p)
    {
        p.SetUniform("uLandscapeView", (int)LandscapeView);
        float wind=(PlanetPlan.Hash(World.Definition.Seed,0,0,9102)%65536)/65536f*MathF.Tau;
        p.SetUniform("uTerrainWind",new Vector2(MathF.Cos(wind),MathF.Sin(wind)));
        p.SetUniform("uTerrainColor", 26);
        p.SetUniform("uTerrainNormal", 27);
        p.SetUniform("uTerrainMasks", 28);
        p.SetUniform("uTerrainRelief", terrainRelief);
        p.SetUniform("uTerrainParallaxSteps", TerrainParallaxEnabled ? Settings.Quality switch
        {
            GraphicsQuality.Low => 16,
            GraphicsQuality.High => 48,
            _ => 32
        } : 0);
        p.SetUniform("uTerrainParallaxRange", Settings.Quality switch
        {
            GraphicsQuality.Low => 18f,
            GraphicsQuality.High => 40f,
            _ => 28f
        });
        p.SetUniform("uRoadPaved", World.Definition.Settlements?.Roads.Any(r => r.Paved) == true ? 1f : 0f);
        p.SetUniform("uTerrainMetres", terrainMetres);
        p.SetUniform("uTerrainGain", terrainGain);
        p.SetUniform("uTerrainNormalStrength", terrainNormalStrength);
        p.SetUniform("uTerrainHeightStrength", terrainHeightStrength);
        p.SetUniform("uTerrainGrass", terrainGrass);
        p.SetUniform("uTerrainBiome", World.Definition.Preset == "icy" ? 2 : World.Definition.Preset == "desert" ? 1 : 0);
        SurfaceUniforms(p);
        return new TextureBindings([terrainPbr![0].Bind(26), terrainPbr[1].Bind(27), terrainPbr[2].Bind(28)]);
    }
}
