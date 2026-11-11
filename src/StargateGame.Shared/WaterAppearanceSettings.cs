using System.Numerics;

namespace StargateGame.Shared;

// Metres and linear light. This is presentation data, never water physics or
// generation identity. The same transport coefficients are used from both sides.
public sealed record WaterAppearanceSettings
{
    // Muted, slightly green-gray water. Similar channel extinction keeps
    // transmitted beds from turning cyan; blue attenuates a little faster.
    public Vector3 Absorption { get; init; } = new(.22f, .215f, .25f);
    public Vector3 Scattering { get; init; } = new(.052f, .057f, .050f);
    public float RefractiveIndex { get; init; } = 1.333f;
    public float Roughness { get; init; } = .30f;
    public float ReflectionStrength { get; init; } = .55f;
    public Vector3 RippleMetres { get; init; } = new(9, 2.3f, .55f);
    public Vector3 RippleStrength { get; init; } = new(.055f, .032f, .014f);
    public float FoamStrength { get; init; } = .22f;

    public void Validate()
    {
        static bool V(Vector3 v, float min, float max) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z)
            && v.X >= min && v.Y >= min && v.Z >= min && v.X <= max && v.Y <= max && v.Z <= max;
        if (!V(Absorption, .001f, 10) || !V(Scattering, 0, 1) || !V(RippleMetres, .05f, 100)
            || !V(RippleStrength, 0, .5f) || !float.IsFinite(RefractiveIndex) || RefractiveIndex is < 1.01f or > 2
            || !float.IsFinite(Roughness) || Roughness is < .06f or > .6f
            || !float.IsFinite(ReflectionStrength) || ReflectionStrength is < 0 or > 1
            || !float.IsFinite(FoamStrength) || FoamStrength is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(WaterAppearanceSettings), "Invalid water optical settings.");
    }
}

public enum WaterDiagnostic { None, Depth, Normals, Flow, Reflection, Refraction, Foam }
