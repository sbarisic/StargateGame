using System.Globalization;
using System.Text.Json;

namespace StargateGame.Client;

enum ExposureMode { Auto, Manual }
sealed record ToneMappingSettings
{
    public ExposureMode Mode { get; init; } = ExposureMode.Auto;
    public float Compensation { get; init; }
    public static ExposureMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "auto" => ExposureMode.Auto, "manual" => ExposureMode.Manual,
        _ => throw new ArgumentException("--exposure must be auto or manual.")
    };
    public static float ParseEv(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ev)
            || !float.IsFinite(ev) || ev < -2 || ev > 2)
            throw new ArgumentException("--exposure-ev must be a finite number from -2 to +2.");
        return MathF.Round(ev * 10) / 10;
    }
    static string FilePath => Path.Combine(StargateGame.Shared.UserData.Root, "tone-mapping.json");
    public static ToneMappingSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ToneMappingSettings>(File.ReadAllText(FilePath));
            return settings is not null && Enum.IsDefined(settings.Mode) && float.IsFinite(settings.Compensation)
                ? settings with { Compensation = MathF.Round(Math.Clamp(settings.Compensation, -2, 2) * 10) / 10 } : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
