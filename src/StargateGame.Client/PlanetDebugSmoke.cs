using StargateGame.Shared;
namespace StargateGame.Client;
sealed partial class GameApp
{
    int planetSmokeStage, planetSmokeBiome;
    double planetSmokeNext;
    string? planetDebugCapture;
    void PlanetDebugSmoke(double now)
    {
        if (session is null || scene is null || now - connectedAt < 3 || now < planetSmokeNext) return;
        if (planetSmokeStage == 0)
        {
            window.SetClientSize(960, 600); OpenGenerator(); planetSmokeStage = 1; planetSmokeNext = now + 2; return;
        }
        if (planetSmokeStage == 1)
        {
            if (session.DebugHome is null) return;
            LoadGenerator(); planetDebugCapture = "generator-960x600.png"; planetSmokeStage = 2; planetSmokeNext = now + 2; return;
        }
        if (planetSmokeStage == 2)
        {
            generatorScroll = 12 - GeneratorRows;
            planetDebugCapture = "generator-weather-960x600.png"; planetSmokeStage = 3; planetSmokeNext = now + 2; return;
        }
        if (planetSmokeStage == 3)
        {
            generatorDraft = PlanetDebugSettings.Read(session.DebugHome!) with { Seed = 7340 + planetSmokeBiome, Preset = PlanetSettings.Presets[planetSmokeBiome],
                Settlements = new("wilderness"), Generation = new(1.2f, -1, .5f, .5f),
                Environment = new(11, true, planetSmokeBiome switch { 1 => PlanetWeather.Snowstorm, 2 => PlanetWeather.DesertDust, _ => PlanetWeather.Clear }) };
            session.ApplyPlanetDebug(generatorDraft, true); planetSmokeStage = 4; return;
        }
        if (planetSmokeStage == 4)
        {
            if (session.PlanetDebugState?.Busy == true || session.PlanetVisualPending) return;
            if (session.PlanetDebugState?.Phase != "Ready") throw new InvalidOperationException(session.PlanetDebugState?.Detail);
            planetPanel = false; SetPause(false); window.SetClientSize(1280, 800);
            planetSmokeStage = 5; planetSmokeNext = now + 8; return;
        }
        if (planetSmokeStage == 5)
        {
            if (scene.WorldCanopiesPending != 0) return;
            planetDebugCapture = $"planet-{generatorDraft.Preset}-{generatorDraft.Environment.Weather}.png";
            Console.WriteLine($"PLANET VISUAL {generatorDraft.Preset} volumeTrees={scene.VisibleVolumeTrees} oldLeaves={scene.VisibleLegacyLeafTrees} modelMiB={scene.ModelGpuBytes / 1048576d:F1}");
            planetSmokeBiome++;
            if (planetSmokeBiome < 3) { planetSmokeStage = 3; planetSmokeNext = now + 2; }
            else { planetSmokeStage = 6; planetSmokeNext = now + 2; }
            return;
        }
        Console.WriteLine("PLANET GENERATOR VISUAL SMOKE PASSED"); window.IsCloseRequested = true;
    }
}
