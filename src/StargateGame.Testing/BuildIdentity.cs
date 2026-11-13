using System.Security.Cryptography;
using System.Text;

namespace StargateGame.Testing;

public sealed record PreparedBuild(string Repository, string SourceIdentity, DateTimeOffset PreparedUtc, string[] Projects, double Seconds);
public static class BuildIdentity
{
    public static string Source(string repository)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(repository, "src"), "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".cs" or ".csproj" && !p.Split(Path.DirectorySeparatorChar).Any(n => n is "bin" or "obj"))
            .Concat(new[] { "Directory.Build.props", "Directory.Build.targets", "global.json" }.Select(p => Path.Combine(repository, p)).Where(File.Exists)).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(repository, file)));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static object Inspect(string buildRoot)
    {
        string file = Path.Combine(buildRoot, "prepared-build.json");
        if (!File.Exists(file)) return new { sourceStatus = "unavailable; packaged or externally built runtime" };
        var record = TestJson.Read<PreparedBuild>(file);
        bool available = Directory.Exists(Path.Combine(record.Repository, "src"));
        return new { record.SourceIdentity, record.PreparedUtc, sourceStatus = !available ? "source-unavailable" : Source(record.Repository) == record.SourceIdentity ? "current" : "source-changed; stop and prepare before restarting" };
    }
}
