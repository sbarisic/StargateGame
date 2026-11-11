using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Building;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    AxisAlignedBoundingBox ProxyBounds(string name)
    {
        if (modelBounds.TryGetValue(name, out var bounds)) return bounds;
        if (modelAssets.Store?.Current.Assets.TryGetValue("models/" + name + ".json", out var asset) == true
            && asset.BoundsMin is { Length: 3 } min && asset.BoundsMax is { Length: 3 } max)
            return new(new(min[0], min[1], min[2]), new(max[0], max[1], max[2]));
        if (BuildingPrefabs.Known(name))
        {
            var prefab = BuildingPrefabs.Get(name);
            return new(new(prefab.Min[0], prefab.Min[1], prefab.Min[2]), new(prefab.Max[0], prefab.Max[1], prefab.Max[2]));
        }
        return new(new(-.5f, 0, -.5f), new(.5f, 1, .5f));
    }
    void DrawModelProxy(RenderPass pass, ShaderProgram program, string name, Matrix4x4 transform)
    {
        var bounds = ProxyBounds(name);
        using var model = pass.PushModel(Matrix4x4.CreateScale(Vector3.Max(bounds.Max - bounds.Min, new Vector3(.01f)))
            * Matrix4x4.CreateTranslation((bounds.Min + bounds.Max) * .5f) * transform);
        program.SetUniform("uTerrain", 0);
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
        program.SetUniform("uTint", new Vector3(.34f, .39f, .4f));
        Mesh(pass, crate, white, program);
        program.SetUniform("uTint", Vector3.One);
    }
}
