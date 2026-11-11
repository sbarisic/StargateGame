using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public DhdGlyphState DhdGlyphs
    {
        get;
    } = new();
    public int[] DhdPreviewGlyphs
    {
        get;
        set;
    } = [];
    public DhdHintFrame DhdHint
    {
        get;
        set;
    }
    public bool DhdHintActive
    {
        get;
        set;
    }
    StaticEntity[] visibleLitDhds = [];
    void PrepareDhds(ViewFrustum frustum)
    {
        var dhds = World.Definition.Entities.Where(e => e.Model == "dhd");
        if (DhdPreviewGlyphs.Length > 0)
            foreach (var dhd in dhds)
                DhdGlyphs.SetGlyphs(dhd.Id, DhdPreviewGlyphs);
        // Terrain can be playable before streamed DHD geometry is published.
        // Recompute each frame so emission resumes as soon as the model arrives.
        if (!models.ContainsKey("dhd") || !modelBounds.TryGetValue("dhd", out var bounds))
        {
            visibleLitDhds = [];
            return;
        }
        // Retain bloom targets during the pauses rather than reallocating every pulse.
        visibleLitDhds = dhds.Where(e => (DhdGlyphs.AnyEmission(e.Id) || DhdHintActive) &&
            frustum.Intersects(new(bounds.Min * e.Scale + e.Position, bounds.Max * e.Scale + e.Position))).ToArray();
        if (visibleLitDhds.Length == 0)
            return;
        gateEmissionShader ??= LoadProgram("world.vert", "gate_emission.frag");
        gateEmissionShader.SetUniform("uSceneDepth", 1);
    }
    void DrawDhdEmission(RenderPass pass, bool mask)
    {
        if (visibleLitDhds.Length == 0 || gateEmissionShader is null)
            return;
        Lighting(gateEmissionShader);
        gateEmissionShader.SetUniform("uEmissiveOnly", mask ? 1 : 0);
        gateEmissionShader.SetUniform("uMaskSize", new Vector2(horizonMask?.Width ?? 1, horizonMask?.Height ?? 1));
        using var state = pass.PushState(mask ? pass.State : OpaqueState with
        {
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.LessOrEqual,
            BlendEnabled = true,
            SourceBlend = BlendFactor.One,
            DestinationBlend = BlendFactor.One
        });
        foreach (var dhd in visibleLitDhds)
        {
            if (!models.TryGetValue(dhd.Model, out var parts))
                continue;
            using var endpoint = SelectGate(dhd);
            using var transform = pass.PushModel(dhd.Transform);
            foreach (var part in parts)
            {
                bool accepted = part.DhdOrb ? DhdGlyphs.IsOrbLit(dhd.Id) : DhdGlyphs.IsLit(dhd.Id, part.DhdKeyId);
                float hint = part.DhdOrb ? 0 : DhdHint.ForKey(part.DhdKeyId);
                if (part.Emission is null || (!accepted && hint <= 0))
                    continue;
                float bloomGain = mask && !part.DhdOrb ? accepted ? Math.Max(0, DhdLights.GlyphBloomGain) : .65f : 1;
                var emission = accepted ? part.EmissionColor * part.EmissionIntensity * (part.DhdOrb ? OrbStrength : 1) : new Vector3(.045f) * hint;
                gateEmissionShader.SetUniform("uEmission", emission * bloomGain);
                using var press = pass.PushModel(PressTransform(part) * dhd.Transform);
                Mesh(pass, part.mesh, part.Emission, gateEmissionShader);
                HorizonDrawCalls++;
            }
        }
    }
}
