using System.Numerics;
namespace StargateGame.Client;

// Artistic daylight calibration in linear HDR. Night and twilight keep their
// existing fill; weather multipliers and display exposure remain independent.
static class AtmosphereIllumination
{
    public static Vector3 AmbientTint(string preset) => preset == "icy" ? new(.92f, .98f, 1.06f) : Vector3.One;
    public static Vector3 TintAmbient(Vector3 color, string preset)
    {
        var tinted = color * AmbientTint(preset);
        var luma = new Vector3(.2126f, .7152f, .0722f);
        return tinted * (Vector3.Dot(color, luma) / Math.Max(Vector3.Dot(tinted, luma), 1e-8f));
    }
    static float Smooth(float a, float b, float x)
    {
        x = Math.Clamp((x - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }
    public static Vector3 SkyFill(float sunHeight, string preset)
    {
        var twilight = Vector3.Lerp(new(.07f, .085f, .12f), new(.30f, .34f, .40f), Smooth(-.20f, .05f, sunHeight));
        var day = preset == "temperate" ? new Vector3(.24f, .28f, .34f) : new Vector3(.40f, .44f, .50f);
        return Vector3.Lerp(twilight, day, Smooth(.05f, .35f, sunHeight));
    }
    public static float GroundBounce(float sunHeight) => float.Lerp(.22f, .28f, Smooth(.05f, .35f, sunHeight)) * Smooth(-.20f, -.08f, sunHeight);
    public static float SunGain(float sunHeight) => float.Lerp(1, 1.8f, Smooth(.05f, .35f, sunHeight));
    // A clear temperate day has warm directional light and cooler, weaker fill.
    // Keep the sunset spectrum and the deliberately overcast winter profile.
    public static Vector3 SunMultiplier(float sunHeight, string preset)
    {
        var sunlight = Vector3.One * SunGain(sunHeight);
        return preset == "temperate" ? Vector3.Lerp(sunlight, new Vector3(1.45f, 1.38f, 1.26f), Smooth(.05f, .35f, sunHeight)) : sunlight;
    }
    public static float CloudCoverage(string preset) => preset == "icy" ? .85f : preset == "desert" ? .32f : .48f;
    public static float SkyRadianceScale(float sunHeight, string preset) => preset == "temperate" ? float.Lerp(3f, 1.4f, Smooth(.05f, .35f, sunHeight)) : 3f;
    public static float SkyIrradianceScale(float sunHeight, string preset) => preset == "temperate" ? float.Lerp(5f, 3f, Smooth(.05f, .35f, sunHeight)) : 5f;
}
