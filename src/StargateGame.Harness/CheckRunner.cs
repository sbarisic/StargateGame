using System.Diagnostics;
using StargateGame.Testing;

namespace StargateGame.Harness;

static class CheckRunner
{
    public static readonly string[] Groups = ["quick", "full", "harness", "harness-processes", "movement", "tactical-movement", "fixture-gates", "travel", "travel-network", "galaxy", "persistence", "building", "prefab", "settlements", "settlement-seeds", "furnishing-fixtures", "furnishings", "weapons", "weapon-network", "interaction", "streaming", "regional", "regional-hydrology", "regional-seams", "environment", "atmosphere", "horizon", "address-book", "ui", "ui-render", "audio", "assets", "blender", "network", "dhd", "trees", "rocks", "worldgen", "canopy-lod", "planet-debug", "emergence", "forest-fixture", "prop-exit", "cooked-assets", "gate-physics", "palettes", "mounted-palettes", "buried-props", "terrain-physics", "regional-integration", "regional-profile", "streaming-cache", "horizon-network", "environment-network", "atmosphere-network", "audio-events", "gate-transition", "settlement-network", "prefab-network", "gate-build", "meadow-props", "address-book-network"];
    public static async Task<int> Run(string build, string selection, string root, string[] modifiers)
    {
        var selected = selection == "quick" ? new[] { "harness", "tactical-movement", "address-book" } : selection == "full" ? Groups.Except(["quick", "full", "blender", "regional-profile"]).ToArray() : selection.Split(',');
        var results = new List<object>(); bool failed = false;
        string run = Path.Combine(build, "checks", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(run);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        foreach (string group in selected)
        {
            if (!Groups.Contains(group)) throw new ArgumentException("Unknown check group: " + group);
            if (group == "harness-processes")
            {
                try { var detail = await LifecycleChecks.Run(build, cancel.Token); results.Add(new { group, outcome = "passed", detail }); }
                catch (Exception e) { failed = true; results.Add(new { group, outcome = "failed", error = e.ToString() }); }
                continue;
            }
            var commands = new List<(string Executable, string[] Args)>();
            if (group == "assets") foreach (string file in Directory.GetFiles(Path.Combine(root, "tools"), "test_*.py")) commands.Add(("python", [file]));
            else if (group == "blender")
            {
                string? blender = Environment.GetEnvironmentVariable("BLENDER_EXE");
                if (string.IsNullOrEmpty(blender) || !File.Exists(blender)) { results.Add(new { group, outcome = "skipped", reason = "Optional Blender prerequisite missing: set BLENDER_EXE to the installed Blender executable." }); continue; }
                commands.Add(("dotnet", [HarnessFiles.Binary(build, "StargateGame.Tests"), "--furnishing-fixtures"]));
                commands.Add((blender, ["-b", "-t", "8", "--python", Path.Combine(root, "tools/render_furnished_interiors.py")]));
            }
            else if (group is "settlement-seeds" or "furnishing-fixtures") commands.Add(("dotnet", [HarnessFiles.Binary(build, "StargateGame.Tests"), "--" + group]));
            else if (group == "ui-render") foreach (string dpi in new[] { "100", "150", "200" }) commands.Add(("dotnet", [HarnessFiles.Binary(build, "StargateGame.Client"), "--ui-preview", "--ui-check", "--ui-hidden", "--no-focus", "--ui-dpi", dpi, "--capture-dir", Path.Combine(run, "dpi-" + dpi)]));
            else if (group == "audio") commands.Add(("dotnet", ["test", Path.Combine(root, "tests/StargateGame.Audio.Tests"), "-c", "Release", "--artifacts-path", build, "--no-build"]));
            else if (group == "ui") commands.Add(("dotnet", [HarnessFiles.Binary(build, "StargateGame.Ui.Tests")]));
            else if (group == "harness") commands.Add(("dotnet", [HarnessFiles.Binary(build, "StargateGame.Harness.Tests")]));
            else commands.Add(("dotnet", [HarnessFiles.Binary(build, "StargateGame.Tests"), "--group", group, ..modifiers]));
            int exit = 0; int index = 0;
            foreach (var command in commands)
            {
                using var workerOwner = new OwnedProcesses();
                var environment = new Dictionary<string, string> { ["STARGATEGAME_REPO_ROOT"] = root, ["STARGATEGAME_USER_DATA"] = Path.Combine(run, "profile"), ["STARGATEGAME_TERRAIN_CACHE"] = Path.Combine(build, "test-cache", "terrain") };
                var process = workerOwner.Start(command.Executable, command.Args, Path.Combine(run, group + "-" + index++ + ".log"), environment, root);
                await process.WaitForExitAsync(cancel.Token).WaitAsync(TimeSpan.FromMinutes(20), cancel.Token);
                if (process.ExitCode != 0) { exit = process.ExitCode; break; }
            }
            if (exit == 0 && group == "ui-render")
            {
                foreach (int dpi in new[] { 100, 150, 200 })
                {
                    var images = Directory.GetFiles(Path.Combine(run, "dpi-" + dpi), "*.png");
                    if (images.Length != 40) throw new InvalidDataException("Expected 40 UI captures at " + dpi + "% DPI.");
                    foreach (string file in images)
                    {
                        var dimensions = Path.GetFileName(file).Split('-')[0].Split('x').Select(int.Parse).ToArray();
                        byte[] header = File.ReadAllBytes(file);
                        if (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4)) != dimensions[0] * dpi / 100 || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4)) != dimensions[1] * dpi / 100) throw new InvalidDataException("UI framebuffer size: " + file);
                    }
                }
            }
            failed |= exit != 0; results.Add(new { group, outcome = exit == 0 ? "passed" : "failed", exitCode = exit });
        }
        string output = Path.Combine(run, "result.json");
        TestJson.Write(output, new { selected, results }); Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { selected, results, output }, TestJson.Options));
        return failed ? 1 : 0;
    }
}
