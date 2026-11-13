using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    readonly CookedAssets modelAssets;
    readonly Dictionary<string, IEnumerator<bool>> modelJobs = [];
    readonly HashSet<string> texturesLoading = [];
    long modelMeshBytes, modelTextureBytes, modelPreparingBytes;
    public long ModelGpuBytes => CableGpuBytes + modelMeshBytes + modelPreparingBytes + modelTextureBytes + refiningTextureBytes + prototypeBytes
        + (military?.GpuBytes ?? 0) + (rifle?.GpuBytes ?? 0) + (preparingCharacter?.GpuBytes ?? 0) + waterTextureBytes;
    public double ModelUploadPeakMilliseconds
    {
        get;
        private set;
    }
    public double ModelUploadMilliseconds
    {
        get;
        private set;
    }
    public double ModelReadMilliseconds => modelAssets.ReadMilliseconds;
    public double ModelDecodeMilliseconds => modelAssets.DecodeMilliseconds;
    public long ModelPreparedBytes => modelAssets.PreparedBytes;
    public int ModelPending => modelJobs.Count;
    public bool CookedModels => modelAssets.IsCooked;
    bool RequestModel(string name, int priority = 2)
    {
        var handle = Handle(name);
        handle.LastUsed = frameNumber;
        handle.Priority = Math.Min(handle.Priority, priority);
        if (name is "stargate" or "dhd") handle.Priority = 0;
        if (models.ContainsKey(name))
            return true;
        if (!modelJobs.ContainsKey(name))
        {
            handle.Request();
            modelJobs.Add(name, UploadModel(name).GetEnumerator());
        }
        return false;
    }
    void PumpModels(double milliseconds)
    {
        var clock = Stopwatch.StartNew();
        foreach (var name in modelJobs.Keys.OrderBy(n => Handle(n).Priority).ToArray())
        {
            var job = modelJobs[name];
            while (clock.Elapsed.TotalMilliseconds < milliseconds)
            {
                bool advanced;
                try { advanced = job.MoveNext(); }
                catch (Exception error) when (replacingModels.Remove(name))
                {
                    job.Dispose(); modelJobs.Remove(name);
                    Handle(name).Fail(error);
                    Console.Error.WriteLine("ASSETS replacement failed; retaining previous model " + name + ": " + error.Message);
                    break;
                }
                if (!advanced)
                {
                    job.Dispose();
                    modelJobs.Remove(name);
                    replacingModels.Remove(name);
                    shadows?.InvalidateAll();
                    InvalidateGateShadows();
                    break;
                }
                if (!job.Current)
                    break;
            }
            if (clock.Elapsed.TotalMilliseconds >= milliseconds)
                break;
        }
        while (foliageLoader is not null && clock.Elapsed.TotalMilliseconds < milliseconds)
        {
            if (!foliageLoader.MoveNext())
            {
                foliageLoader.Dispose();
                foliageLoader = null;
                break;
            }
            if (!foliageLoader.Current)
                break;
        }
        while (prototypeUpload is not null && clock.Elapsed.TotalMilliseconds < milliseconds)
        {
            if (!prototypeUpload.MoveNext())
            {
                prototypeUpload.Dispose();
                prototypeUpload = null;
                break;
            }
            if (!prototypeUpload.Current)
                break;
        }
        while (CommonReady && detailPreparation is not null && clock.Elapsed.TotalMilliseconds < milliseconds)
        {
            if (!detailPreparation.MoveNext())
            {
                detailPreparation.Dispose();
                detailPreparation = null;
                break;
            }
            if (!detailPreparation.Current) break;
        }
        foreach (string key in (modelJobs.Count == 0 && detailPreparation is null
            ? textureRefinements.Keys.Where(k => !ModelPressure || reducingTextures.Contains(k)).OrderByDescending(reducingTextures.Contains).ToArray() : []))
        {
            if (clock.Elapsed.TotalMilliseconds >= milliseconds) break;
            var job = textureRefinements[key];
            try
            {
                if (!job.MoveNext()) { job.Dispose(); textureRefinements.Remove(key); reducingTextures.Remove(key); }
            }
            catch (Exception error)
            {
                job.Dispose(); textureRefinements.Remove(key); reducingTextures.Remove(key);
                failedTextureRefinements.Add(key);
                StartupTrace.Mark("texture-refinement-failed", detail: key + ": " + error.Message);
            }
        }
        ModelUploadMilliseconds = clock.Elapsed.TotalMilliseconds;
        ModelUploadPeakMilliseconds = Math.Max(ModelUploadPeakMilliseconds, ModelUploadMilliseconds);
    }
    IEnumerable<bool> UploadModel(string name)
    {
        List<Mesh3D> ownedMeshes = [];
        bool completed = false;
        long ownedBytes = 0;
        try
        {
            byte[] data;
            while (!modelAssets.TryTake("models/" + name + ".json", out data))
                yield return false;
            var manifestWork = modelAssets.PrepareCpuAsync(() =>
            {
                using var parsed = JsonDocument.Parse(data);
                return parsed.RootElement.Clone();
            }, startupStop.Token);
            while (!manifestWork.IsCompleted)
                yield return false;
            var manifest = manifestWork.GetAwaiter().GetResult();
            if (manifest.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("Unsupported model version.");
            List<ModelPart> parts = [];
            var bounds = AxisAlignedBoundingBox.Empty;
            var baseBounds = AxisAlignedBoundingBox.Empty;
            foreach (var part in manifest.GetProperty("parts").EnumerateArray())
            {
                if (part.TryGetProperty("hidden", out var hidden) && hidden.GetBoolean())
                    continue;
                string meshName = "models/" + part.GetProperty("mesh").GetString();
                CookedAssets.StagingLease meshLease;
                while (!modelAssets.TryTakeLease(meshName, out meshLease)) yield return false;
                Mesh3D mesh;
                static Vector3 V(JsonElement v) => new(v[0].GetSingle(), v[1].GetSingle(), v[2].GetSingle());
                using (meshLease)
                {
                    byte[] meshData = meshLease.Bytes;
                    int count = MeshVertexCount(meshData);
                    mesh = Own(graphics.CreateMesh3D());
                    ownedMeshes.Add(mesh);
                    if (part.TryGetProperty("boundsMin", out var min))
                    {
                        bounds = bounds.Include(V(min)).Include(V(part.GetProperty("boundsMax")));
                        baseBounds = baseBounds.Include(V(part.GetProperty("baseMin"))).Include(V(part.GetProperty("baseMax")));
                    }
                    else
                    {
                        var work = modelAssets.PrepareCpuAsync(() => PackedMeshBounds(meshData), startupStop.Token);
                        while (!work.IsCompleted) yield return false;
                        var computed = work.GetAwaiter().GetResult();
                        bounds = bounds.Include(computed.Bounds.Min).Include(computed.Bounds.Max);
                        if (!computed.Base.IsEmpty) baseBounds = baseBounds.Include(computed.Base.Min).Include(computed.Base.Max);
                    }
                    ownedBytes += (long)count * 32;
                    modelPreparingBytes += (long)count * 32;
                    foreach (bool step in mesh.UploadPositionNormalUv(meshData, 8, count)) yield return step;
                }
                var wrap = part.TryGetProperty("repeat", out var repeat) && repeat.GetBoolean() ? TextureWrap.Repeat : TextureWrap.ClampToEdge;
                float cutoff = part.TryGetProperty("alphaCutoff", out var cut) ? cut.GetSingle() : 0;
                string TextureKey(string field) => part.GetProperty(field).GetString()!;
                string CacheKey(string field) => TextureKey(field) + "|" + wrap + "|" + field + "|" + (field == "texture" ? cutoff : 0);
                foreach (string field in new[] { "texture", "normal", "metallicRoughness", "emissionTexture" })
                {
                    if (!part.TryGetProperty(field, out _))
                        continue;
                    string cache = CacheKey(field);
                    if (textures.ContainsKey(cache))
                        continue;
                    if (texturesLoading.Contains(cache))
                    {
                        while (!textures.ContainsKey(cache))
                            yield return false;
                        continue;
                    }
                    texturesLoading.Add(cache);
                    try
                    {
                        foreach (bool step in UploadTexture(TextureKey(field), cache, wrap, alphaCutoff: field == "texture" ? cutoff : 0))
                            yield return step;
                    }
                    finally
                    {
                        texturesLoading.Remove(cache);
                    }
                }
                Texture? T(string field) => part.TryGetProperty(field, out _) ? textures[CacheKey(field)] : null;
                Vector3 emissionColor = part.TryGetProperty("emissionColor", out var ec) ? V(ec) : Vector3.Zero;
                Vector3? lightPosition = part.TryGetProperty("lightPosition", out var lp) ? V(lp) : null;
                string label = part.TryGetProperty("name", out var labelProperty) ? labelProperty.GetString()! : "";
                int chevron = name == "stargate" && label.Contains("Chevron") ? int.Parse(label[^2..]) : 0;
                bool leaf = part.TryGetProperty("surfaceRole", out var role) && role.GetString() == "leaf";
                parts.Add(new(mesh, T("texture")!, T("normal"), cutoff, T("emissionTexture"), emissionColor, part.TryGetProperty("emissionIntensity", out var ei) ? ei.GetSingle() : 0, lightPosition,
                    part.TryGetProperty("dhdKeyId", out var key) ? key.GetInt32() : 0, part.TryGetProperty("dhdOrb", out var orb) && orb.GetBoolean(), chevron, T("metallicRoughness"), leaf, part.TryGetProperty("heightScale", out var relief) ? relief.GetSingle() : 0, part.TryGetProperty("alphaMode", out var alphaMode) && alphaMode.GetString() == "BLEND"));
            }
            Handle(name).CpuReady();
            ReleaseModel(name);
            models.Add(name, parts);
            Handle(name).Publish(parts);
            modelAllocations[name] = ownedBytes;
            modelBounds[name] = bounds;
            modelBaseBounds[name] = baseBounds;
            modelMeshBytes += ownedBytes;
            completed = true;
        }
        finally
        {
            modelPreparingBytes -= ownedBytes;
            if (!completed)
                foreach (var mesh in ownedMeshes)
                {
                    resources.Remove(mesh);
                    mesh.Dispose();
                }
        }
    }
    internal static int MeshVertexCount(byte[] data)
    {
        if (data.Length < 8 || BitConverter.ToUInt32(data) != 0x314d4753)
            throw new InvalidDataException("Invalid mesh header.");
        int count = BitConverter.ToInt32(data, 4);
        if (count is <= 0 or > 2_000_000 || count % 3 != 0 || data.Length != 8L + count * 32L)
            throw new InvalidDataException("Invalid mesh size.");
        return count;
    }
    static (AxisAlignedBoundingBox Bounds, AxisAlignedBoundingBox Base) PackedMeshBounds(byte[] data)
    {
        int count = MeshVertexCount(data);
        var values = MemoryMarshal.Cast<byte, float>(data.AsSpan(8));
        var bounds = AxisAlignedBoundingBox.Empty;
        var bottom = AxisAlignedBoundingBox.Empty;
        for (int i = 0; i < count; i++)
        {
            var position = new Vector3(values[i * 8], values[i * 8 + 1], values[i * 8 + 2]);
            bounds = bounds.Include(position);
            if (position.Y < .75f) bottom = bottom.Include(position);
        }
        return (bounds, bottom);
    }
    internal static (Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs) ReadMesh(byte[] data)
    {
        int count = MeshVertexCount(data);
        var values = MemoryMarshal.Cast<byte, float>(data.AsSpan(8));
        Vector3[] p = new Vector3[count], n = new Vector3[count];
        Vector2[] uv = new Vector2[count];
        for (int i = 0; i < count; i++)
        {
            int j = i * 8;
            p[i] = new(values[j], values[j + 1], values[j + 2]);
            n[i] = new(values[j + 3], values[j + 4], values[j + 5]);
            uv[i] = new(values[j + 6], values[j + 7]);
        }
        return (p, n, uv);
    }
    IEnumerable<bool> UploadTexture(string file, string cache, TextureWrap wrap, string looseFolder = "models/", float alphaCutoff = 0)
    {
        if (!modelAssets.IsCooked)
        {
            if (alphaCutoff > 0)
            {
                foreach (bool step in UploadLeafMips(file, cache, wrap, alphaCutoff))
                    yield return step;
                yield break;
            }
            byte[] bytes;
            while (!modelAssets.TryTake(looseFolder + file, out bytes))
                yield return false;
            using var memory = new MemoryStream(bytes);
            using var image = System.Drawing.Image.FromStream(memory);
            textures.Add(cache, Own(graphics.CreateTextureFromImage(image, new TextureLoadOptions { MipLevels = 1 + (int)MathF.Log2(Math.Max(image.Width, image.Height)), Sampling = new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, wrap, wrap) })));
            modelTextureBytes += (long)image.Width * image.Height * 4 * 4 / 3;
            yield return true;
            yield break;
        }
        byte[] data;
        while (!modelAssets.TryTake(file, out data))
            yield return false;
        var descriptorBytes = data;
        var descriptorWork = modelAssets.PrepareCpuAsync(() =>
        {
            using var parsed = JsonDocument.Parse(descriptorBytes);
            return parsed.RootElement.Clone();
        }, startupStop.Token);
        while (!descriptorWork.IsCompleted)
            yield return false;
        var d = descriptorWork.GetAwaiter().GetResult();
        textureDefinitions[cache] = (d, wrap);
        failedTextureRefinements.Remove(cache);
        if (!d.TryGetProperty("rowOrder", out var rowOrder) || rowOrder.GetString() != "bottom-up")
            throw new InvalidDataException("Incompatible texture rows; recook models.sga.");
        int w = d.GetProperty("width").GetInt32(), h = d.GetProperty("height").GetInt32();
        var levels = d.GetProperty("mips").EnumerateArray().Select(v => v.GetString()!).ToArray();
        int target = 0;
        int maximum = Settings.Quality switch { GraphicsQuality.Low => 512, GraphicsQuality.High => 4096, _ => 2048 };
        while (target < levels.Length - 1 && Math.Max(w >> target, h >> target) > maximum) target++;
        int initial = target;
        while (initial < levels.Length - 1 && Math.Max(w >> initial, h >> initial) > 128) initial++;
        foreach (bool step in UploadTextureLevels(d, cache, wrap, initial, false)) yield return step;
        if (initial > target && !textureRefinements.ContainsKey(cache))
            textureRefinements[cache] = UploadTextureLevels(d, cache, wrap, target, true).GetEnumerator();
    }
    readonly Dictionary<string, IEnumerator<bool>> textureRefinements = [];
    readonly Dictionary<string, (JsonElement Descriptor, TextureWrap Wrap)> textureDefinitions = [];
    readonly HashSet<string> reducingTextures = [];
    readonly HashSet<string> failedTextureRefinements = [];
    long refiningTextureBytes;
    IEnumerable<bool> UploadTextureLevels(JsonElement d, string cache, TextureWrap wrap, int first, bool replace)
    {
        int width = d.GetProperty("width").GetInt32(), height = d.GetProperty("height").GetInt32();
        var levels = d.GetProperty("mips").EnumerateArray().Select(v => v.GetString()!).ToArray();
        TextureFormat format = TextureFormat.RGBA8Unorm;
        if (d.TryGetProperty("gpuFormat", out var gpu))
        {
            if (gpu.GetString() == "BC7" && graphics.Capabilities.SupportsBc7) format = TextureFormat.BC7Unorm;
            if (gpu.GetString() == "BC5" && graphics.Capabilities.SupportsBc5) format = TextureFormat.BC5Unorm;
            if (format != TextureFormat.RGBA8Unorm) levels = d.GetProperty("gpuMips").EnumerateArray().Select(v => v.GetString()!).ToArray();
        }
        // A legacy/fallback 4096 RGBA block plus its compressed input can exceed
        // staging capacity. Keep a complete smaller chain rather than deadlocking.
        while (first < levels.Length - 1 && !modelAssets.FitsStaging(levels[first])) first++;
        long plannedBytes = MipChainBytes(format, Math.Max(1, width >> first), Math.Max(1, height >> first), levels.Length - first);
        // Keep the complete resident mip chain when an optional upgrade cannot fit.
        // Residency schedules a fresh upgrade when there is headroom; leaving this
        // iterator pending forever also prevents capture readiness from completing.
        if (replace && !reducingTextures.Contains(cache) && ModelGpuBytes + plannedBytes > ModelBudgetBytes)
            yield break;
        float anisotropy = graphics.Capabilities.SupportsAnisotropy ? Math.Min(8, graphics.Capabilities.MaximumAnisotropy) : 1;
        var texture = Own(graphics.CreateTexture(new TextureDescriptor(Math.Max(1, width >> first), Math.Max(1, height >> first), format,
            mipLevels: levels.Length - first, sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, wrap, wrap, anisotropy: anisotropy))));
        long allocation = TextureBytes(texture);
        refiningTextureBytes += allocation;
        bool uploaded = false;
        try
        {
            yield return true;
            for (int level = levels.Length - 1; level >= first; level--)
            {
                CookedAssets.StagingLease lease;
                while (!modelAssets.TryTakeLease(levels[level], out lease)) yield return false;
                using var held = lease;
                byte[] data = lease.Bytes;
                int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
                if (format == TextureFormat.RGBA8Unorm)
                {
                    if (data.Length != w * h * 4) throw new InvalidDataException("Invalid cooked mip dimensions.");
                    foreach (bool step in UploadTextureRows(texture, data, w, h, level - first)) yield return step;
                }
                else
                {
                    int stride = ((w + 3) / 4) * 16;
                    if (data.Length != stride * ((h + 3) / 4)) throw new InvalidDataException("Invalid BC mip dimensions.");
                    int blockRows = Math.Max(1, 256 * 1024 / stride);
                    for (int y = 0; y < h; y += blockRows * 4)
                    {
                        int rows = Math.Min(blockRows * 4, h - y);
                        texture.WriteCompressed(data.AsSpan((y / 4) * stride, ((rows + 3) / 4) * stride), new TextureRegion(0, y, w, rows), level - first);
                        yield return true;
                    }
                }
            }
            if (replace && textures.TryGetValue(cache, out var old))
            {
                ReplaceTextureReferences(old, texture);
                modelTextureBytes -= TextureBytes(old);
                resources.Remove(old);
                old.Dispose();
            }
            textures[cache] = texture;
            modelTextureBytes += allocation;
            uploaded = true;
        }
        finally
        {
            refiningTextureBytes -= allocation;
            if (!uploaded) { resources.Remove(texture); texture.Dispose(); }
        }
    }
    static long TextureBytes(Texture texture) => MipChainBytes(texture.Format, texture.Width, texture.Height, texture.MipLevels);
    static long MipChainBytes(TextureFormat format, int width, int height, int mips) => Enumerable.Range(0, mips).Sum(level =>
        format is TextureFormat.BC7Unorm or TextureFormat.BC5Unorm
            ? (long)((Math.Max(1, width >> level) + 3) / 4) * ((Math.Max(1, height >> level) + 3) / 4) * 16
            : (long)Math.Max(1, width >> level) * Math.Max(1, height >> level) * 4);
    void ReplaceTextureReferences(Texture old, Texture next)
    {
        Texture? Swap(Texture? value) => ReferenceEquals(value, old) ? next : value;
        foreach (var parts in models.Values)
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                parts[i] = p with { texture = Swap(p.texture)!, normal = Swap(p.normal), Emission = Swap(p.Emission), MetallicRoughness = Swap(p.MetallicRoughness) };
            }
        foreach (var asset in characterAssets.Values.Cast<SkeletalAsset?>().Append(rifle).Distinct())
            if (asset is not null)
                for (int i = 0; i < asset.Parts.Count; i++)
                {
                    var p = asset.Parts[i];
                    asset.Parts[i] = p with { Albedo = Swap(p.Albedo)!, Normal = Swap(p.Normal), MetallicRoughness = Swap(p.MetallicRoughness) };
                }
        shadows?.InvalidateAll();
    }
    static IEnumerable<bool> UploadTextureRows(Texture texture, byte[] data, int width, int height, int level)
    {
        int rows = Math.Max(1, 256 * 1024 / (width * 4));
        for (int y = 0; y < height; y += rows)
        {
            int count = Math.Min(rows, height - y);
            texture.Write<byte>(data.AsSpan(y * width * 4, count * width * 4), TextureDataFormat.RGBA8Unorm,
                new TextureRegion(0, y, width, count), new TextureSubresource(level));
            yield return true;
        }
    }
    Texture SharedCharacterTexture(string file)
    {
        string cache = file + "|character";
        if (!textures.TryGetValue(cache, out var texture))
        {
            if (modelAssets.IsCooked)
            {
                using var job = UploadTexture(file, cache, TextureWrap.ClampToEdge).GetEnumerator();
                while (job.MoveNext())
                    if (!job.Current)
                        Thread.Sleep(1);
                texture = textures[cache];
            }
            else
            {
                using var image = System.Drawing.Image.FromFile(Asset("characters", file));
                texture = Own(graphics.CreateTextureFromImage(image, new TextureLoadOptions { MipLevels = 1 + (int)MathF.Log2(Math.Max(image.Width, image.Height)), Sampling = new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear) }));
                textures.Add(cache, texture);
            }
        }
        return texture;
    }
}
