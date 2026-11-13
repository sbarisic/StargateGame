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
using (var lease = RuntimeLease.Read(runtimeFixture))
{
    using var another = RuntimeLease.Read(runtimeFixture);
    try { using var writer = new FileStream(RuntimeLease.PathFor(runtimeFixture), FileMode.Open, FileAccess.ReadWrite, FileShare.None); throw new Exception("Active runtime overwritten"); }
    catch (IOException) { checks++; }
}
File.Delete(RuntimeLease.PathFor(runtimeFixture));
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
    finally { if (!owner.HasExited) owner.Kill(true); if (!unrelated.HasExited) { unrelated.Kill(true); await unrelated.WaitForExitAsync(); }
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture outside temp root"); Directory.Delete(root, true); }
}

await using (var operations = new Operations())
{
    int executions = 0, releases = 0;
    var request = new TestRequest { Session = "unit", Generation = 1, Action = TestAction.Move, Async = true, TimeoutSeconds = 3 };
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<TestResult> Execute(TestRequest r, CancellationToken token) { Interlocked.Increment(ref executions); started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return TestResult.Ok(r); }
    Task Release(TestRequest r) { Interlocked.Increment(ref releases); return Task.CompletedTask; }
    var accepted = await operations.Submit(request, Execute, Release, CancellationToken.None);
    Check(accepted.Outcome == "accepted", "asynchronous operation returns promptly");
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await operations.Submit(request, Execute, Release, CancellationToken.None);
    Check(executions == 1, "identical request is deduplicated");
    try { await operations.Submit(request with { Client = 2 }, Execute, Release, CancellationToken.None); throw new Exception("conflict accepted"); } catch (InvalidOperationException) { checks++; }
    operations.Cancel(request.Id);
    var terminal = await operations.Wait(request.Id, 2, CancellationToken.None);
    Check(terminal.State == "cancelled" && releases == 1, "cancellation finalizes and releases input");
    var timeout = request with { Id = Guid.NewGuid(), TimeoutSeconds = 1 };
    await operations.Submit(timeout, Execute, Release, CancellationToken.None);
    Check((await operations.Wait(timeout.Id, 3, CancellationToken.None)).State == "timed-out", "deadline distinguished from cancellation");
}
await using (var operations = new Operations())
{
    var r = new TestRequest { Session = "unit", Generation = 2, Action = TestAction.Input };
    var result = await operations.Submit(r, (r, _) => Task.FromResult(TestResult.Ok(r)), _ => throw new IOException("release failed"), CancellationToken.None);
    Check(result.Outcome == "passed" && operations.Read(r.Id).CleanupError!.Contains("release failed"), "cleanup failure is separate from execution");
    Check(operations.Request(r.Id) == r, "completed hold retains its request for explicit cancellation");
}
string concurrentName = "stargate-concurrent-" + Guid.NewGuid().ToString("N");
var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using (var pipe = new LocalPipe(concurrentName, async (r, token) => {
    if (r.Action == TestAction.Move) { blocked.TrySetResult(); await Task.Delay(1000, token); }
    return TestResult.Ok(r);
}))
{
    var slow = LocalPipe.Send(concurrentName, new() { Action = TestAction.Move });
    await blocked.Task;
    var status = LocalPipe.Send(concurrentName, new() { Action = TestAction.Status });
    await status.WaitAsync(TimeSpan.FromMilliseconds(800));
    Check(!slow.IsCompleted, "status bypasses a long running pipe request");
    await slow;
}
await using (var operations = new Operations())
await using (var pipe = new LocalPipe("stargate-waiters-" + name, async (r, token) =>
{
    if (r.Action == TestAction.Operation) return TestResult.Ok(r, operations.Read(r.Value<Guid>("id")));
    if (r.Action == TestAction.Cancel) { operations.Cancel(r.Value<Guid>("id")); return TestResult.Ok(r); }
    if (r.Action == TestAction.Status) return TestResult.Ok(r);
    return await operations.Submit(r with { Async = true }, async (r, t) => { await Task.Delay(200, t); return TestResult.Ok(r); }, _ => Task.CompletedTask, token);
}))
{
    var pending = Enumerable.Range(0, 12).Select(_ => LocalPipe.Send("stargate-waiters-" + name, new() { Action = TestAction.Move, TimeoutSeconds = 10 })).ToArray();
    var status = LocalPipe.Send("stargate-waiters-" + name, new() { Action = TestAction.Status });
    await status.WaitAsync(TimeSpan.FromSeconds(1));
    Check(pending.Any(p => !p.IsCompleted), "twelve synchronous callers do not occupy control connections");
    await Task.WhenAll(pending);
}
Check(Conditions.Matches(TestJson.Element(new { x = 3.1 }), TestJson.Element(new { path = "x", near = 3, tolerance = .2 }), out _), "numeric tolerance condition");
Check(Conditions.Matches(TestJson.Element(new { }), TestJson.Element(new { path = "missing", present = false }), out _), "absence condition");
try { ActionCatalog.Validate(new() { Action = TestAction.Move, Data = TestJson.Element(new { seconds = 1, typo = 2 }) }); throw new Exception("unknown field accepted"); } catch (ArgumentException) { checks++; }
ActionCatalog.Validate(new() { Action = TestAction.Npc, Data = TestJson.Element(new { command = new { action = "spawn", order = (object?)null } }) }); checks++;
ActionCatalog.Validate(new() { Action = TestAction.Settings, Data = TestJson.Element(new { visualTime = (double?)null, shadows = false }) }); checks++;
try { ActionCatalog.Validate(new() { Action = TestAction.Settings, Data = TestJson.Element(new { shadows = "off" }) }); throw new Exception("invalid setting type accepted"); } catch (ArgumentException) { checks++; }
Check(RecipeCatalog.All.Select(r => r.Id).Distinct().Count() == RecipeCatalog.All.Length, "recipe registry has unique entries");
// The fixture is an exact task-created temporary directory, never a user save.
if (!Path.GetFullPath(runtimeFixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture outside temp root");
Directory.Delete(runtimeFixture, true);
Console.WriteLine($"HARNESS {checks} CHECKS PASSED");
