using System.Numerics;
using System.Text.Json;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool WorldVolumeTrees { get; set; } = true;
    readonly Dictionary<string, TreePrototypeDefinition> worldCanopies;
    readonly Dictionary<string, TreePrototypeDefinition> requestedCanopies = [];
    public int WorldCanopiesReady => prototypeVolumes.Values.Count(volume => volume.Ready);
    public int WorldCanopiesPending => requestedCanopies.Keys.Count(key => !prototypeVolumes.TryGetValue(key, out var volume) || !volume.Ready);
    public int VisibleVolumeTrees { get; private set; }
    public int VisibleLegacyLeafTrees { get; private set; }

    string TreeVariant(string source) => source +
        (source == "European_White_Birch_Hero_Winter" && World.Definition.Preset == "temperate" ? "-summer" : "");

    // All foliage-bearing world trees use volumes, including distant trees.
    // Placement and authoritative collision remain unchanged.
    bool UsesWorldCanopy(TreeInstance tree) => WorldVolumeTrees && !InspectTree
        && worldCanopies.ContainsKey(TreeVariant(tree.Model));
    bool RequestWorldCanopy(TreeInstance tree)
    {
        if (!WorldVolumeTrees || InspectTree)
            return true;
        // Broadleaf_Desktop is a bare trunk, so it has no canopy to replace.
        if (!worldCanopies.TryGetValue(TreeVariant(tree.Model), out var definition))
            return true;
        requestedCanopies.TryAdd(definition.Canopy, definition);
        var instance = WorldVolumeTree(tree, definition);
        bool ready = RequestModel(PrototypeTrunk(instance));
        return ready && prototypeVolumes.TryGetValue(definition.Canopy, out var volume) && volume.Ready;
    }

    static PrototypeTree WorldVolumeTree(TreeInstance tree, TreePrototypeDefinition definition)
    {
        if (!Matrix4x4.Decompose(tree.Transform, out var scale, out var rotation, out var position))
            throw new InvalidDataException("Invalid tree placement transform.");
        var forward = Vector3.Transform(Vector3.UnitZ, rotation);
        return new(definition.Canopy, position, MathF.Atan2(forward.X, forward.Z), scale.X, definition);
    }

    bool TryWorldCanopy(TreeInstance tree, out PrototypeTree instance, out float coverage)
    {
        instance = null!;
        coverage = 0;
        if (!WorldVolumeTrees || InspectTree)
            return false;
        if (!worldCanopies.TryGetValue(TreeVariant(tree.Model), out var definition))
            return false;
        if (!prototypeVolumes.TryGetValue(definition.Canopy, out var volume) || !volume.Ready)
            return false;
        instance = WorldVolumeTree(tree, definition);
        if (ReadyWorldTrunk(instance) is null)
            return false;
        coverage = 1;
        return true;
    }

    string? ReadyWorldTrunk(PrototypeTree tree)
    {
        string desired = PrototypeTrunk(tree);
        if (models.ContainsKey(desired)) return desired;
        for (int level = tree.Definition.TrunkLevels - 1; level >= 0; level--)
            if (models.ContainsKey(tree.Definition.TrunkPrefix + level)) return tree.Definition.TrunkPrefix + level;
        return null;
    }

    void StartWorldCanopyUploads()
    {
        if (prototypeUpload is null && requestedCanopies.Keys.Any(key => !prototypeVolumes.ContainsKey(key)))
            prototypeUpload = UploadWorldCanopies().GetEnumerator();
    }

    IEnumerable<bool> UploadWorldCanopies()
    {
        // Requests can grow while the iterator is suspended. Do not retain a dictionary enumerator.
        while (requestedCanopies.FirstOrDefault(pair => !prototypeVolumes.ContainsKey(pair.Key)) is var next && next.Key is not null)
        {
            var volume = new PrototypeVolume();
            prototypeVolumes.Add(next.Key, volume);
            foreach (bool uploaded in UploadTreePrototype(next.Key, next.Value, volume))
                yield return uploaded;
        }
    }
}
