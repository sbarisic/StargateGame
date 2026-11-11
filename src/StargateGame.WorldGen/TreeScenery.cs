using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

public sealed record TreeInstance(ulong Id, string Model, Matrix4x4 Transform, Vector3 Min, Vector3 Max);

/// <summary>Immutable trees selected from server-owned candidates. Trees stay upright on slopes.</summary>
public static class TreeScenery
{
    public const int Version = PlanetSettings.SceneryVersion;
    // Additional world-size adjustment, on top of the existing import calibration.
    public const float SizeMultiplier = 1.5f;
    sealed record TreeBounds(Vector3 Min, Vector3 Max, string[] Lods, float[] LodDistances);
    static readonly IReadOnlyDictionary<string, TreeBounds> bounds = LoadBounds();
    public static IEnumerable<string> Models => bounds.Keys.Order(StringComparer.Ordinal);
    public static IEnumerable<string> RenderModels => Models.SelectMany(n => new[] { n }.Concat(bounds[n].Lods));
    static IReadOnlyDictionary<string, TreeBounds> LoadBounds()
    {
        static Vector3 V(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        Dictionary<string, TreeBounds> result = new(StringComparer.Ordinal);
        // Tree Pack 3D is retired; retain its files only as source/import history.
        foreach (var resource in new[] { "TreeBounds", "RealisticTreeBounds" })
        {
            using var stream = typeof(TreeScenery).Assembly.GetManifestResourceStream(resource)!;
            using var json = JsonDocument.Parse(stream);
            foreach (var p in json.RootElement.EnumerateObject())
            {
                var lods = p.Value.GetProperty("lods").EnumerateArray().Select(e => e.GetString()!).ToArray();
                var distances = p.Value.TryGetProperty("lodDistances", out var ranges)
                    ? ranges.EnumerateArray().Select(e => e.GetSingle()).ToArray()
                    : new[] { 80f, 180f }.Take(lods.Length).ToArray();
                if (distances.Length != lods.Length || distances.Any(d => !float.IsFinite(d) || d <= 0) ||
                    distances.Zip(distances.Skip(1)).Any(pair => pair.First >= pair.Second))
                    throw new InvalidDataException("Invalid tree LOD distances: " + p.Name);
                result.Add(p.Name, new(V(p.Value.GetProperty("min")), V(p.Value.GetProperty("max")), lods, distances));
            }
        }
        return result;
    }
    public static string RenderModel(string model, float distance)
    {
        var tree = bounds[model];
        for (int i = tree.Lods.Length - 1; i >= 0; i--)
            if (distance > tree.LodDistances[i])
                return tree.Lods[i];
        return model;
    }
    public static SceneryCatalogue Catalogue(string preset, string? fixture = null)
    {
        List<SceneryAsset> assets = [];
        void Add(string name, float min, float max) => assets.Add(new(name, "tree", name, 1, min * 1.5f * SizeMultiplier, max * 1.5f * SizeMultiplier, 26, name));
        if (preset == "temperate" && fixture == "gate-pair")
        {
            foreach (var name in new[] { "RealisticTree_01", "RealisticTree_02", "MoreRealisticTree_01", "MoreRealisticTree_02" })
                Add(name, .85f, 1.2f);
            return new(1, assets.ToArray());
        }
        switch (preset)
        {
            case "temperate":
                Add("Leave-Tree-01a", .6f, .9f);
                Add("Broadleaf_Desktop", .8f, 1.2f);
                foreach (var name in new[] { "RealisticTree_01", "RealisticTree_02", "MoreRealisticTree_01", "MoreRealisticTree_02" })
                    Add(name, .85f, 1.2f);
                break;
            case "icy":
                foreach (var name in new[] { "BlueSpruce_Desktop_Winter", "BlueSpruce_Desktop_Winter2", "BlueSpruce_Desktop_Winter3" })
                    Add(name, .7f, 1.05f);
                Add("European_White_Birch_Hero_Winter", .65f, .95f);
                break;
            case "desert":
                Add("Broadleaf_Desktop", .7f, 1.2f);
                break;
            default:
                throw new ArgumentException("Unknown tree preset.");
        }
        return new(1, assets.ToArray());
    }
    public static IReadOnlyList<TreeInstance> Resolve(TerrainChunkData chunk, string preset, string? fixture = null)
    {
        List<TreeInstance> result = [];
        var rocks = RockScenery.Resolve(chunk, preset);
        foreach (var instance in SceneryPlacement.Resolve(chunk, Catalogue(preset, fixture)))
        {
            var candidate = instance.Candidate;
            var b = bounds[instance.Model];
            float scale = instance.Scale * (candidate.Scale < .7f ? candidate.Scale : 1);
            var p = candidate.Position;
            int x = (int)p.X - chunk.Coordinate.X * 64, z = (int)p.Z - chunk.Coordinate.Z * 64;
            // Fixed world-space planting depths below the ground at the trunk.
            // The new meshes already have a trunk-base pivot; the recovered leafy
            // tree has a tall root flare. Neither scale nor downhill neighbours
            // should add more sinking to these authored offsets.
            float rootDepth = instance.Model switch
            {
                "Leave-Tree-01a" => 1.5f,
                "RealisticTree_01" or "RealisticTree_02" => .15f,
                _ => .10f
            };
            p.Y = chunk.Sample(p.X, p.Z) - rootDepth;
            var transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(candidate.Yaw) * Matrix4x4.CreateTranslation(p);
            var corners = Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), transform)).ToArray();
            var min = corners.Aggregate(Vector3.Min);
            var max = corners.Aggregate(Vector3.Max);
            // The root owns the tree; canopy bounds may cross chunk edges. Requiring
            // the entire crown to fit left a treeless strip at every chunk seam.
            // Roots stay dry, while foliage can naturally overhang river banks.
            bool dry = true;
            for (int iz = z - 1; iz <= z + 1; iz++)
                for (int ix = x - 1; ix <= x + 1; ix++)
                    if (float.IsFinite(chunk.WaterHeight(ix, iz)))
                        dry = false;
            if (!dry)
                continue;
            float radius = instance.Model == "Leave-Tree-01a" ? 2 * scale : .75f * scale;
            if (rocks.Any(r => r.Collidable && p.X + radius > r.Min.X && p.X - radius < r.Max.X && p.Z + radius > r.Min.Z && p.Z - radius < r.Max.Z))
                continue;
            result.Add(new(candidate.Id, instance.Model, transform, min, max));
        }
        return result;
    }
}
