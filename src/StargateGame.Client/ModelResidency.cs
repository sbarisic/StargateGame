using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    readonly Dictionary<string, AssetHandle<IReadOnlyList<ModelPart>>> modelHandles = [];
    public long ModelBudgetBytes => (Settings.Quality switch { GraphicsQuality.Low => 256L, GraphicsQuality.High => 1024L, _ => 512L }) * 1024 * 1024;
    bool ModelPressure => ModelGpuBytes >= ModelBudgetBytes;
    AssetHandle<IReadOnlyList<ModelPart>> Handle(string name)
    {
        if (!modelHandles.TryGetValue(name, out var handle)) modelHandles[name] = handle = new("models/" + name + ".json");
        return handle;
    }
    void MaintainModelResidency()
    {
        if (attachedWorld is null) return;
        foreach (var entity in testingInspection) RequestModel(entity.Model, 1);
        Dictionary<string, int> references = [];
        void Reference(string name) => references[name] = references.GetValueOrDefault(name) + 1;
        if (buildingBaseMesh is not null) Reference("kit-stone-foundation");
        foreach (var entity in World.Definition.Entities.Concat(testingInspection)) Reference(entity.Model);
        foreach (var prop in World.Definition.Props) Reference(prop.Model);
        foreach (var piece in buildingPrefabs) Reference(piece.Model);
        foreach (var chunk in resident.Values)
        {
            foreach (var rock in chunk.Rocks) Reference(rock.Model);
            foreach (var tree in chunk.Trees) Reference(TreeModels(tree.Model)[^1]);
        }
        var liveTrees = resident.Values.SelectMany(c => c.Trees).Select(t => t.Id).ToHashSet();
        foreach (ulong id in treeLods.Keys.Where(id => !liveTrees.Contains(id)).ToArray()) treeLods.Remove(id);
        foreach (string name in treeLods.Values) Reference(name);
        foreach (var (name, handle) in modelHandles)
            handle.References = references.GetValueOrDefault(name) + (modelJobs.ContainsKey(name) ? 1 : 0);
        foreach (string name in models.Keys.OrderBy(n => Handle(n).LastUsed).ToArray())
        {
            var handle = Handle(name);
            if (handle.References != 0 || frameNumber - handle.LastUsed < 120) continue;
            // A small warm cache is useful for travel; surplus unreferenced assets are reclaimable.
            if (!ModelPressure && frameNumber - handle.LastUsed < 1800) continue;
            ReleaseModel(name);
            handle.Evict();
        }
        if (modelJobs.Count != 0 || detailPreparation is not null || texturesLoading.Count != 0) return;
        var used = new HashSet<Texture>();
        void Use(Texture? texture) { if (texture is not null) used.Add(texture); }
        foreach (var part in models.Values.SelectMany(p => p))
        {
            Use(part.texture); Use(part.normal); Use(part.Emission); Use(part.MetallicRoughness);
        }
        foreach (var asset in characterAssets.Values.Cast<SkeletalAsset?>().Append(rifle).Distinct())
            if (asset is not null) foreach (var part in asset.Parts) { Use(part.Albedo); Use(part.Normal); Use(part.MetallicRoughness); }
        foreach (string key in textures.Keys.Where(k => !used.Contains(textures[k])).ToArray())
        {
            if (textureRefinements.Remove(key, out var refining)) refining.Dispose();
            var texture = textures[key]; textures.Remove(key);
            textureDefinitions.Remove(key); reducingTextures.Remove(key);
            failedTextureRefinements.Remove(key);
            modelTextureBytes -= TextureBytes(texture);
            resources.Remove(texture); texture.Dispose();
        }
        if (ModelPressure)
        {
            // Do not strand a half-uploaded refinement and its staging lease under pressure.
            bool cancelled = false;
            foreach (string key in textureRefinements.Keys.Where(k => !reducingTextures.Contains(k)).ToArray())
            {
                textureRefinements[key].Dispose(); textureRefinements.Remove(key);
                cancelled = true;
            }
            if (cancelled) modelAssets.CancelPending();
            if (!InspectTree && prototypeUpload is null)
                foreach (var (key, volume) in prototypeVolumes.Where(p => p.Value.Ready).ToArray())
                {
                    if (!ModelPressure) break;
                    // World crowns are essential visuals. Reduce ordinary texture detail
                    // below instead of permanently returning these trees to leaf cards.
                    if (requestedCanopies.ContainsKey(key)) continue;
                    ReleasePrototypeTextures(volume);
                }
            if (ModelPressure && reducingTextures.Count == 0)
            {
                string? key = textures.Keys.Where(k => textureDefinitions.ContainsKey(k)
                    && !failedTextureRefinements.Contains(k)
                    && Math.Max(textures[k].Width, textures[k].Height) > 128)
                    .OrderByDescending(k => TextureBytes(textures[k])).FirstOrDefault();
                if (key is not null)
                {
                    var definition = textureDefinitions[key];
                    int width = definition.Descriptor.GetProperty("width").GetInt32();
                    int height = definition.Descriptor.GetProperty("height").GetInt32();
                    int first = 0, count = definition.Descriptor.GetProperty("mips").GetArrayLength();
                    while (first < count - 1 && Math.Max(width >> first, height >> first) > 128) first++;
                    reducingTextures.Add(key);
                    textureRefinements[key] = UploadTextureLevels(definition.Descriptor, key, definition.Wrap, first, true).GetEnumerator();
                }
            }
        }
        else if (textureRefinements.Count == 0)
        {
            // Raising quality or reclaiming unused models can make refinement affordable again.
            int maximum = Settings.Quality switch { GraphicsQuality.Low => 512, GraphicsQuality.High => 4096, _ => 2048 };
            foreach (var (key, definition) in textureDefinitions)
            {
                if (failedTextureRefinements.Contains(key)) continue;
                if (!textures.TryGetValue(key, out var current)) continue;
                int width = definition.Descriptor.GetProperty("width").GetInt32();
                int height = definition.Descriptor.GetProperty("height").GetInt32();
                var levels = definition.Descriptor.GetProperty(current.Format is TextureFormat.BC5Unorm or TextureFormat.BC7Unorm ? "gpuMips" : "mips");
                int first = 0;
                while (first < levels.GetArrayLength() - 1 && (Math.Max(width >> first, height >> first) > maximum
                    || !modelAssets.FitsStaging(levels[first].GetString()!))) first++;
                int w = Math.Max(1, width >> first), h = Math.Max(1, height >> first);
                if (w <= current.Width && h <= current.Height) continue;
                long allocation = MipChainBytes(current.Format, w, h, levels.GetArrayLength() - first);
                if (ModelGpuBytes + allocation > ModelBudgetBytes * .9) continue;
                textureRefinements[key] = UploadTextureLevels(definition.Descriptor, key, definition.Wrap, first, true).GetEnumerator();
                break;
            }
        }
        var unrefined = textureRefinements.Keys.Where(textures.ContainsKey).Select(k => textures[k]).ToHashSet();
        foreach (var (name, parts) in models)
            if (!parts.Any(p => unrefined.Contains(p.texture) || p.normal is not null && unrefined.Contains(p.normal)
                || p.Emission is not null && unrefined.Contains(p.Emission) || p.MetallicRoughness is not null && unrefined.Contains(p.MetallicRoughness)))
                Handle(name).Refine();
    }
}
