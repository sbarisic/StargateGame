using System.Numerics;
using System.Text.Json;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class SceneRenderer : IDisposable
{
    readonly GraphicsContext graphics;
    readonly List<IDisposable> resources = [];
    sealed record ModelPart(Mesh3D mesh, Texture texture, Texture? normal, float AlphaCutoff, Texture? Emission, Vector3 EmissionColor, float EmissionIntensity, Vector3? LightPosition, int DhdKeyId, bool DhdOrb, int Chevron, Texture? MetallicRoughness = null, bool Leaf = false, float HeightScale = 0);
    readonly Dictionary<string, List<ModelPart>> models = [];
    const int TreeBatchSize = 8;
    readonly Dictionary<string, Texture> textures = [];
    readonly Dictionary<string, AxisAlignedBoundingBox> modelBaseBounds = [];
    ShaderProgram shader = null!;
    Mesh3D? terrain;
    Mesh3D body = null!, crate = null!, debugLines = null!;
    Texture white = null!;
    Texture grassTexture = null!, cliffTexture = null!, sandTexture = null!, snowTexture = null!;
    Texture buildingTexture = null!, buildingNormal = null!;
    PlanetWorld? attachedWorld;
    public PlanetWorld World
    {
        get => attachedWorld ?? throw new InvalidOperationException("No world is attached to this renderer.");
        private set => attachedWorld = value;
    }
    public TerrainStore? TerrainSource => terrainStore;
    public void ChangePlanet(PlanetWorld world, TerrainStore? store)
    {
        ClearPlanet(warmCache?.Begin.LocationId == world.Definition.LocationId, true, world.Definition.Preset == "temperate");
        World = world;
        terrainStore = store;
        if (store is not null)
            meshGeneration = new(world.Definition.Seed, world.Definition.Preset, canonical: store);
        else
            terrain = CreateTerrain();
    }
    public void ClearPlanet(bool preservePreload = false, bool preservePalette = false, bool preserveFoliage = false)
    {
        ResetExposure();
        ClearTreePrototype();
        foreach (var job in modelJobs.Values)
            job.Dispose();
        foreach (string name in modelJobs.Keys) Handle(name).Cancel();
        modelJobs.Clear();
        modelAssets.CancelPending();
        ClearDebugMeshes();
        debugOwner = null;
        DebugWorld = null;
        actorAnimation.Clear();
        actorPoses.Clear();
        aimBlend = 0;
        lastCharacterTime = 0;
        if (!preservePreload)
            ClearDestinationResources();
        ResetHorizons();
        DhdGlyphs.Clear();
        visibleLitDhds = [];
        ClearFoliage(preserveFoliage);
        ClearTerrainMaterials(preservePalette);
        ClearAtmosphere();
        ClearGroundCover();
        ClearWeather();
        ClearBuildings();
        DisposeStreaming();
        terrainStore = null;
        shadows?.InvalidateAll();
        previousFrame = null;
        if (terrain is not null)
        {
            resources.Remove(terrain);
            terrain.Dispose();
            terrain = null;
        }
    }
    public static string Asset(params string[] path) => Path.Combine([AppContext.BaseDirectory, "assets", .. path]);
    T Own<T>(T value) where T : IDisposable
    {
        resources.Add(value);
        return value;
    }

    public SceneRenderer(GraphicsContext graphics, CookedAssets assets)
    {
        this.graphics = graphics;
        modelAssets = assets;
        treeVariants = JsonSerializer.Deserialize<Dictionary<string, string[]>>(ReadRuntimeAsset("vegetation/trees.json"))!;
        worldCanopies = JsonSerializer.Deserialize<Dictionary<string, TreePrototypeDefinition>>(ReadRuntimeAsset("vegetation/world-canopies.json"))!;
        foreach (string species in treeVariants.Keys.Where(k => k != "Broadleaf_Desktop"))
            if (!worldCanopies.ContainsKey(species))
                throw new InvalidDataException("Missing custom canopy for tree species " + species + ". Rebuild vegetation assets.");
        preparation = PrepareCommonResources().GetEnumerator();
    }
    Texture LoadTerrainTexture(string name, string folder = "terrain")
    {
        using var source = new MemoryStream(ReadRuntimeAsset(folder + "/" + name), writable: false);
        using var image = new System.Drawing.Bitmap(source);
        // Extracted terrain maps pack masks/height in alpha. Make them opaque
        // before FishGfx's image conversion can composite those masks into RGB.
        var pixels = image.LockBits(new System.Drawing.Rectangle(0, 0, image.Width, image.Height),
            System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[image.Width * 4];
            for (int y = 0; y < image.Height; y++)
            {
                var address = pixels.Scan0 + y * pixels.Stride;
                System.Runtime.InteropServices.Marshal.Copy(address, row, 0, row.Length);
                for (int x = 3; x < row.Length; x += 4)
                    row[x] = 255;
                System.Runtime.InteropServices.Marshal.Copy(row, 0, address, row.Length);
            }
        }
        finally
        {
            image.UnlockBits(pixels);
        }
        float anisotropy = graphics.Capabilities.SupportsAnisotropy ? Math.Min(8, graphics.Capabilities.MaximumAnisotropy) : 1;
        return Own(graphics.CreateTextureFromImage(image, new TextureLoadOptions
        {
            MipLevels = 1 + (int)MathF.Log2(Math.Max(image.Width, image.Height)),
            Sampling = new TextureSamplingState(TextureFilter.LinearMipmapLinear, TextureFilter.Linear,
                TextureWrap.Repeat, TextureWrap.Repeat, anisotropy: anisotropy)
        }));
    }
    Mesh3D MakeMesh(Vector3[] p, Vector3[] n, Vector2[] uv)
    {
        var mesh = Own(graphics.CreateMesh3D());
        mesh.SetVertices(p);
        mesh.SetNormals(n);
        mesh.SetUVs(uv);
        return mesh;
    }
    Mesh3D CreateTerrain()
    {
        List<Vector3> p = [], n = [];
        List<Vector2> uv = [];
        foreach (var triangle in World.Triangles())
            Triangle(triangle.A, triangle.B, triangle.C);
        void Triangle(Vector3 a, Vector3 b, Vector3 c)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            foreach (var point in new[] { a, b, c })
            {
                p.Add(point);
                n.Add(normal);
                uv.Add(Vector2.Zero);
            }
        }
        return MakeMesh(p.ToArray(), n.ToArray(), uv.ToArray());
    }
    Mesh3D CreateBox(bool cube = false)
    {
        Vector3[] corners = [new(-.3f,0,-.22f),new(.3f,0,-.22f),new(.3f,1.8f,-.22f),new(-.3f,1.8f,-.22f),
            new(-.3f,0,.22f),new(.3f,0,.22f),new(.3f,1.8f,.22f),new(-.3f,1.8f,.22f)];
        if (cube)
            corners = corners.Select(p => new Vector3(p.X / .6f, p.Y / 1.8f - .5f, p.Z / .44f)).ToArray();
        int[] indices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5, 3, 7, 6, 3, 6, 2, 0, 1, 5, 0, 5, 4];
        var p = indices.Select(i => corners[i]).ToArray();
        var n = new Vector3[p.Length];
        for (int i = 0; i < p.Length; i += 3)
            n[i] = n[i + 1] = n[i + 2] = Vector3.Normalize(Vector3.Cross(p[i + 1] - p[i], p[i + 2] - p[i]));
        return MakeMesh(p, n, new Vector2[p.Length]);
    }
    public void Dispose()
    {
        DisposeExposure();
        preparation?.Dispose();
        detailPreparation?.Dispose();
        detailPreparation = null;
        preparation = null;
        startupStop.Cancel();
        grassVolumeUpload?.Dispose();
        grassVolumeUpload = null;
        ClearTreePrototype();
        foreach (var job in modelJobs.Values)
            job.Dispose();
        foreach (string name in modelJobs.Keys) Handle(name).Cancel();
        modelJobs.Clear();
        foreach (var job in textureRefinements.Values) job.Dispose();
        textureRefinements.Clear();
        modelAssets.Dispose();
        startupStop.Dispose();
        ClearGateSpray();
        viewmodelTarget?.Dispose();
        viewmodelMultisampleTarget?.Dispose();
        ClearDestinationResources();
        ClearDebugMeshes();
        ClearFoliage();
        ClearTerrainMaterials();
        ClearAtmosphere();
        ClearGroundCover();
        ClearWeather();
        ClearBuildings();
        DisposeStreaming();
        grassCancellation.Dispose();
        surfaceCancellation.Dispose();
        foliageCancellation.Dispose();
        DisposeHorizonWaves();
        DisposeTargets();
        DisposeAtmosphereCache();
        for (int i = resources.Count - 1; i >= 0; i--)
            resources[i].Dispose();
        resources.Clear();
    }
    int dynamicDebugLineCount;
    public double DebugLineCpuMilliseconds
    {
        get;
        private set;
    }
    readonly Vertex3[] debugVertices = new Vertex3[60000];
    public void DrawPhysics(RenderPass pass, IEnumerable<DebugLine> lines)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int count = 0;
        foreach (var line in lines)
        {
            if (count == debugVertices.Length)
                break;
            var color = new Color((byte)(line.Color.X * 255), (byte)(line.Color.Y * 255), (byte)(line.Color.Z * 255));
            debugVertices[count++] = new Vertex3(line.A, color);
            debugVertices[count++] = new Vertex3(line.B, color);
        }
        dynamicDebugLineCount = count / 2;
        if (count == 0)
        {
            DebugLineCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
            return;
        }
        debugLines.SetVertices(debugVertices, count, hasUvs: false);
        pass.DrawMesh(debugLines, white);
        DebugLineCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
}
