using System.Numerics;
using System.Text.Json;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed record TreeRenderSettings
{
    public Vector3 LeafTint { get; init; } = new(.62f, .50f, .38f);
    public float LeafSaturation { get; init; } = .72f;
    public float WindStrength
    {
        get;
        init;
    } = .12f;
    public float LeafTransmission
    {
        get;
        init;
    } = .10f;
    public float Hysteresis
    {
        get;
        init;
    } = .10f;
    public static float Threshold(int index) => index switch { 0 => 256, 1 => 112, _ => 56 };
    public static int Level(float pixels, int count)
    {
        int level = 0;
        while (level < count - 1 && pixels < Threshold(level))
            level++;
        return level;
    }
}
sealed partial class SceneRenderer
{
    public TreeRenderSettings TreeSettings
    {
        get;
        set;
    } = new();
    readonly Dictionary<string, string[]> treeVariants;
    float treeFocalPixels = 800;
    Vector3 treeCamera;
    string[] TreeModels(string source)
    {
        string key = TreeVariant(source);
        return treeVariants[key];
    }
    float TreePixels(TreeInstance tree) => Math.Max(.1f, tree.Max.Y - tree.Min.Y) * treeFocalPixels / Math.Max(.5f, Vector3.Distance(treeCamera, tree.Transform.Translation));
    bool RequestScenery(IReadOnlyList<RockInstance> rocks, IReadOnlyList<TreeInstance> trees, bool requireDetailed = false)
    {
        bool ready = true;
        foreach (var model in rocks.Select(r => r.Model).Distinct())
            ready &= RequestModel(model);
        foreach (var group in trees.GroupBy(t => t.Model))
        {
            var names = TreeModels(group.Key);
            // A cheap crown arrives first; detailed representations can follow without a hole.
            ready &= RequestModel(names[^1], 1);
            foreach (var t in group)
            {
                if (UsesWorldCanopy(t))
                {
                    bool canopyReady = RequestWorldCanopy(t);
                    if (requireDetailed && Vector3.DistanceSquared(treeCamera, t.Transform.Translation) < 64 * 64)
                        ready &= canopyReady;
                    continue;
                }
                int level = ModelPressure ? names.Length - 1 : TreeRenderSettings.Level(TreePixels(t), names.Length);
                bool detail = RequestModel(names[level], 3);
                bool canopy = RequestWorldCanopy(t);
                if (requireDetailed && Vector3.DistanceSquared(treeCamera, t.Transform.Translation) < 64 * 64)
                    ready &= detail && canopy;
            }
        }
        return !requireDetailed || ready;
    }
    string ReadyTreeModel(TreeInstance tree, int level)
    {
        var names = TreeModels(tree.Model);
        RequestModel(names[level], 3);
        if (models.ContainsKey(names[level]))
            return names[level];
        for (int i = names.Length - 1; i >= 0; i--)
            if (models.ContainsKey(names[i]))
                return names[i];
        return names[^1];
    }
    readonly Dictionary<string, List<(Matrix4x4 Transform, Vector2 Range)>> treeDrawGroups = [];
    readonly Matrix4x4[] treeRanges = new Matrix4x4[8];
    void DrawTrees(RenderPass pass, ShaderProgram program, ViewFrustum frustum, bool depth)
    {
        program.SetUniform("uRockSnow", 0);
        program.SetUniform("uRoughness", .9f);
        program.SetUniform("uTint", Vector3.One);
        program.SetUniform("uTreeTime", (float)weatherFrame.Time);
        program.SetUniform("uTreeWind", weatherFrame.Wind);
        program.SetUniform("uTreeWindStrength", depth && !pointShadowPass ? 0 : TreeSettings.WindStrength);
        program.SetUniform("uLeafTransmission", TreeSettings.LeafTransmission);
        program.SetUniform("uLeafOlive", World.Definition.Preset == "temperate" ? 1f : 0f);
        foreach (var group in treeDrawGroups.Values)
            group.Clear();
        if (!depth) VisibleVolumeTrees = VisibleLegacyLeafTrees = 0;
        foreach (var tree in Candidates(frustum).Trees)
        {
            float canopyCoverage = 0;
            if (!depth && UsesWorldCanopy(tree))
            {
                RequestWorldCanopy(tree);
                var canopyTree = WorldVolumeTree(tree, worldCanopies[TreeVariant(tree.Model)]);
                string? trunk = ReadyWorldTrunk(canopyTree);
                if (trunk is not null)
                {
                    if (!treeDrawGroups.TryGetValue(trunk, out var wood))
                        treeDrawGroups[trunk] = wood = [];
                    wood.Add((tree.Transform, new(0, 1)));
                    treeLods[tree.Id] = trunk;
                }
                if (TryWorldCanopy(tree, out _, out _)) VisibleVolumeTrees++;
                // A loading crown is temporarily absent; never switch to old leaf cards.
                continue;
            }
            if (canopyCoverage >= 1)
                continue;
            var names = TreeModels(tree.Model);
            float pixels = ModelPressure || depth && UsesWorldCanopy(tree) ? 0 : TreePixels(tree);
            int selected = TreeRenderSettings.Level(pixels, names.Length);
            string name = ReadyTreeModel(tree, selected);
            int old = treeLods.TryGetValue(tree.Id, out var last) ? Array.IndexOf(names, last) : -1;
            if (old >= 0 && old != selected)
            {
                float threshold = TreeRenderSettings.Threshold(Math.Min(old, selected));
                if (Math.Abs(pixels - threshold) < threshold * TreeSettings.Hysteresis)
                    selected = old;
            }
            if (old >= 0 && old != selected && !depth)
            {
                shadows.InvalidateStaticCaster(new AxisAlignedBoundingBox(tree.Min - new Vector3(.6f), tree.Max + new Vector3(.6f)));
                InvalidateGateShadows();
            }
            if (!depth)
                treeLods[tree.Id] = names[selected];
            int first = selected, second = -1;
            float fraction = 0;
            if (!depth)
                for (int edge = 0; edge < names.Length - 1; edge++)
                {
                    float threshold = TreeRenderSettings.Threshold(edge);
                    if (pixels >= threshold * .9f && pixels <= threshold * 1.1f)
                    {
                        first = edge;
                        second = edge + 1;
                        fraction = Math.Clamp((threshold * 1.1f - pixels) / (threshold * .2f), 0, 1);
                        break;
                    }
                }
            for (int draw = 0; draw < (second >= 0 ? 2 : 1); draw++)
            {
                string desired = names[draw == 0 ? first : second];
                RequestModel(desired);
                bool pair = second >= 0 && models.ContainsKey(names[first]) && models.ContainsKey(names[second]);
                if (draw == 1 && !pair)
                    break;
                name = pair ? desired : ReadyTreeModel(tree, selected);
                if (!models.ContainsKey(name))
                    continue;
                if (!treeDrawGroups.TryGetValue(name, out var group))
                    treeDrawGroups[name] = group = [];
                var range = pair ? (draw == 0 ? new Vector2(fraction, 1) : new Vector2(0, fraction)) : new Vector2(0, 1);
                range = new Vector2(canopyCoverage) + range * (1 - canopyCoverage);
                group.Add((tree.Transform, range));
            }
            if (!depth && models.TryGetValue(name, out var visibleParts) && visibleParts.Any(p => p.Leaf)) VisibleLegacyLeafTrees++;
        }
        foreach (var (name, group) in treeDrawGroups)
        {
            if (group.Count == 0)
                continue;
            if (!depth) ApplyTreeLeafPalette(program, name);
            program.SetUniform("uTreeHeight", Math.Max(1, modelBounds[name].Max.Y));
            program.SetUniform("uTreeVertices", -1);
            foreach (var part in models[name])
            {
                program.SetUniform("uTreeMode", part.Leaf ? 2 : 1);
                program.SetUniform("uFoliageCutoff", part.AlphaCutoff);
                program.SetUniform("uHasModelNormal", !depth && part.normal is not null ? 1 : 0);
                program.SetUniform("uHasMetallicRoughness", !depth && part.MetallicRoughness is not null ? 1 : 0);
                program.SetUniform("uMetallicRoughness", 6);
                using var normal = depth ? null : part.normal?.Bind(5);
                using var mr = depth ? null : part.MetallicRoughness?.Bind(6);
                for (int offset = 0; offset < group.Count; offset += 8)
                {
                    int count = Math.Min(8, group.Count - offset);
                    for (int i = 0; i < count; i++)
                    {
                        treeTransforms[i] = group[offset + i].Transform;
                        treeRanges[i] = Matrix4x4.Identity;
                        treeRanges[i].M11 = group[offset + i].Range.X;
                        treeRanges[i].M12 = group[offset + i].Range.Y;
                    }
                    program.SetUniform("uTreeTransforms", treeTransforms.AsSpan());
                    program.SetUniform("uTreeParameters", treeRanges.AsSpan());
                    pass.DrawMeshInstances(part.mesh, count, depth && part.AlphaCutoff == 0 ? white : part.texture, program);
                    DrawCalls++;
                }
            }
        }
        program.SetUniform("uTreeVertices", 0);
        program.SetUniform("uTreeMode", 0);
        program.SetUniform("uTreeLodRange", new Vector2(0, 1));
        program.SetUniform("uFoliageCutoff", 0f);
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
    }
}
