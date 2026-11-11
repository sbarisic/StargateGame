using StargateGame.Testing;
using System.Diagnostics;
using System.Reflection;

if (args.Length > 0 && args[0] == "--sleeper") { await Task.Delay(Timeout.Infinite); return; }
if (args.Length > 0 && args[0] == "--job-owner")
{
    using var owned = new OwnedProcesses();
    var child = owned.Start("dotnet", [Assembly.GetExecutingAssembly().Location, "--sleeper"], Path.Combine(args[1], "child.log"));
    File.WriteAllText(Path.Combine(args[1], "pid"), child.Id.ToString());
    await Task.Delay(Timeout.Infinite); return;
}

int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
string runtimeFixture = Path.Combine(Path.GetTempPath(), "stargate-runtime-paths-" + Guid.NewGuid().ToString("N"));
string debugClient = Path.Combine(runtimeFixture, "Client", "bin", "Debug", "net10.0-windows");
Directory.CreateDirectory(debugClient);
var runtimePaths = new[] { "StargateGame.Client", "StargateGame.Server", "StargateGame.Harness" }.Select(project =>
{
    string path = Path.Combine(project == "StargateGame.Client" ? debugClient : Path.Combine(runtimeFixture, project, "bin", "Debug", "net10.0"), project + ".dll");
    Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path;
}).ToArray();
File.WriteAllLines(Path.Combine(debugClient, SessionLauncher.RuntimePathsFile), runtimePaths);
Check(SessionLauncher.FindBuildRoot(debugClient) == debugClient, "VS Debug manifest resolves its own build root");
Check(SessionLauncher.Binary(debugClient, "StargateGame.Server") == runtimePaths[1], "VS Debug uses exact dependency output");
string fallback = Path.Combine(debugClient, "testing", "StargateGame.Server", "StargateGame.Server.dll");
Directory.CreateDirectory(Path.GetDirectoryName(fallback)!); File.WriteAllText(fallback, "stale release fixture");
File.Delete(runtimePaths[1]);
try { SessionLauncher.FindBuildRoot(debugClient); throw new Exception("missing Debug dependency accepted"); } catch (FileNotFoundException) { checks++; }
File.WriteAllText(runtimePaths[1], "fixture");
File.WriteAllLines(Path.Combine(debugClient, SessionLauncher.RuntimePathsFile), runtimePaths.Append(runtimePaths[1]));
try { SessionLauncher.Binary(debugClient, "StargateGame.Server"); throw new Exception("ambiguous runtime accepted"); } catch (FileNotFoundException) { checks++; }
string overlay = Path.Combine(runtimeFixture, "dev-override"); Directory.CreateDirectory(overlay);
File.WriteAllText(Path.Combine(overlay, "manifest.json"), "{}");
File.WriteAllLines(Path.Combine(debugClient, SessionLauncher.RuntimePathsFile), runtimePaths.Append("development-assets=" + overlay));
Check(SessionLauncher.DevelopmentAssets(debugClient) == overlay, "additional clients inherit the development asset overlay");
File.Delete(Path.Combine(overlay, "manifest.json"));
try { SessionLauncher.DevelopmentAssets(debugClient); throw new Exception("missing overlay accepted"); } catch (FileNotFoundException) { checks++; }
var icy = ScenarioCatalog.Resolve(new(Preset: "icy-highlands", Scenario: "planet-explorer"));
Check(icy.Biome == "icy" && icy.Seed == 7322, "curated options resolve identically");
try { ScenarioCatalog.Resolve(new(Scenario: "missing")); throw new Exception("invalid scenario accepted"); } catch (ArgumentException) { checks++; }
try { ScenarioCatalog.Resolve(new(Hour: double.NaN)); throw new Exception("nonfinite hour accepted"); } catch (ArgumentException) { checks++; }
var queue = new TestCommandQueue();
using (var cancel = new CancellationTokenSource())
{
    var pending = queue.Enqueue(new() { Action = TestAction.Move }, cancel.Token); cancel.Cancel();
    try { await pending; } catch (OperationCanceledException) { checks++; }
    bool mutated = false; queue.Drain(r => { mutated = true; return TestResult.Ok(r); }); Check(!mutated, "cancelled queued actions never execute");
}
string name = "stargate-harness-check-" + Guid.NewGuid().ToString("N");
await using (var pipe = new LocalPipe(name, (r, _) => Task.FromResult(r.Generation == 7 ? TestResult.Ok(r, new { ready = true }) : TestResult.Fail(r, new InvalidOperationException("stale")))))
{
    var ok = await LocalPipe.Send(name, new() { Session = "test", Generation = 7 });
    Check(ok.Outcome == "passed" && ok.Data.GetProperty("ready").GetBoolean(), "typed pipe roundtrip");
    var stale = await LocalPipe.Send(name, new() { Session = "test", Generation = 6 }); Check(stale.Outcome == "failed", "stale generation error preserved");
}
var sandbox = StargateGame.Building.SandboxStations.Create(StargateGame.Shared.GraphicsFixture.Create("gate-pair"));
Check(StargateGame.Building.BuildingRules.Unsupported(sandbox).Length == 0, "every sandbox station has valid support");
var door = sandbox.Pieces.First(StargateGame.Building.BuildingPrefabs.IsDoor);
var edited = StargateGame.Building.BuildingRules.Edit(sandbox, new(Guid.NewGuid(), 0, "door", Target: door.Id), (_,_) => 0, (_,_) => true);
Check(edited.State.Pieces.Single(p => p.Id == door.Id).Open, "sandbox generated door opens through gameplay rules");
if (OperatingSystem.IsWindows())
{
    string root = Path.Combine(Path.GetTempPath(), "stargate-job-check-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
    ProcessStartInfo Worker(params string[] arguments) { var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true }; info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); foreach (var a in arguments) info.ArgumentList.Add(a); return info; }
    using var unrelated = Process.Start(Worker("--sleeper"))!;
    using var owner = Process.Start(Worker("--job-owner", root))!;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!File.Exists(Path.Combine(root, "pid"))) { if (owner.HasExited) throw new Exception("Job helper failed."); await Task.Delay(25, timeout.Token); }
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(root, "pid"))));
        owner.Kill(); await owner.WaitForExitAsync(timeout.Token); await child.WaitForExitAsync(timeout.Token);
        Check(child.HasExited, "coordinator crash releases job children");
        Check(!unrelated.HasExited, "unrelated processes survive cleanup");
        using var owned = new OwnedProcesses(); var normal = owned.Start("dotnet", [Assembly.GetExecutingAssembly().Location, "--sleeper"], Path.Combine(root, "normal.log")); int pid = normal.Id;
        owned.Dispose(); owned.Dispose();
        bool running; try { using var probe = Process.GetProcessById(pid); running = !probe.HasExited; } catch (ArgumentException) { running = false; }
        Check(!running, "repeated cleanup is safe");
    }
    finally { if (!owner.HasExited) owner.Kill(true); if (!unrelated.HasExited) unrelated.Kill(true); }
}
Console.WriteLine($"HARNESS {checks} CHECKS PASSED");
