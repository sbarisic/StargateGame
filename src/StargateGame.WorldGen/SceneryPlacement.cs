using StargateGame.Shared;

namespace StargateGame.WorldGen;

public sealed record SceneryInstance(PlacementCandidate Candidate, string AssetId, string Model, string? CollisionProxy, float Scale);

/// <summary>Catalogue changes select models without resampling or changing terrain.</summary>
public static class SceneryPlacement
{
    public static IReadOnlyList<SceneryInstance> Resolve(TerrainChunkData chunk, SceneryCatalogue catalogue)
    {
        if (catalogue.Version != 1 || catalogue.Assets.Select(a => a.Id).Distinct().Count() != catalogue.Assets.Length ||
           catalogue.Assets.Any(a => string.IsNullOrWhiteSpace(a.Id) || a.Category is not ("tree" or "rock") ||
               !float.IsFinite(a.Density) || a.Density is < 0 or > 1 || !float.IsFinite(a.MinScale) || !float.IsFinite(a.MaxScale) ||
               a.MinScale <= 0 || a.MaxScale < a.MinScale || !float.IsFinite(a.MaxSlope) || a.MaxSlope is < 0 or > 90))
            throw new ArgumentException("Invalid scenery catalogue.");
        List<SceneryInstance> result = [];
        foreach (var candidate in chunk.Placements.OrderBy(c => c.Id))
        {
            var assets = catalogue.Assets.Where(a => a.Category == candidate.Category).OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
            if (assets.Length == 0)
                continue;
            var asset = assets[(int)((candidate.Id >> 8) % (ulong)assets.Length)];
            if ((candidate.Id & 65535) / 65536f >= asset.Density)
                continue;
            var normal = chunk.Normal((int)candidate.Position.X - chunk.Coordinate.X * 64, (int)candidate.Position.Z - chunk.Coordinate.Z * 64);
            if (MathF.Acos(Math.Clamp(normal.Y, -1, 1)) * 180 / MathF.PI > asset.MaxSlope)
                continue;
            float scale = asset.MinScale + (asset.MaxScale - asset.MinScale) * ((candidate.Id >> 32) & 65535) / 65535f;
            result.Add(new(candidate, asset.Id, asset.Model, asset.CollisionProxy, scale));
        }
        return result;
    }
}
