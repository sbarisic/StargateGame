using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed record DhdLightingSettings
{
    public float GlyphBloomGain
    {
        get;
        init;
    } = 1.75f;
    public float SpillStrength
    {
        get;
        init;
    } = .02f;
    // Metres in model space; range and source radius follow the entity's scale.
    public float SpillRange
    {
        get;
        init;
    } = .3f;
}

sealed partial class SceneRenderer
{
    public DhdLightingSettings DhdLights
    {
        get;
        set;
    } = new();
    const int MaxDhdLights = 38; // All available keys, including diagnostic previews.
    const float DhdSourceRadius = .06f;
    readonly record struct DhdLightSurface(Vector3 Position, Vector3 Normal);
    // Immutable model geometry only. Selection and entity transforms are read each draw.
    static readonly Lazy<Dictionary<int, DhdLightSurface>> dhdLightSurfaces = new(() =>
    {
        var surfaces = new Dictionary<int, DhdLightSurface>();
        foreach (var key in DhdInteraction.Surfaces.Where(s => DhdInteraction.Available(s.Key)).GroupBy(s => s.Key))
        {
            Vector3 position = Vector3.Zero, normal = Vector3.Zero;
            float weight = 0;
            foreach (var face in key)
            {
                var cross = Vector3.Cross(face.B - face.A, face.C - face.A);
                float area = cross.Length();
                // Exclude side walls and undersides; area weighting avoids tessellation bias.
                if (area <= 1e-8f || cross.Y / area <= .35f)
                    continue;
                position += (face.A + face.B + face.C) / 3 * area;
                normal += cross;
                weight += area;
            }
            if (weight > 0)
                surfaces.Add(key.Key, new(position / weight, Vector3.Normalize(normal)));
        }
        return surfaces;
    });

    int DhdLightingUniforms(ShaderProgram program, StaticEntity entity)
    {
        if (entity.Model != "dhd" || !DhdGlyphs.AnyLit(entity.Id)
            || DhdLights.SpillStrength <= 0 || DhdLights.SpillRange <= 0 || entity.Scale <= 0)
            return 0;
        int count = 0;
        foreach (var part in models[entity.Model])
        {
            if (part.DhdOrb || part.Emission is null || !DhdGlyphs.IsLit(entity.Id, part.DhdKeyId)
                || !dhdLightSurfaces.Value.TryGetValue(part.DhdKeyId, out var surface))
                continue;
            if (count == MaxDhdLights)
                break;
            // Lift a broad virtual source above the key to approximate nearby reflected light.
            var local = Vector3.Transform(surface.Position + surface.Normal * .035f, PressTransform(part));
            var position = local * entity.Scale + entity.Position;
            program.SetUniform($"uDhdLightPositionRange[{count}]", new Vector4(position, DhdLights.SpillRange * entity.Scale));
            program.SetUniform($"uDhdLightNormalRadius[{count}]", new Vector4(surface.Normal, DhdSourceRadius * entity.Scale));
            // Scaling intensity by area keeps the lighting consistent on scaled consoles.
            program.SetUniform($"uDhdLightColor[{count}]", part.EmissionColor * part.EmissionIntensity
                * DhdLights.SpillStrength * entity.Scale * entity.Scale);
            count++;
        }
        return count;
    }
}
