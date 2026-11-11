using System.Numerics;
using System.Security.Cryptography;
using System.Text;
namespace StargateGame.Shared;

public sealed record PlanetEnvironmentDefinition(int Version, long EpochUtcMilliseconds, double InitialPhase, double DaySeconds, float SunAzimuth, string AtmosphereProfile, string CloudProfile, int CelestialSeed, double? FixedHour = null)
{
    public PlanetWeather Weather { get; init; }
    public bool DebugConfigured { get; init; }
    public static PlanetEnvironmentDefinition Create(Guid location, int seed, string preset)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"environment:1:{location:D}:{seed}:{preset}"));
        double Unit(int i) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i, 4)) / 4294967296.0;
        return new(1, 946684800000, Unit(0), 3600 + 3600 * Unit(4), (float)(Math.Tau * Unit(8)), preset, preset, BitConverter.ToInt32(hash, 12));
    }
    public void Validate()
    {
        if (FixedHour is { } hour && (!double.IsFinite(hour) || hour < 0 || hour >= 24))
            throw new InvalidDataException("Invalid fixed planetary time.");
        if (!Enum.IsDefined(Weather) || Version != 1 || !double.IsFinite(InitialPhase) || InitialPhase is < 0 or >= 1 || !double.IsFinite(DaySeconds) || DaySeconds is < 3600 or > 7200 || !float.IsFinite(SunAzimuth) || !PlanetSettings.Presets.Contains(AtmosphereProfile) || !PlanetSettings.Presets.Contains(CloudProfile))
            throw new InvalidDataException("Unsupported planetary environment.");
    }
    public double Phase(double utcSeconds)
    {
        if (FixedHour is { } hour)
            return hour / 24;
        double p = InitialPhase + (utcSeconds - EpochUtcMilliseconds * .001) / DaySeconds;
        return p - Math.Floor(p);
    }
    public Vector3 Sun(double phase)
    {
        double angle = (phase - .25) * Math.Tau;
        float altitude = (float)Math.Sin(angle), horizontal = (float)Math.Cos(angle);
        return Vector3.Normalize(new(horizontal * MathF.Cos(SunAzimuth), altitude, horizontal * MathF.Sin(SunAzimuth)));
    }
}
