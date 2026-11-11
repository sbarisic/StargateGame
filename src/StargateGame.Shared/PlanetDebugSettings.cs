namespace StargateGame.Shared;

public sealed record PlanetGenerationSettings(float Relief = 1, float WaterOffset = 0, float TreeDensity = 1, float RockDensity = 1, LandscapeMode LandscapeMode = LandscapeMode.Auto)
{
    public void Validate()
    {
        if (!Enum.IsDefined(LandscapeMode) || !float.IsFinite(Relief) || Relief is < .25f or > 2 || !float.IsFinite(WaterOffset) || WaterOffset is < -3 or > 3
            || !float.IsFinite(TreeDensity) || TreeDensity is < 0 or > 2 || !float.IsFinite(RockDensity) || RockDensity is < 0 or > 2)
            throw new InvalidDataException("Invalid planet generation parameters.");
    }
    public string Fingerprint => FormattableString.Invariant($"{Relief:R}:{WaterOffset:R}:{TreeDensity:R}:{RockDensity:R}:{LandscapeMode}");
}
public enum PlanetWeather { Auto, Clear, LightSnow, Snowstorm, DesertDust }
public sealed record PlanetEnvironmentSettings(double Hour = 11, bool Fixed = true, PlanetWeather Weather = PlanetWeather.Auto)
{
    public void Validate()
    {
        if (!double.IsFinite(Hour) || Hour is < 0 or >= 24 || !Enum.IsDefined(Weather))
            throw new InvalidDataException("Invalid time or weather.");
    }
    public PlanetEnvironmentDefinition Apply(PlanetEnvironmentDefinition environment, long now)
    {
        Validate();
        return environment with { FixedHour = Fixed ? Hour : null, InitialPhase = Hour / 24, EpochUtcMilliseconds = now, Weather = Weather, DebugConfigured = true };
    }
    public static PlanetEnvironmentSettings Read(PlanetDefinition planet, long now)
    {
        var e = planet.Environment ?? PlanetEnvironmentDefinition.Create(planet.LocationId, planet.Seed, planet.Preset);
        return new(e.Phase(now * .001) * 24, e.FixedHour is not null, e.Weather);
    }
}
public sealed record PlanetDebugSettings(int Seed, string Preset, SettlementSettings Settlements, PlanetGenerationSettings Generation, PlanetEnvironmentSettings Environment)
{
    public void Validate()
    {
        if (!PlanetSettings.Presets.Contains(Preset) || Settlements is null || Generation is null || Environment is null)
            throw new InvalidDataException("Invalid planet settings.");
        Settlements.Validate(); Generation.Validate(); Environment.Validate();
    }
    public static PlanetDebugSettings Read(PlanetDefinition planet) => new(planet.Seed, planet.Preset, planet.SettlementGeneration,
        planet.Generation, PlanetEnvironmentSettings.Read(planet, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
}
public sealed record PlanetDebugRequest(Guid Id, PlanetDebugSettings Settings);
public sealed record PlanetDebugStatus(Guid RequestId, string Phase, string Detail, double ElapsedSeconds, bool Busy, bool CanCancel, PlanetDefinition? Home = null);
