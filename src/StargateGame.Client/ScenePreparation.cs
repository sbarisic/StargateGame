using System.Diagnostics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    readonly CancellationTokenSource startupStop = new();
    IEnumerator<bool>? preparation;
    IEnumerator<bool>? detailPreparation;
    public bool CommonReady
    {
        get;
        private set;
    }
    public string PreparationStage
    {
        get;
        private set;
    } = "Preparing renderer";
    public bool InspectionAssetsReady => CommonReady && testingInspection.All(e => models.ContainsKey(e.Model)) && detailPreparation is null && modelJobs.Count == 0 && texturesLoading.Count == 0 && warmPaletteMaps is null && HasWorld && terrainSize == PaletteSize;
    public bool HasWorld => attachedWorld is not null;

    public void PumpPreparation(double budgetMs = 2)
    {
        if (preparation is null)
            return;
        var clock = Stopwatch.StartNew();
        // Each setup step is separately scheduled. An indivisible driver call may
        // overrun the budget, but never starts another step in that frame.
        while (clock.Elapsed.TotalMilliseconds < budgetMs)
        {
            if (!preparation.MoveNext())
            {
                preparation.Dispose();
                preparation = null;
                CommonReady = true;
                StartupTrace.Mark("common-resources-ready", detail: $"read={ModelReadMilliseconds:F1}ms decode={ModelDecodeMilliseconds:F1}ms cpuPreparation={modelAssets.ParseMilliseconds:F1}ms uploadPeak={ModelUploadPeakMilliseconds:F1}ms");
                break;
            }
            if (!preparation.Current)
                break;
        }
        if (clock.Elapsed.TotalMilliseconds > budgetMs)
            StartupTrace.Mark("preparation-overrun", clock.Elapsed.TotalMilliseconds, PreparationStage);
    }
    IEnumerable<bool> PrepareCommonResources()
    {
        PreparationStage = "Compiling world shader";
        shader = LoadProgram("world.vert", "world.frag");
        yield return false;
        using (var pixel = new System.Drawing.Bitmap(1, 1))
        {
            pixel.SetPixel(0, 0, System.Drawing.Color.White);
            white = Own(graphics.CreateTextureFromImage(pixel));
        }
        yield return true;
        grassTexture = cliffTexture = sandTexture = snowTexture = buildingTexture = white;
        using (var pixel = new System.Drawing.Bitmap(1, 1))
        {
            pixel.SetPixel(0, 0, System.Drawing.Color.FromArgb(128, 128, 255));
            buildingNormal = Own(graphics.CreateTextureFromImage(pixel));
        }
        shader.SetUniform("uGrass", 1);
        shader.SetUniform("uCliff", 2);
        shader.SetUniform("uSand", 3);
        shader.SetUniform("uSnow", 4);
        shader.SetUniform("uModelNormal", 5);
        body = CreateBox();
        crate = CreateBox(true);
        debugLines = Own(graphics.CreateMesh3D());
        debugLines.PrimitiveType = PrimitiveType.Lines;
        yield return true;
        PreparationStage = "Preparing lighting shaders";
        foreach (bool step in InitializeLighting())
            yield return step;
        PreparationStage = "Preparing gate and props";
        // The prefab catalog builds sizeable collision/mesh tables on first use.
        // Resolve it on a reader worker before UploadModel calls Known().
        var catalog = modelAssets.PrepareCpuAsync(() => StargateGame.Building.BuildingPrefabs.All.Count, startupStop.Token);
        while (!catalog.IsCompleted)
            yield return false;
        _ = catalog.GetAwaiter().GetResult();
        foreach (string name in new[] { "stargate", "dhd", "wooden-crate", "barrel-red", "barrel-black" })
            RequestModel(name);
        while (modelJobs.Count > 0)
        {
            PumpModels(2);
            yield return false;
        }
        detailPreparation = PrepareDetailResources().GetEnumerator();
        PreparationStage = "Ready";
    }
    IEnumerable<bool> PrepareDetailResources()
    {
        // Detail preparation can start before session options are applied. Load
        // the small complete chain for both comparison modes instead of missing
        // it when realistic water is selected later in startup.
        PreparationStage = "Preparing water normal mips";
        foreach (bool step in PrepareWaterResources()) yield return step;
        PreparationStage = "Preparing base textures";
        foreach (var pair in new (string File, string Folder, Action<Texture> Set)[]{
            ("grass.png","terrain",t=>grassTexture=t),("cliff.png","terrain",t=>cliffTexture=t),
            ("sand.png","terrain",t=>sandTexture=t),("snow.png","terrain",t=>snowTexture=t),
            ("plaster.png","building",t=>buildingTexture=t),("plaster-normal.png","building",t=>buildingNormal=t)})
            foreach (bool step in PrepareLegacyTexture(pair.File, pair.Folder, pair.Set))
                yield return step;
        foreach (bool step in PrepareCharacterResources())
            yield return step;
        StartupTrace.Mark("character-and-base-detail-ready");
    }
    IEnumerable<bool> PrepareLegacyTexture(string name, string folder, Action<Texture> assign)
    {
        var work = modelAssets.PrepareCpuAsync(() =>
        {
            using var source = new MemoryStream(ReadRuntimeAsset(folder + "/" + name), writable: false);
            using var image = new System.Drawing.Bitmap(source);
            int width = image.Width, height = image.Height;
            var rect = new System.Drawing.Rectangle(0, 0, width, height);
            var bits = image.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                byte[] bytes = new byte[width * height * 4];
                byte[] row = new byte[width * 4];
                for (int y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + y * bits.Stride, row, 0, row.Length);
                    for (int x = 0; x < width; x++)
                    {
                        int dst = ((height - 1 - y) * width + x) * 4;
                        bytes[dst] = row[x * 4 + 2];
                        bytes[dst + 1] = row[x * 4 + 1];
                        bytes[dst + 2] = row[x * 4];
                        bytes[dst + 3] = 255;
                    }
                }
                return (Width: width, Height: height, Bytes: bytes);
            }
            finally
            {
                image.UnlockBits(bits);
            }
        }, startupStop.Token);
        while (!work.IsCompleted)
            yield return false;
        var data = work.GetAwaiter().GetResult();
        float aniso = graphics.Capabilities.SupportsAnisotropy ? Math.Min(8, graphics.Capabilities.MaximumAnisotropy) : 1;
        var texture = Own(graphics.CreateTexture(new TextureDescriptor(data.Width, data.Height, TextureFormat.RGBA8Unorm,
            mipLevels: 1 + (int)MathF.Log2(Math.Max(data.Width, data.Height)), sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.Repeat, TextureWrap.Repeat, anisotropy: aniso))));
        foreach (bool step in UploadTextureRows(texture, data.Bytes, data.Width, data.Height, 0))
            yield return step;
        texture.GenerateMipmaps();
        assign(texture);
        yield return true;
    }
    public bool PrepareArrival(ClientSession pending, double budgetMs = 2)
    {
        var clock = Stopwatch.StartNew();
        if (!ReferenceEquals(terrainStore, pending.Terrain) || !ReferenceEquals(attachedWorld, pending.World))
            ChangePlanet(pending.World, pending.Terrain);
        foreach (string model in pending.World.Definition.Entities.Select(e => e.Model).Distinct())
            RequestModel(model);
        PreparationStage = "Preparing destination materials";
        WarmTerrainPalette(pending.World.Definition.Preset, budgetMs);
        if (paletteWork?.IsFaulted == true)
            paletteWork.GetAwaiter().GetResult();
        if (clock.Elapsed.TotalMilliseconds < budgetMs)
        {
            PreparationStage = "Preparing arrival terrain";
            SupplyStreaming(pending);
            UpdateStreaming(pending.Local.Position, budgetMs - clock.Elapsed.TotalMilliseconds, true);
        }
        return terrainStore is null || ArrivalRegionReady(pending.Local.Position);
    }
}
