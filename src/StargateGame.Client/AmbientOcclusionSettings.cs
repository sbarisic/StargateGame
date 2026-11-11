using System.Text.Json;

namespace StargateGame.Client;

enum AmbientOcclusionQuality { Auto, Off, Low, Performance, High }
sealed record AmbientOcclusionSettings
{
    public AmbientOcclusionQuality Quality { get; init; } = AmbientOcclusionQuality.Auto;
    public float Radius { get; init; } = 1;
    public float Strength { get; init; } = 1;
    public string Inspection { get; init; } = "lit";
    public AmbientOcclusionQuality Resolve(GraphicsQuality graphics) => Quality == AmbientOcclusionQuality.Auto
        ? graphics switch { GraphicsQuality.Low => AmbientOcclusionQuality.Low, GraphicsQuality.High => AmbientOcclusionQuality.High, _ => AmbientOcclusionQuality.Performance } : Quality;
    public static AmbientOcclusionQuality Parse(string value) => value.ToLowerInvariant() switch
    {
        "auto" => AmbientOcclusionQuality.Auto, "off" => AmbientOcclusionQuality.Off,
        "low" => AmbientOcclusionQuality.Low, "performance" => AmbientOcclusionQuality.Performance,
        "high" => AmbientOcclusionQuality.High, _ => throw new ArgumentException("--ao expects auto, off, low, performance or high.")
    };
    static string FilePath => Path.Combine(StargateGame.Shared.UserData.Root, "ambient-occlusion.json");
    public static AmbientOcclusionSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AmbientOcclusionSettings>(File.ReadAllText(FilePath));
            return settings is not null && Enum.IsDefined(settings.Quality) ? new() { Quality = settings.Quality } : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(new { Quality })); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
