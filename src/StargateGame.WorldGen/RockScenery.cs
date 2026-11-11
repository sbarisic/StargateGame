using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

public sealed record RockInstance(ulong Id, string Model, Matrix4x4 Transform, Vector3 Min, Vector3 Max, bool Collidable);

/// <summary>Deterministic selection from server-delivered candidates; never changes terrain or its cache.</summary>
public static class RockScenery
{
    public const int Version = 1;
    static readonly IReadOnlyDictionary<string, (Vector3 Min, Vector3 Max)> bounds = LoadBounds();
    public static IEnumerable<string> Models => bounds.Keys.Order(StringComparer.Ordinal);
    static IReadOnlyDictionary<string, (Vector3, Vector3)> LoadBounds()
    {
        using var stream = typeof(RockScenery).Assembly.GetManifestResourceStream("RockBounds")!;
        using var json = JsonDocument.Parse(stream);
        static Vector3 V(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (V(p.Value.GetProperty("min")), V(p.Value.GetProperty("max"))), StringComparer.Ordinal);
    }
    public static SceneryCatalogue Catalogue(string preset)
    {
        List<SceneryAsset> assets = [];
        void Add(string name, float min, float max, float slope = 28) => assets.Add(new(name, "rock", name, 1, min, max, slope, name));
        switch (preset)
        {
            case "temperate":
                Add("Stone_01", .65f, 1.05f);
                Add("Stone_02", .8f, 1.5f);
                Add("Stone_03", .55f, .8f);
                Add("SM_rock_sandstone", .35f, .55f, 18);
                Add("SM_rock_sandstone2", .5f, .8f, 22);
                break;
            case "desert":
                // Recovered sandstone meshes are centimetre-sized. Explicit landscape scales,
                // not a unit-conversion correction, turn these into low outcrops/boulders.
                for (int i = 1; i <= 4; i++)
                    Add($"SM_Rock_Sandstone_{i:00}", 4, 10, 24);
                break;
            case "icy":
                // Detailed boulder silhouettes; the client applies neutral stone/snow shading.
                Add("Stone_01", .65f, 1.05f, 32);
                Add("Stone_02", .8f, 1.5f, 32);
                Add("Stone_03", .55f, .8f, 28);
                break;
            default:
                throw new ArgumentException("Unknown scenery preset.");
        }
        return new(1, assets.ToArray());
    }
    public static IReadOnlyList<RockInstance> Resolve(TerrainChunkData chunk, string preset)
    {
        List<RockInstance> result = [];
        foreach (var instance in SceneryPlacement.Resolve(chunk, Catalogue(preset)))
        {
            var candidate = instance.Candidate;
            string name = instance.Model;
            float scale = instance.Scale;
            // One quarter of the original rock candidates become small surface stones.
            // Separate hash bits keep choice/scale independent of catalogue enumeration.
            bool detail = ((candidate.Id >> 20) & 3) == 0;
            if (detail)
            {
                name = $"rock_ground_{1 + (candidate.Id >> 24) % 4}";
                scale = .9f + ((candidate.Id >> 40) & 255) / 255f * 1.4f;
            }
            var b = bounds[name];
            var p = candidate.Position;
            int x = (int)p.X - chunk.Coordinate.X * 64, z = (int)p.Z - chunk.Coordinate.Z * 64;
            var normal = chunk.Normal(x, z);
            var axis = Vector3.Cross(Vector3.UnitY, normal);
            var rotation = axis.LengthSquared() < 1e-8f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.Acos(Math.Clamp(normal.Y, -1, 1)));
            var transform = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationY(candidate.Yaw) * Matrix4x4.CreateFromQuaternion(rotation);
            float height = (b.Max.Y - b.Min.Y) * scale;
            p.Y = chunk.Sample(p.X, p.Z) - Math.Min(.18f, height * .08f);
            transform *= Matrix4x4.CreateTranslation(p);
            var corners = Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), transform)).ToArray();
            var min = corners.Aggregate(Vector3.Min);
            var max = corners.Aggregate(Vector3.Max);
            // Keep the entire footprint on its owning collision chunk. This also makes
            // collider removal safe without introducing cross-chunk scenery ownership.
            if (min.X < chunk.Coordinate.X * 64 + 1 || max.X > chunk.Coordinate.X * 64 + 63 || min.Z < chunk.Coordinate.Z * 64 + 1 || max.Z > chunk.Coordinate.Z * 64 + 63)
                continue;
            bool dry = true;
            for (int iz = (int)MathF.Floor(min.Z); iz <= MathF.Ceiling(max.Z); iz++)
                for (int ix = (int)MathF.Floor(min.X); ix <= MathF.Ceiling(max.X); ix++)
                    if (float.IsFinite(chunk.WaterHeight(ix - chunk.Coordinate.X * 64, iz - chunk.Coordinate.Z * 64)))
                        dry = false;
            if (!dry)
                continue;
            result.Add(new(candidate.Id, name, transform, min, max, !detail));
        }
        return result;
    }
}
