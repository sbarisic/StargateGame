using System.Numerics;
using System.Text.Json;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed record TreeVolumeSettings
{
    public float StepCells { get; init; } = .65f;
    public float OpacityThreshold { get; init; } = .35f;
    public int SunSteps { get; init; } = 24;
    public float Transmission { get; init; } = .10f;
    public float SkyFillFloor { get; init; } = .45f;
    public float GroundFillWeight { get; init; } = .35f;
}

sealed record TreePrototypeDefinition(string LeafModel, string TrunkPrefix, int TrunkLevels, float Scale, float Height, string Canopy, float DensityScale = 1f)
{
    public static TreePrototypeDefinition For(string species) => species switch
    {
        "broadleaf" => new("veg-MoreRealisticTree_01", "prototype-trunk-", 3, 1.6f, 9.4f, "vegetation/prototype/canopy.json"),
        "broadleaf-2" => new("veg-MoreRealisticTree_02", "prototype-broadleaf-2-trunk-", 3, 1.6f, 8f, "vegetation/prototype/broadleaf-2/canopy.json"),
        "birch" => new("veg-European_White_Birch_Hero_Winter-summer", "prototype-birch-trunk-", 3, 1f, 17.6f, "vegetation/prototype/birch/canopy.json", .55f),
        "spruce" => new("veg-BlueSpruce_Desktop_Winter", "prototype-spruce-trunk-", 1, 1f, 19.1f, "vegetation/prototype/spruce/canopy.json", .35f),
        _ => throw new ArgumentException("--tree-species expects broadleaf, broadleaf-2, birch or spruce.")
    };
}

// Shared volume resources for normal scenery and the explicit inspection fixture.
sealed partial class SceneRenderer
{
    public TreeVolumeSettings VolumeTreeSettings { get; set; } = new();
    public bool CanopyLodBaseline { get; set; }
    public int[] CanopyLodCounts { get; } = new int[4];
    public int CanopyDraws => CanopyLodCounts.Sum();
    public double CanopyGpuMilliseconds => gpuTimer.CanopyMilliseconds;
    public double CanopyCpuMilliseconds { get; private set; }
    public string TreeVolumeInspection { get; set; } = "lit";
    public string TreePrototype
    {
        get;
        set;
    } = "off";
    sealed class PrototypeVolume
    {
        public Texture? Color, Normals, Ambient;
        public long AmbientBytes;
        public Vector3 Minimum, Maximum;
        public float Cell, DensityReference;
        public long Bytes;
        public bool Ready;
        public int Levels;
    }
    readonly Dictionary<string, PrototypeVolume> prototypeVolumes = [];
    PrototypeTree[] prototypeTrees = [];
    ShaderProgram? prototypeShader;
    Mesh3D? prototypeQuad;
    IEnumerator<bool>? prototypeUpload;
    long prototypeBytes => prototypeVolumes.Values.Sum(v => v.Bytes);
    public const float TreePrototypeScale = 1.6f;
    string treeSpecies = "broadleaf";
    bool treeForest;
    int treeForestSeed = 7319;
    public string TreeSpecies
    {
        get => treeSpecies;
        set
        {
            if (value == treeSpecies)
                return;
            if (!worldCanopies.ContainsKey(value)) _ = TreePrototypeDefinition.For(value);
            treeSpecies = value;
            ResetPrototypeLayout();
        }
    }
    public bool TreeForest
    {
        get => treeForest;
        set
        {
            if (value != treeForest)
            {
                treeForest = value;
                ResetPrototypeLayout();
            }
        }
    }
    public int TreeForestSeed
    {
        get => treeForestSeed;
        set
        {
            if (value != treeForestSeed)
            {
                treeForestSeed = value;
                ResetPrototypeLayout();
            }
        }
    }
    void ResetPrototypeLayout()
    {
        ClearTreePrototype();
        shadows?.InvalidateAll();
        InvalidateGateShadows();
        shelterDirty = true;
    }
    string PrototypeTrunk(PrototypeTree tree)
    {
        float pixels = treeFocalPixels * tree.Definition.Height * tree.Scale / Math.Max(1, Vector3.Distance(treeCamera, tree.Position));
        int level = pixels >= 256 ? 0 : pixels >= 112 ? 1 : 2;
        return tree.Definition.TrunkPrefix + Math.Min(level, tree.Definition.TrunkLevels - 1);
    }
    bool InspectTree => TreePrototype != "off" && World.Definition.Fixture == "graphics-lab";
    public bool TreePrototypeReady => !InspectTree || prototypeTrees.Length > 0 && prototypeTrees.All(tree =>
        models.ContainsKey(tree.Definition.LeafModel) && (TreePrototype == "current" || models.ContainsKey(PrototypeTrunk(tree))) &&
        (TreePrototype != "volume" || prototypeVolumes.TryGetValue(tree.Species, out var volume) && volume.Ready));

    void PrepareTreePrototype()
    {
        if (!InspectTree)
        {
            StartWorldCanopyUploads();
            return;
        }
        if (TreePrototype is not ("current" or "mesh" or "volume"))
            throw new ArgumentException("--tree-prototype expects current, mesh, volume or off.");
        if (prototypeTrees.Length == 0)
        {
            prototypeTrees = TreeForest ? PrototypeForest.Create(TreeForestSeed) : [new(TreeSpecies, Vector3.Zero, 0, 1, worldCanopies.GetValueOrDefault(TreeSpecies))];
            Console.WriteLine($"TREE INSPECTION: {prototypeTrees.Length} trees; forest={TreeForest}; seed={TreeForestSeed}");
        }
        foreach (var tree in prototypeTrees.DistinctBy(t => t.Species))
        {
            RequestModel(tree.Definition.LeafModel);
            if (TreePrototype != "current")
                for (int level = 0; level < tree.Definition.TrunkLevels; level++)
                    RequestModel(tree.Definition.TrunkPrefix + level);
        }
        if (TreePrototype == "volume" && prototypeUpload is null && prototypeTrees.Any(t => !prototypeVolumes.ContainsKey(t.Species)))
            prototypeUpload = UploadPrototypeForest().GetEnumerator();
    }
    IEnumerable<bool> UploadPrototypeForest()
    {
        foreach (var tree in prototypeTrees.DistinctBy(t => t.Species))
        {
            if (prototypeVolumes.ContainsKey(tree.Species))
                continue;
            var volume = new PrototypeVolume();
            prototypeVolumes.Add(tree.Species, volume);
            foreach (bool uploaded in UploadTreePrototype(tree.Species, tree.Definition, volume))
                yield return uploaded;
        }
    }

    IEnumerable<bool> UploadTreePrototype(string species, TreePrototypeDefinition definition, PrototypeVolume volume)
    {
        byte[] data;
        while (!modelAssets.TryTake(definition.Canopy, out data))
            yield return false;
        using var document = JsonDocument.Parse(data);
        var descriptor = document.RootElement;
        static Vector3 V(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
        if (descriptor.GetProperty("version").GetInt32() != 3)
            throw new InvalidDataException("Incompatible canopy. Rebuild the version 3 canopy and models.sga.");
        long payloadBytes = descriptor.GetProperty("bytes").GetInt64();
        long ambientBytes = descriptor.GetProperty("ambient").GetProperty("bytes").GetInt64();
        if (payloadBytes <= 0 || payloadBytes > 160L * 1024 * 1024 || ambientBytes <= 0 || ambientBytes > 1024 * 1024)
            throw new InvalidDataException("Inspection canopy exceeds its resource budget.");
        volume.Minimum = V(descriptor.GetProperty("minimum"));
        volume.Maximum = V(descriptor.GetProperty("maximum"));
        volume.Cell = descriptor.GetProperty("cellSize").GetSingle();
        volume.DensityReference = descriptor.GetProperty("densityReferenceDistance").GetSingle();
        if (descriptor.GetProperty("densityEncoding").GetString() != "opacity-at-reference-distance" ||
            !float.IsFinite(volume.DensityReference) || volume.DensityReference <= 0 ||
            !float.IsFinite(volume.Cell) || volume.Cell <= 0 ||
            !float.IsFinite(volume.Minimum.LengthSquared()) || !float.IsFinite(volume.Maximum.LengthSquared()) ||
            volume.Minimum.X >= volume.Maximum.X || volume.Minimum.Y >= volume.Maximum.Y || volume.Minimum.Z >= volume.Maximum.Z)
            throw new InvalidDataException("Invalid canopy density or bounds.");
        var levels = descriptor.GetProperty("levels");
        var size = levels[0].GetProperty("size").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        if (size.Length != 3 || size.Any(v => v is < 1 or > 256) || levels.GetArrayLength() > 9)
            throw new InvalidDataException("Invalid canopy dimensions.");
        if (CanopyMipSelection.Bytes(size, levels.GetArrayLength(), 0) != payloadBytes)
            throw new InvalidDataException("Canopy descriptor byte count mismatch.");
        var selection = CanopyMipSelection.Select(size, levels.GetArrayLength(),
            InspectTree ? 256 : Settings.Quality == GraphicsQuality.Low ? 64 : 128,
            InspectTree ? long.MaxValue : ModelBudgetBytes - ModelGpuBytes, ambientBytes);
        int first = selection.First;
        volume.Bytes = selection.PayloadBytes + ambientBytes;
        volume.Cell *= 1 << first;
        var textureDescriptor = new TextureDescriptor(Math.Max(1, size[0] >> first), Math.Max(1, size[1] >> first), dimension: TextureDimension.Texture3D,
            depth: Math.Max(1, size[2] >> first), mipLevels: levels.GetArrayLength() - first,
            sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
        bool complete = false;
        try
        {
            volume.Levels = levels.GetArrayLength() - first;
            volume.Color = Own(graphics.CreateTexture(textureDescriptor));
            yield return true;
            volume.Normals = Own(graphics.CreateTexture(textureDescriptor));
            yield return true;
            long uploaded = 0;
            for (int level = first; level < levels.GetArrayLength(); level++)
            {
                int width = Math.Max(1, size[0] >> level), height = Math.Max(1, size[1] >> level), depth = Math.Max(1, size[2] >> level);
                var dimensions = levels[level].GetProperty("size").EnumerateArray().Select(v => v.GetInt32());
                var slices = levels[level].GetProperty("slices");
                if (!dimensions.SequenceEqual(new[] { width, height, depth }) || slices.GetArrayLength() != depth)
                    throw new InvalidDataException("Invalid canopy mip dimensions.");
                for (int z = 0; z < depth; z++)
                    for (int channel = 0; channel < 2; channel++)
                    {
                        // Keep a small read window ahead of uploads. Otherwise each slice
                        // waits a whole frame for a background read before the next starts.
                        for (int next = z * 2 + channel; next < Math.Min(depth * 2, z * 2 + channel + 16); next++)
                            modelAssets.Prefetch(slices[next / 2][next % 2].GetString()!);
                        string key = slices[z][channel].GetString()!;
                        if (!key.StartsWith("vegetation/prototype/", StringComparison.Ordinal) || key.Contains(".."))
                            throw new InvalidDataException("Invalid canopy block path.");
                        CookedAssets.StagingLease lease;
                        while (!modelAssets.TryTakeLease(key, out lease)) yield return false;
                        using var held = lease;
                        data = lease.Bytes;
                        if (data.Length != width * height * 4)
                            throw new InvalidDataException("Invalid canopy slice size.");
                        (channel == 0 ? volume.Color : volume.Normals).Write<byte>(data, TextureDataFormat.RGBA8Unorm,
                            new TextureRegion3D(0, 0, z, width, height, 1), level - first);
                        uploaded += data.Length;
                        yield return true;
                    }
            }
            if (uploaded != selection.PayloadBytes)
                throw new InvalidDataException("Canopy byte count mismatch.");
            var ambient = descriptor.GetProperty("ambient");
            if (ambient.GetProperty("encoding").GetString() != "upper-lower-hemisphere-visibility")
                throw new InvalidDataException("Invalid canopy ambient encoding.");
            var ambientLevels = ambient.GetProperty("levels");
            int aw = ambientLevels[0].GetProperty("size")[0].GetInt32();
            int ah = ambientLevels[0].GetProperty("size")[1].GetInt32();
            int ad = ambientLevels[0].GetProperty("size")[2].GetInt32();
            if (aw < 1 || ah < 1 || ad < 1 || Math.Max(aw, Math.Max(ah, ad)) > 64 || ambientLevels.GetArrayLength() > 7)
                throw new InvalidDataException("Invalid canopy ambient dimensions.");
            volume.Ambient = Own(graphics.CreateTexture(new TextureDescriptor(aw, ah, TextureFormat.RG8Unorm,
                dimension: TextureDimension.Texture3D, depth: ad, mipLevels: ambientLevels.GetArrayLength(),
                sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge))));
            yield return true;
            for (int mip = 0; mip < ambientLevels.GetArrayLength(); mip++)
            {
                var entry = ambientLevels[mip];
                int mw = Math.Max(1, aw >> mip), mh = Math.Max(1, ah >> mip), md = Math.Max(1, ad >> mip);
                if (!entry.GetProperty("size").EnumerateArray().Select(v => v.GetInt32()).SequenceEqual(new[] { mw, mh, md }))
                    throw new InvalidDataException("Invalid ambient mip dimensions.");
                string key = entry.GetProperty("path").GetString()!;
                if (!key.StartsWith("vegetation/prototype/", StringComparison.Ordinal) || key.Contains(".."))
                    throw new InvalidDataException("Invalid ambient block path.");
                while (!modelAssets.TryTake(key, out data)) yield return false;
                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
                if (data.Length != mw * mh * md * 2 || !hash.Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Canopy ambient checksum or size mismatch.");
                volume.Ambient.Write<byte>(data, TextureDataFormat.RG8Unorm, new TextureRegion3D(0, 0, 0, mw, mh, md), mip);
                volume.AmbientBytes += data.Length;
                yield return true;
            }
            if (volume.AmbientBytes != ambient.GetProperty("bytes").GetInt64())
                throw new InvalidDataException("Canopy ambient byte count mismatch.");
            if (prototypeQuad is null)
            {
                prototypeQuad = Own(graphics.CreateMesh3D());
                prototypeQuad.SetVertices([new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0),
                    new(-1, -1, 0), new(1, 1, 0), new(-1, 1, 0)]);
            }
            prototypeShader ??= LoadProgram("tree_volume.vert", "tree_volume.frag");
            prototypeShader.SetUniform("uShadowMaps", new[] { 9, 9, 9 });
            prototypeShader.SetUniform("uDynamicShadowMaps", new[] { 9, 9, 9 });
            volume.Ready = complete = true;
            Console.WriteLine($"TREE CANOPY {species} ready: {volume.Bytes / 1048576.0:F1} MiB; firstMip={first}");
        }
        finally
        {
            if (!complete)
                ReleasePrototypeTextures(volume);
        }
    }

    void ReleasePrototypeTextures(PrototypeVolume volume)
    {
        foreach (var texture in new[] { volume.Color, volume.Normals, volume.Ambient })
            if (texture is not null)
            {
                resources.Remove(texture);
                texture.Dispose();
            }
        volume.Levels = 0;
        volume.Color = volume.Normals = volume.Ambient = null;
        volume.AmbientBytes = 0;
        volume.Ready = false;
        volume.Bytes = 0;
    }
    void ClearTreePrototype()
    {
        prototypeUpload?.Dispose();
        prototypeUpload = null;
        foreach (var volume in prototypeVolumes.Values)
            ReleasePrototypeTextures(volume);
        prototypeVolumes.Clear();
        requestedCanopies.Clear();
        prototypeTrees = [];
    }

    void DrawPrototypeMeshes(RenderPass pass, ShaderProgram program, ViewFrustum frustum, bool depth)
    {
        if (!InspectTree)
            return;
        foreach (var tree in prototypeTrees)
        {
            if (modelBounds.TryGetValue(tree.Definition.LeafModel, out var bounds) && !frustum.Intersects(PrototypeBounds(tree, bounds.Min, bounds.Max)))
                continue;
            string leafModel = tree.Definition.LeafModel;
            string trunkModel = PrototypeTrunk(tree);
            if (!depth)
            {
                ApplyTreeLeafPalette(program, leafModel);
                program.SetUniform("uLeafOlive", World.Definition.Preset == "temperate" ? 1f : 0f);
            }
            using var transform = pass.PushModel(tree.Model);
            // The source mesh is a conservative rest-pose shadow/shelter proxy for the volume.
            foreach (string name in TreePrototype == "current" ? new[] { leafModel } : new[] { trunkModel, leafModel })
                foreach (var part in models.GetValueOrDefault(name, []))
                {
                    if (name == leafModel && TreePrototype != "current" && (!part.Leaf || TreePrototype == "volume" && !depth && prototypeVolumes.TryGetValue(tree.Species, out var volume) && volume.Ready))
                        continue;
                    program.SetUniform("uTreeMode", part.Leaf ? 2 : 1);
                    program.SetUniform("uTreeHeight", Math.Max(1, modelBounds[name].Max.Y));
                    program.SetUniform("uTreeVertices", 0);
                    program.SetUniform("uTreeLodRange", new Vector2(0, 1));
                    program.SetUniform("uFoliageCutoff", part.AlphaCutoff);
                    program.SetUniform("uHasModelNormal", !depth && part.normal is not null ? 1 : 0);
                    program.SetUniform("uHasMetallicRoughness", !depth && part.MetallicRoughness is not null ? 1 : 0);
                    using var normal = depth ? null : part.normal?.Bind(5);
                    using var material = depth ? null : part.MetallicRoughness?.Bind(6);
                    Mesh(pass, part.mesh, part.texture, program);
                }
        }
        program.SetUniform("uTreeMode", 0);
        program.SetUniform("uFoliageCutoff", 0f);
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
    }

    void DrawPrototypeVolume(RenderPass pass, Camera camera, Matrix4x4 inverse)
    {
        Array.Clear(CanopyLodCounts); CanopyCpuMilliseconds = 0;
        if (InspectTree ? TreePrototype != "volume" : !WorldVolumeTrees)
            return;
        if (prototypeShader is null)
            return;
        var program = prototypeShader;
        Lighting(program);
        GateLightingUniforms(program);
        var frustum = ViewFrustum.FromCamera(camera);
        List<(PrototypeTree Tree, float Coverage)> visible = [];
        if (InspectTree)
            visible.AddRange(prototypeTrees.Select(tree => (tree, 1f)));
        else
            foreach (var placed in Candidates(frustum).Trees)
                if (TryWorldCanopy(placed, out var tree, out float coverage))
                    visible.Add((tree, coverage));
        var cpu = System.Diagnostics.Stopwatch.StartNew();
        var viewProjection = camera.View * camera.Projection;
        program.SetUniform("uCanopy", 1);
        program.SetUniform("uCanopyNormals", 2);
        program.SetUniform("uCanopyAmbient", 3);
        program.SetUniform("uInverseViewProjection", inverse);
        program.SetUniform("uCanopyViewProjection", viewProjection);
        program.SetUniform("uViewPosition", camera.Position);
        program.SetUniform("uPrimaryStepCells", Math.Clamp(VolumeTreeSettings.StepCells, .35f, 1f));
        program.SetUniform("uOpacityThreshold", Math.Clamp(VolumeTreeSettings.OpacityThreshold, .1f, .8f));
        program.SetUniform("uTransmissionStrength", Math.Clamp(VolumeTreeSettings.Transmission, 0, .2f));
        program.SetUniform("uSkyFillFloor", Math.Clamp(VolumeTreeSettings.SkyFillFloor, 0, 1));
        program.SetUniform("uGroundFillWeight", Math.Clamp(VolumeTreeSettings.GroundFillWeight, 0, 1));
        program.SetUniform("uVolumeInspection", TreeVolumeInspection switch
        {
            "lit" => 0, "normals" => 1, "density" => 2, "sun" => 3, "sky" => 4, "ground" => 5, "albedo" => 6, "shadow" => 7,
            _ => throw new ArgumentException("--tree-volume-view expects lit, normals, density, sun, sky, ground, albedo or shadow.")
        });
        program.SetUniform("uCanopyTime", (float)weatherFrame.Time);
        program.SetUniform("uWindStrength", TreeSettings.WindStrength);
        using var gates = gateShadowAtlas?.DepthStencilAttachment.Bind(15);
        using var shadow = shadows.CurrentFrame.Bind(program, 9);
        foreach (var (tree, coverage) in visible.OrderBy(t => Vector3.DistanceSquared(t.Tree.Position, camera.Position)))
        {
            if (!prototypeVolumes.TryGetValue(tree.Species, out var volume) || !volume.Ready)
                continue;
            var bounds = PrototypeBounds(tree, volume.Minimum, volume.Maximum);
            if (!frustum.Intersects(bounds))
                continue;
            float pixels = CanopyLod.ProjectedHeight(bounds.Min, bounds.Max, camera.Position, viewProjection, graphics.Backbuffer.Height);
            var lod = CanopyLod.Select(pixels, volume.Levels, CanopyLodBaseline);
            CanopyLodCounts[lod.Band]++;
            program.SetUniform("uCanopyLod", new Vector4(lod.Mip, volume.Levels - 1, lod.MarchLimit,
                // Lighting quadrature must not slide through the crown as the
                // camera crosses a LOD band. Only primary visibility uses LOD.
                (float)Math.Clamp(VolumeTreeSettings.SunSteps, 1, 24)));
            var definition = tree.Definition;
            var rigid = Matrix4x4.CreateRotationY(tree.Yaw) * Matrix4x4.CreateTranslation(tree.Position);
            Matrix4x4.Invert(rigid, out var inverseRigid);
            program.SetUniform("uTreeWorld", rigid);
            program.SetUniform("uTreeInverse", inverseRigid);
            program.SetUniform("uCanopyRoot", tree.Position);
            program.SetUniform("uCanopyCoverage", coverage);
            program.SetUniform("uCanopyScale", tree.Scale);
            program.SetUniform("uScreenBounds", PrototypeScreenBounds(bounds, viewProjection));
            program.SetUniform("uCanopyMinimum", volume.Minimum * tree.Scale);
            program.SetUniform("uCanopyMaximum", volume.Maximum * tree.Scale);
            program.SetUniform("uCellSize", volume.Cell * tree.Scale);
            program.SetUniform("uDensityScale", definition.DensityScale);
            program.SetUniform("uDensityReferenceDistance", volume.DensityReference * tree.Scale);
            var wind = Vector3.TransformNormal(new Vector3(weatherFrame.Wind.X, 0, weatherFrame.Wind.Y), inverseRigid);
            program.SetUniform("uCanopyWind", new Vector2(wind.X, wind.Z));
            ApplyTreeLeafPalette(program, definition.LeafModel);
            using var color = volume.Color!.Bind(1);
            using var normal = volume.Normals!.Bind(2);
            using var ambientBinding = volume.Ambient!.Bind(3);
            Mesh(pass, prototypeQuad!, white, program);
        }
        CanopyCpuMilliseconds = cpu.Elapsed.TotalMilliseconds;
    }
}
