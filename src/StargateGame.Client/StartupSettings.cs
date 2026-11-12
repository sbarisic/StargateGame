using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class GameApp
{
    void ConfigureScene()
    {
        if (scene is null)
            throw new InvalidOperationException("Renderer is not prepared.");
        toneMappingSettings = toneMappingSettings with
        {
            Mode = ToneMappingSettings.ParseMode(Arguments.Get(args, "--exposure", toneMappingSettings.Mode.ToString())),
            Compensation = ToneMappingSettings.ParseEv(Arguments.Get(args, "--exposure-ev", toneMappingSettings.Compensation.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        };
        scene.ToneMapping = toneMappingSettings;
        scene.GateLightingEnabled = Arguments.Get(args, "--gate-lighting", "on").ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgumentException("--gate-lighting must be on or off.")
        };
        string timeOfDay = Arguments.Get(args, "--time-of-day", args.Contains("--fixture") ? "12" : "");
        if (timeOfDay.Length > 0)
        {
            if (!float.TryParse(timeOfDay, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float hour) || !float.IsFinite(hour) || hour < 0 || hour > 24)
                throw new ArgumentException("--time-of-day must be a finite hour from 0 through 24.");
            scene.TimeOfDay = hour;
        }
        bool Toggle(string name, string fallback = "on") => Arguments.Get(args, name, fallback).ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgumentException(name + " must be on or off.")
        };
        scene.KawooshMistEnabled = Toggle("--kawoosh-mist");
        scene.KawooshBloomEnabled = Toggle("--kawoosh-bloom");
        scene.TerrainParallaxEnabled = Toggle("--terrain-parallax");
        scene.PauseSky = args.Contains("--pause-sky");
        scene.CloudsEnabled = Toggle("--clouds", args.Contains("--fixture") ? "off" : "on");
        scene.VolumetricsEnabled = Toggle("--volumetrics");
        scene.WaterReflections = Toggle("--water-reflections");
        scene.RealisticWater = Arguments.Get(args, "--water-style", "realistic").ToLowerInvariant() switch
        {
            "realistic" => true,
            "legacy" => false,
            _ => throw new ArgumentException("--water-style must be legacy or realistic.")
        };
        string waterView = Arguments.Get(args, "--water-debug", "none");
        if (!Enum.TryParse<WaterDiagnostic>(waterView, true, out var diagnostic) || !Enum.IsDefined(diagnostic))
            throw new ArgumentException("--water-debug must be none, depth, normals, flow, reflection, refraction or foam.");
        scene.WaterDebug = diagnostic;
        if (diagnostic != WaterDiagnostic.None) scene.RealisticWater = true;
        scene.GroundCoverSettings = new()
        {
            Enabled = Arguments.Get(args, "--grass", "on") switch
            {
                "on" => true,
                "off" => false,
                _ => throw new ArgumentException("--grass must be on or off.")
            }
        };
        scene.GroundCoverSettings = scene.GroundCoverSettings with
        {
            Hybrid = Arguments.Get(args, "--grass-renderer", "hybrid") switch { "hybrid" => true, "mesh" => false, _ => throw new ArgumentException("--grass-renderer must be mesh or hybrid.") },
            DensityOverride = Arguments.Number(args, "--grass-density", 0, 0, 24)
        };
        scene.SetLandscapeView(Enum.Parse<LandscapeView>(Arguments.Get(args,"--landscape-view","None"),true));
        scene.LandscapeDiagnostics = args.Contains("--landscape-check");
        scene.WeatherDiagnostics = args.Contains("--weather-check");
        scene.WeatherFixtureRoof = !args.Contains("--weather-roof-off");
        scene.WeatherSettings = new()
        {
            Mode = WeatherSettings.Parse(Arguments.Get(args, "--weather", "auto")),
            GroundMistEnabled = Toggle("--ground-mist"),
            DesertDustEnabled = Toggle("--dust")
        };
        scene.HorizonSettings = new()
        {
            Enabled = eventHorizon,
            CollisionWaves = Arguments.Get(args, "--horizon-waves", "on") switch
            {
                "on" => true,
                "off" => false,
                _ => throw new ArgumentException("--horizon-waves must be on or off.")
            }
        };
        scene.HorizonStress = args.Contains("--horizon-stress");
        scene.HorizonWaveDiagnostics = args.Contains("--horizon-wave-check");
        scene.AoSettings = aoSettings with { Inspection = "lit" };
        scene.SetGraphics(graphicsSettings);
    }
}
