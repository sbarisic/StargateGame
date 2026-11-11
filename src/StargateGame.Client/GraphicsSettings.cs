using System.Numerics;
using FishGfx.Graphics.Shadows;

namespace StargateGame.Client;

enum GraphicsQuality
{
    Performance, Low, High
}

sealed record GraphicsSettings(GraphicsQuality Quality)
{
    public static GraphicsSettings Parse(string value) => value.ToLowerInvariant() switch
    {
        "performance" => new(GraphicsQuality.Performance),
        "low" => new(GraphicsQuality.Low),
        "high" => new(GraphicsQuality.High),
        _ => throw new ArgumentException("--graphics must be performance, low, or high.")
    };
    public DirectionalShadowOptions Shadows => new(Quality == GraphicsQuality.Low ? 2 : 3,
        Quality switch
        {
            GraphicsQuality.Low => 1024,
            GraphicsQuality.High => 2048,
            _ => 1536
        },
        Quality switch
        {
            GraphicsQuality.Low => 96,
            GraphicsQuality.High => 320,
            _ => 192
        },
        .8f, .15f, Quality == GraphicsQuality.High ? DirectionalShadowFilter.Pcf5x5 : DirectionalShadowFilter.Pcf3x3,
        1.5f, 1f)
    {
        UpdateIntervals = [1, 2, 4]
    };
    public GraphicsSettings Next() => new((GraphicsQuality)(((int)Quality + 1) % 3));
}

sealed record Daylight(Vector3 Zenith, Vector3 Horizon, Vector3 SunColor, Vector3 AmbientSky, Vector3 AmbientGround)
{
    // Base density, low-altitude density, height falloff. Shared by surfaces and emission.
    public Vector3 Fog
    {
        get;
        init;
    } = new(.00048f, .00055f, .012f);
    public static readonly Vector3 SunDirection = Vector3.Normalize(new(-.55f, .75f, .4f));
    public static Daylight For(string preset) => preset switch
    {
        "desert" => new(new(.12f, .30f, .56f), new(.70f, .65f, .52f), new(1.9f, 1.65f, 1.30f), new(.28f, .32f, .36f), new(.20f, .14f, .09f)) { Fog = new(.0017f, .0024f, .010f) },
        "icy" => new(new(.10f, .25f, .47f), new(.65f, .76f, .85f), new(1.65f, 1.75f, 1.85f), new(.30f, .38f, .48f), new(.17f, .21f, .27f)),
        _ => new(new(.10f, .28f, .55f), new(.62f, .73f, .80f), new(1.85f, 1.72f, 1.50f), new(.28f, .36f, .46f), new(.14f, .15f, .10f))
    };
}
