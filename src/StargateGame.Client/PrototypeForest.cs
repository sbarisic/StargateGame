using System.Numerics;
using FishGfx;

namespace StargateGame.Client;

sealed record PrototypeTree(string Species, Vector3 Position, float Yaw, float Size, TreePrototypeDefinition? SourceDefinition = null)
{
    public TreePrototypeDefinition Definition { get; } = SourceDefinition ?? TreePrototypeDefinition.For(Species);
    public float Scale => Definition.Scale * Size;
    public Matrix4x4 Model => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);
}

static class PrototypeForest
{
    public static float PathX(float z) => 4 * MathF.Sin((z + 80) * .05f);
    static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        return value ^ (value >> 16);
    }
    static float Unit(uint value) => (Hash(value) & 65535) / 65535f;
    public static PrototypeTree[] Create(int seed)
    {
        List<PrototypeTree> trees = [];
        for (int z = -4; z <= 4; z++)
            for (int x = -4; x <= 4; x++)
            {
                uint key = Hash(unchecked((uint)seed ^ (uint)x * 73856093u ^ (uint)z * 19349663u));
                var p = new Vector3(x * 8.5f + (Unit(key + 1) - .5f) * 3.5f, 0,
                    -45 + z * 8.5f + (Unit(key + 2) - .5f) * 3.5f);
                // Irregular perimeter, a walkable sightline, and the existing gate clearing.
                if (x * x + z * z > 27 || Math.Abs(p.X - PathX(p.Z)) < 3 ||
                    Vector3.DistanceSquared(p, new(-35, 0, -35)) < 12 * 12)
                    continue;
                float choice = Unit(key + 3);
                string species = choice < .4f ? "broadleaf" : choice < .7f ? "broadleaf-2" : choice < .9f ? "birch" : "spruce";
                trees.Add(new(species, p, Unit(key + 4) * MathF.Tau, .8f + Unit(key + 5) * .4f));
            }
        return trees.ToArray();
    }
}

sealed partial class SceneRenderer
{
    static IEnumerable<Vector3> BoundsCorners(Vector3 min, Vector3 max)
    {
        for (int i = 0; i < 8; i++)
            yield return new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
    }
    AxisAlignedBoundingBox PrototypeBounds(PrototypeTree tree, Vector3 min, Vector3 max)
    {
        Vector3 lower = new(float.PositiveInfinity), upper = new(float.NegativeInfinity);
        var model = tree.Model;
        foreach (var corner in BoundsCorners(min, max))
        {
            var world = Vector3.Transform(corner, model);
            lower = Vector3.Min(lower, world);
            upper = Vector3.Max(upper, world);
        }
        // Broad gust (1.35) plus fine leaf motion; include filtering support too.
        float margin = weatherFrame.Wind.Length() * TreeSettings.WindStrength * 1.4f + .2f;
        return new(lower - new Vector3(margin), upper + new Vector3(margin));
    }
    static Vector4 PrototypeScreenBounds(AxisAlignedBoundingBox bounds, Matrix4x4 viewProjection)
    {
        Vector2 min = Vector2.One, max = -Vector2.One;
        foreach (var corner in BoundsCorners(bounds.Min, bounds.Max))
        {
            var clip = Vector4.Transform(new Vector4(corner, 1), viewProjection);
            // Camera within/behind the near face: retain the full screen conservatively.
            if (clip.W <= .01f)
                return new(-1, -1, 1, 1);
            var ndc = new Vector2(clip.X, clip.Y) / clip.W;
            min = Vector2.Min(min, ndc);
            max = Vector2.Max(max, ndc);
        }
        min = Vector2.Max(-Vector2.One, min - new Vector2(.002f));
        max = Vector2.Min(Vector2.One, max + new Vector2(.002f));
        return new(min.X, min.Y, max.X, max.Y);
    }
}
