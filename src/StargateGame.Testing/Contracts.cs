using System.Text.Json;
using System.Text.Json.Serialization;

namespace StargateGame.Testing;

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) }
    };
    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Options);
    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException(path);
    public static void Write(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
    }
}

public enum TestAction
{
    Status, Connect, Disconnect, Stop, Capture, Camera, Settings, Move, Teleport,
    Dial, Carry, Build, Weapon, ResetPlayer, ResetStation, ResetScenario,
    SwitchScenario, AddClient, GatePreview, AudioRecord, Sample, Wait, Assert, PrepareDestination, SetupProp, Window, Relocate, UiReview, MovementReview
}
public sealed record TestRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Session { get; init; } = "";
    public int Generation { get; init; }
    public int Client { get; init; } = 1;
    public TestAction Action { get; init; }
    public JsonElement Data { get; init; } = TestJson.Element(new { });
    public int TimeoutSeconds { get; init; } = 120;
    public string Text(string key, string fallback = "") => Data.TryGetProperty(key, out var v) ? v.GetString() ?? fallback : fallback;
    public int Int(string key, int fallback = 0) => Data.TryGetProperty(key, out var v) ? v.GetInt32() : fallback;
    public double Number(string key, double fallback = 0) => Data.TryGetProperty(key, out var v) ? v.GetDouble() : fallback;
    public bool Flag(string key, bool fallback = false) => Data.TryGetProperty(key, out var v) ? v.GetBoolean() : fallback;
    public T Value<T>(string key) => Data.GetProperty(key).Deserialize<T>(TestJson.Options) ?? throw new ArgumentException(key);
}
public sealed record TestResult(Guid Request, string Session, int Generation, string Outcome, JsonElement Data, string? Error = null, string? ErrorCode = null, int Client = 1)
{
    public static TestResult Ok(TestRequest r, object? data = null) => new(r.Id, r.Session, r.Generation, "passed", TestJson.Element(data), Client: r.Client);
    public static TestResult Fail(TestRequest r, Exception e) => new(r.Id, r.Session, r.Generation, "failed", TestJson.Element(new { }), e.ToString(), e is OperationCanceledException ? "cancelled" : e.GetType().Name, r.Client);
    public void EnsureSuccess() { if (Outcome != "passed") throw new InvalidOperationException(Error ?? Outcome); }
}
public sealed record ScenarioOptions(string Scenario = "sandbox", string Preset = "default", int Seed = 7319,
    string Biome = "temperate", string Landscape = "Inland", string Settlement = "wilderness", string Style = "auto",
    double Hour = 12, string Weather = "off", string Quality = "performance", bool Cold = false, bool Delayed = false,
    int TerrainBytesPerSecond = 0, int DestinationDelayMilliseconds = 0, string[]? Overrides = null);
public sealed record ScenarioPreset(string Id, string Biome, string Landscape, int Seed, string Bookmark);
public sealed record ScenarioDefinition(string Id, string Name, string Description, string[] Bookmarks, string[] Recipes)
{
    public string[] Stations => Id == "sandbox" ? ["gate-pair", "props", "movement", "equipment", "interior", "forest", "water", "technology"] : ["spawn", "gate", "dhd"];
}
public sealed record SessionManifest(string Id, int CoordinatorPid, string Pipe, string Root, string BuildRoot,
    int Generation, ScenarioOptions Options, string State, int[] ClientPids, int ServerPid, string? Error = null);
public sealed record LaunchOptions(string Session, string Root, string BuildRoot, ScenarioOptions Options,
    int Clients = 1, int OwnerPid = 0, string? AttachedClientPipe = null, bool Manual = false);
public sealed record RecipeStep(TestAction Action, JsonElement Data, int Client = 1, int TimeoutSeconds = 120);
public sealed record RecipeDefinition(string Name, RecipeStep[] Steps);

public static class ScenarioCatalog
{
    public const int Version = 1;
    public static readonly string[] RenderSettings = ["hour", "quality", "weather", "clouds", "volumetrics", "reflections", "groundMist", "grass", "grassDensity", "parallax", "gateLighting", "waves", "visualTime", "orbit", "traverse", "meadowMaterial", "horizon", "horizonProps", "horizonPlayers", "horizonOcclusion", "shadowFixture", "propMode", "waveCheck", "landscapeCheck", "stress", "weatherFixture", "roof", "weatherCheck"];
    public static readonly ScenarioDefinition[] Scenarios = [
        new("sandbox", "Sandbox", "Two gates, physics, equipment and furnished inspection stations.",
            ["player", "gate-pair", "dhd", "props", "movement", "equipment", "interior", "forest", "water", "technology"],
            ["visual-check", "gate-roundtrip", "lifecycle", "audio", "environment", "gate-effects", "sandbox-check", "horizon-lifecycle", "ui-live", "movement-visual"]),
        new("planet-explorer", "Planet Explorer", "Generated biomes, regional landscapes and settlements.",
            ["player", "gate", "wide", "grass", "forest", "river", "cliff", "lake", "underwater", "settlement", "settlement-interior", "region"],
            ["visual-check", "environment", "lifecycle"]),
        new("travel", "Travel", "Production dialing, cross-world travel and persistence.",
            ["player", "gate", "dhd", "wide"], ["visual-check", "gate-roundtrip", "travel-roundtrip", "audio", "audio-endpoints", "dialing-failure", "audio-failure", "lifecycle"])
    ];
    public static readonly ScenarioPreset[] AdvancedPresets = [new("graphics-lab", "temperate", "Inland", 7319, "gate")];
    public static readonly ScenarioPreset[] Presets = [
        new("default", "temperate", "Inland", 7319, "gate"),
        new("mountain-meadow", "temperate", "Inland", 7319, "region"),
        new("forest-interior", "temperate", "Inland", 7319, "region"),
        new("desert-mesa", "desert", "Inland", 7320, "region"),
        new("coastline", "temperate", "Coastal", 7321, "region"),
        new("icy-highlands", "icy", "Inland", 7322, "region")
    ];
    public static ScenarioOptions Resolve(ScenarioOptions options)
    {
        if (!Scenarios.Any(s => s.Id == options.Scenario)) throw new ArgumentException("Unknown scenario: " + options.Scenario);
        if (options.Preset == "graphics-lab" && options.Scenario != "sandbox") throw new ArgumentException("graphics-lab is a Sandbox render-only fixture.");
        var preset = Presets.Concat(AdvancedPresets).SingleOrDefault(p => p.Id == options.Preset) ?? throw new ArgumentException("Unknown preset: " + options.Preset);
        if (preset.Id != "default") options = options with { Biome = options.Overrides?.Contains("biome") == true ? options.Biome : preset.Biome, Landscape = options.Overrides?.Contains("landscape") == true ? options.Landscape : preset.Landscape, Seed = options.Overrides?.Contains("seed") == true ? options.Seed : preset.Seed };
        if (!new[] { "temperate", "icy", "desert" }.Contains(options.Biome) || !new[] { "Auto", "Inland", "Coastal" }.Contains(options.Landscape)) throw new ArgumentException("Invalid biome/landscape.");
        if (!new[] { "auto", "wilderness", "ruins", "village", "city" }.Contains(options.Settlement) || !new[] { "auto", "mega", "seahill", "desert", "modern" }.Contains(options.Style)) throw new ArgumentException("Invalid settlement/style.");
        if (!double.IsFinite(options.Hour) || options.Hour < 0 || options.Hour > 24) throw new ArgumentException("Hour must be 0–24.");
        if (!new[] { "auto", "off", "light-snow", "storm" }.Contains(options.Weather)) throw new ArgumentException("Invalid weather.");
        if (!new[] { "performance", "low", "high" }.Contains(options.Quality)) throw new ArgumentException("Invalid quality.");
        if (options.TerrainBytesPerSecond < 0 || options.DestinationDelayMilliseconds is < 0 or > 120000) throw new ArgumentException("Invalid network delay.");
        return options;
    }
}
