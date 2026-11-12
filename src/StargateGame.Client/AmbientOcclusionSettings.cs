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
        "high" => AmbientOcclusionQuality.High, _ => throw new ArgumentException("AO quality expects auto, off, low, performance or high.")
    };
    public static int InspectionId(string value) => value switch
    {
        "lit" => 0, "ao" => 1, "albedo" => 2, "normals" => 3, "geometric-normals" => 4,
        "geometric-depth" => 5, "depth" => 6, "material-class" => 7, "indirect" => 8,
        "sun-diffuse" => 9, "transmission" => 10, "specular" => 11, "emission" => 12, "hdr" => 13,
        "roughness" => 14, "metalness" => 15, "specular-strength" => 16, "surface-auxiliary" => 17, "sun-visibility" => 18,
        _ => throw new ArgumentException("Unknown deferred inspection view: " + value)
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
