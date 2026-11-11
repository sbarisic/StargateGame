using System.Numerics;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    // Balance the bright realistic broadleaves and darker legacy leafy trees while
    // preserving the existing olive hue and lighting response.
    const float RealisticLeafBrightness = .80f;
    const float RealisticLeafSaturation = .90f;
    const float DarkLeafBrightness = 1.20f;
    static readonly Vector3 RealisticLeafOlive = new(1f, .95f, .90f);

    void ApplyTreeLeafPalette(ShaderProgram program, string model)
    {
        if (World.Definition.Preset != "temperate")
        {
            program.SetUniform("uLeafTint", Vector3.One);
            program.SetUniform("uLeafSaturation", 1f);
            return;
        }

        // Mesh LOD names and volume definitions resolve to the same species.
        string source = model.StartsWith("veg-", StringComparison.Ordinal) ? model[4..] : model;
        // The summer suffix follows the LOD suffix on reduced mesh variants.
        bool summerBirch = source.StartsWith("European_White_Birch_Hero_Winter", StringComparison.Ordinal)
            && source.EndsWith("-summer", StringComparison.Ordinal);
        int lod = source.IndexOf("_Lod", StringComparison.Ordinal);
        if (lod >= 0) source = source[..lod];
        if (source.EndsWith("_detail", StringComparison.Ordinal)) source = source[..^7];
        bool realistic = source is "RealisticTree_01" or "RealisticTree_02"
            or "MoreRealisticTree_01" or "MoreRealisticTree_02";
        bool darkLeaves = summerBirch || source == "Leave-Tree-01a";

        program.SetUniform("uLeafTint", realistic
            ? TreeSettings.LeafTint * RealisticLeafOlive * RealisticLeafBrightness
            : TreeSettings.LeafTint * (darkLeaves ? DarkLeafBrightness : 1f));
        program.SetUniform("uLeafSaturation", TreeSettings.LeafSaturation
            * (realistic ? RealisticLeafSaturation : 1f));
    }
}
