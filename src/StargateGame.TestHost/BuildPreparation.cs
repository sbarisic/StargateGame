using System.Diagnostics;
using StargateGame.Testing;

namespace StargateGame.TestHost;

static class BuildPreparation
{
    public static async Task<object> Prepare(string repository, string buildRoot, string group, bool rendered, bool diagnose, CancellationToken token, string? contentCache = null, bool checks = false)
    {
        repository = Path.GetFullPath(repository); buildRoot = Path.GetFullPath(buildRoot);
        contentCache = Path.GetFullPath(string.IsNullOrEmpty(contentCache) ? Path.Combine(repository, "artifacts", "cooked") : contentCache);
        if (!rendered && Expand(group).Any(g => !CheckCatalog.Groups.Contains(g))) throw new ArgumentException("Unknown check group: " + group);
        var tests = !rendered || checks ? Expand(group).Select(g => g switch
        {
            "harness" => "tests/StargateGame.Harness.Tests", "harness-mcp" => "src/StargateGame.TestHost",
            "ui" => "tests/StargateGame.Ui.Tests", "audio" => "tests/StargateGame.Audio.Tests",
            "assets" or "harness-processes" or "ui-render" => "", _ => "tests/StargateGame.Tests"
        }) : [];
        var projects = (rendered ? new[] { "src/StargateGame.Client", "src/StargateGame.Harness" } : ["src/StargateGame.Harness"])
            .Concat(tests).Where(s => s.Length > 0).Distinct().ToArray();
        var missing = new[] { "global.json", "assets/interaction/dhd.sgdi", "assets/building/prefabs.json", "assets/settlements/prefabs.json", "assets/furnishings/prefabs.json" }.Where(p => !File.Exists(Path.Combine(repository, p))).ToArray();
        if (diagnose) return new { repository, buildRoot, projects, rendered, missing, canBuild = missing.Length == 0 };
        if (missing.Length > 0) throw new FileNotFoundException("Restore local assets first: " + string.Join(", ", missing));
        foreach (var path in Directory.Exists(SessionLauncher.Sessions) ? Directory.EnumerateDirectories(SessionLauncher.Sessions) : [])
        {
            SessionManifest m; try { m = HarnessClient.Manifest(Path.GetFileName(path)); } catch { continue; }
            if (m.BuildRoot.Equals(buildRoot, StringComparison.OrdinalIgnoreCase) && HarnessClient.Alive(m)) throw new InvalidOperationException("Build is in use by session " + m.Id + "; stop it or reuse its binaries.");
        }
        Directory.CreateDirectory(buildRoot);
        using var buildLock = new FileStream(RuntimeLease.PathFor(buildRoot), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var watch = Stopwatch.StartNew();
        foreach (string project in projects)
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "build", Path.Combine(repository, project), "-c", "Release", "--artifacts-path", buildRoot, "--nologo", "-p:CookAssets=" + (rendered ? "true" : "false"), "-p:AssetCookCache=" + contentCache }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(token); } catch { process.Kill(true); throw; }
            string log = Path.Combine(buildRoot, "prepare-" + Path.GetFileName(project) + ".log");
            await File.WriteAllTextAsync(log, await output + await error, CancellationToken.None);
            if (process.ExitCode != 0) throw new InvalidOperationException("Build failed; see " + log);
        }
        if (rendered) TestJson.Write(Path.Combine(buildRoot, "prepared-build.json"), new PreparedBuild(repository, BuildIdentity.Source(repository), DateTimeOffset.UtcNow, projects, watch.Elapsed.TotalSeconds));
        return new { buildRoot, projects, rendered, elapsedSeconds = watch.Elapsed.TotalSeconds };
    }
    public static string[] Expand(string group) => group == "quick" ? ["harness", "tactical-movement", "address-book"] : group == "full" ? CheckCatalog.Groups.Except(["quick", "full", "blender", "regional-profile"]).ToArray() : group.Split(',');
}
