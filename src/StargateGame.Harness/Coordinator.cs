using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StargateGame.Testing;

namespace StargateGame.Harness;

sealed class Coordinator(LaunchOptions launch) : IAsyncDisposable
{
    readonly OwnedProcesses owned = new();
    readonly Operations operations = new();
    readonly AsyncLocal<Guid?> recipeOperation = new();
    readonly object traceLock = new();
    readonly List<(string Pipe, Process? Process)> clients = [];
    readonly CancellationTokenSource stop = new();
    Process? server;
    LocalPipe? endpoint;
    int generation = 1, port;
    string? regionalView;
    ScenarioOptions options = ScenarioCatalog.Resolve(launch.Options);
    string state = "starting", serverPipe = "";
    string? error;
    string Pipe => "stargate-test-" + launch.Session;
    string WorldRoot => Path.Combine(launch.Root, "world-" + generation);
    public SessionManifest Manifest => new(launch.Session, Environment.ProcessId, Pipe, launch.Root, launch.BuildRoot,
        generation, options, state, clients.Select(c => c.Process?.Id ?? launch.OwnerPid).ToArray(), server?.Id ?? 0, error, Process.GetCurrentProcess().StartTime.ToUniversalTime());
    void Save() => TestJson.Write(Path.Combine(launch.Root, "session.json"), Manifest);
    void Trace(object data) { lock (traceLock) File.AppendAllText(Path.Combine(launch.Root, "events.jsonl"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, data }, TestJson.Options) + "\n"); }
    public async Task Run()
    {
        Directory.CreateDirectory(launch.Root); Save(); Trace(new { kind = "starting", manifest = Manifest });
        _ = MonitorOwner();
        FileStream? buildLease = null;
        try
        {
        buildLease = RuntimeLease.Read(launch.BuildRoot);
        TestJson.Write(Path.Combine(launch.Root, "build.json"), new { catalogVersion = ScenarioCatalog.Version, contentCatalog = ContentIdentity(), source = BuildIdentity.Inspect(launch.BuildRoot), native = Directory.EnumerateFiles(Path.GetDirectoryName(Binary("StargateGame.Client"))!, "*.dll").Where(p => new[] { "glfw3.dll", "joltc.dll", "StargateAudioNative.dll" }.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))), binaries = new[] { "StargateGame.Client", "StargateGame.Server", "StargateGame.Harness" }.ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Binary(p))))) });
        endpoint = new LocalPipe(Pipe, Dispatch);
            await StartServer(stop.Token);
            if (launch.AttachedClientPipe is { } attached) clients.Add((attached, null));
            else await AddClient(stop.Token);
            for (int i = clients.Count; i < launch.Clients; i++) await AddClient(stop.Token);
            if (launch.AttachedClientPipe is not null) await ConnectClient(1, stop.Token);
            state = "ready"; Save(); Trace(new { kind = "ready", manifest = Manifest });
            while (!stop.IsCancellationRequested)
            {
                if (launch.OwnerPid > 0)
                {
                    try { using var owner = Process.GetProcessById(launch.OwnerPid); if (owner.HasExited) break; }
                    catch (ArgumentException) { break; }
                }
                if (state == "ready" && (server?.HasExited == true || clients.Any(c => c.Process?.HasExited == true))) throw new InvalidOperationException("An owned client/server exited unexpectedly.");
                await Task.Delay(200, stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { state = "failed"; error = e.ToString(); Trace(new { kind = "failed", error }); }
        finally
        {
            stop.Cancel();
            var cleanupErrors = new List<string>();
            async Task Cleanup(Func<Task> action) { try { await action(); } catch (Exception e) { cleanupErrors.Add(e.ToString()); } }
            if (endpoint is not null) { await Cleanup(() => endpoint.DisposeAsync().AsTask()); endpoint = null; }
            await Cleanup(() => operations.DisposeAsync().AsTask());
            await Cleanup(CloseProcesses);
            if (state != "failed") state = "stopped";
            Trace(new { kind = "stopped", manifest = Manifest }); Save();
            try { owned.Dispose(); } catch (Exception e) { cleanupErrors.Add(e.ToString()); }
            try { SessionArtifacts.Compact(launch); } catch (Exception e) { cleanupErrors.Add(e.ToString()); TestJson.Write(Path.Combine(launch.Root, "retention.json"), new { error = e.Message }); }
            buildLease?.Dispose();
            string execution = state == "failed" ? "failed" : "completed", cleanup = cleanupErrors.Count == 0 ? "completed" : "failed";
            TestJson.Write(Path.Combine(launch.Root, "lifecycle.json"), new { outcome = execution == "failed" || cleanup == "failed" ? "failed" : "passed", execution, cleanup, cleanupErrors, state, error });
            if (state == "failed" || !File.Exists(Path.Combine(launch.Root, "result.json"))) TestJson.Write(Path.Combine(launch.Root, "result.json"), new { outcome = execution == "failed" || cleanup == "failed" ? "failed" : "passed", execution, cleanup, cleanupErrors, state, error });
        }
    }

    object ContentIdentity()
    {
        string path = Path.Combine(Path.GetDirectoryName(Binary("StargateGame.Client"))!, "assets", "packs", "catalog.json");
        string? development = SessionLauncher.DevelopmentAssets(launch.BuildRoot);
        string? overlayManifest = development is null ? null : Path.Combine(development, "manifest.json");
        return new { path, sha256 = File.Exists(path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : null,
            overlayManifest, overlaySha256 = overlayManifest is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(overlayManifest))) };
    }
    async Task CloseProcesses()
    {
        foreach (var client in clients.Where(c => c.Process is { HasExited: false }))
            try { await LocalPipe.Send(client.Pipe, Request(TestAction.Stop, timeout: 2)); await client.Process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        if (server is { HasExited: false }) try { await LocalPipe.Send(serverPipe, Request(TestAction.Stop, timeout: 2)); await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
    }
    async Task MonitorOwner()
    {
        if (launch.OwnerPid <= 0) return;
        try { using var owner = Process.GetProcessById(launch.OwnerPid); await owner.WaitForExitAsync(stop.Token); stop.Cancel(); }
        catch (ArgumentException) { stop.Cancel(); }
        catch (OperationCanceledException) { }
    }
    async Task StartServer(CancellationToken token)
    {
        Directory.CreateDirectory(WorldRoot);
        serverPipe = Pipe + "-server-" + generation;
        var args = new List<string> { Binary("StargateGame.Server"), "--bind", "127.0.0.1", "--port", "0", "--allow-test-identities", "--dev-planets",
            "--test-pipe", serverPipe, "--test-session", launch.Session, "--test-generation", generation.ToString(), "--test-root", WorldRoot,
            "--seed", options.Seed.ToString(), "--preset", options.Biome, "--landscape", options.Landscape,
            "--settlements", options.Settlement, "--settlement-style", options.Style, "--save-dir", Path.Combine(WorldRoot, "saves"), "--universe", "test",
            "--test-scenario", options.Scenario, "--test-preset", options.Preset, "--test-destination-delay", options.DestinationDelayMilliseconds.ToString() };
        if (options.Scenario == "sandbox") args.AddRange(["--fixture", options.Preset == "graphics-lab" ? "graphics-lab" : "gate-pair"]);
        else args.AddRange(["--catalogue", Catalogue()]);
        var env = new Dictionary<string, string> { ["STARGATEGAME_TERRAIN_CACHE"] = Path.Combine(options.Cold ? WorldRoot : Path.Combine(launch.BuildRoot, "test-cache"), "terrain") };
        server = owned.Start("dotnet", args, Path.Combine(WorldRoot, "server.log"), env);
        var ready = await Poll(serverPipe, r => r.Data.TryGetProperty("ready", out var value) && value.GetBoolean(), 180, token);
        port = ready.Data.GetProperty("port").GetInt32();
        regionalView = ready.Data.TryGetProperty("regionalView", out var view) && view.ValueKind == JsonValueKind.String ? view.GetString() : null;
        if (options.Delayed || options.TerrainBytesPerSecond > 0)
        {
            proxy?.Dispose(); proxy = new TestDelayProxy(port, options.Delayed, options.TerrainBytesPerSecond); port = proxy.Port;
        }
        Save();
    }
    TestDelayProxy? proxy;
    string Catalogue()
    {
        foreach (string path in new[] { Path.Combine(Path.GetDirectoryName(Binary("StargateGame.Server"))!, "galaxy", "galaxy.bin"), Path.Combine(launch.BuildRoot, "galaxy", "galaxy.bin") })
            if (File.Exists(path)) return path;
        // Server creates the catalogue once in the harness cache, never in the user's saves.
        return Path.Combine(launch.BuildRoot, "galaxy", "galaxy.bin");
    }
    string Binary(string project) => HarnessFiles.Binary(launch.BuildRoot, project);
    async Task AddClient(CancellationToken token)
    {
        if (clients.Count >= 8) throw new InvalidOperationException("At most eight clients.");
        int index = clients.Count + 1;
        string pipe = Pipe + "-client-" + index;
        var args = new List<string> { Binary("StargateGame.Client"), "--test-pipe", pipe, "--test-session", launch.Session,
            "--test-generation", generation.ToString(), "--test-root", Path.Combine(launch.Root, "client-" + index),
            "--name", "Test-" + launch.Session[..6] + "-" + index, "--graphics", options.Quality, "--vsync", "off", "--width", "1280", "--height", "800", "--no-focus" };
        if (!launch.Manual) args.Add("--test-automated");
        if (SessionLauncher.DevelopmentAssets(launch.BuildRoot) is { } development) args.AddRange(["--dev-assets", development]);
        var env = new Dictionary<string, string> { ["STARGATEGAME_USER_DATA"] = Path.Combine(launch.Root, "client-" + index, "profile") };
        var process = owned.Start("dotnet", args, Path.Combine(launch.Root, "client-" + index + ".log"), env);
        clients.Add((pipe, process)); Save();
        await ConnectClient(index, token);
    }
    TestRequest Request(TestAction action, object? data = null, int client = 1, int timeout = 180) => new() { Session = launch.Session, Generation = generation, Action = action, Data = TestJson.Element(data ?? new { }), Client = client, TimeoutSeconds = timeout };
    async Task ConnectClient(int index, CancellationToken token)
    {
        var r = Request(TestAction.Connect, new { host = "127.0.0.1", port, options, generation, regionalView, root = Path.Combine(launch.Root, "client-" + index) }, index);
        (await LocalPipe.Send(clients[index - 1].Pipe, r, token)).EnsureSuccess();
        await Poll(clients[index - 1].Pipe, Ready("playable"), 180, token);
    }
    static Func<TestResult, bool> Ready(string name) => r => r.Data.TryGetProperty(name, out var b) && b.ValueKind == JsonValueKind.True;
    async Task<TestResult> Poll(string pipe, Func<TestResult, bool> condition, int seconds, CancellationToken token, bool compact = true)
    {
        var watch = Stopwatch.StartNew(); TestResult? last = null;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            token.ThrowIfCancellationRequested();
            if (clients.Any(c => c.Process?.HasExited == true)) throw new InvalidOperationException("Client exited. See client logs in " + launch.Root);
            if (server?.HasExited == true) throw new InvalidOperationException("Server exited. See " + WorldRoot);
            try
            {
                last = await LocalPipe.Send(pipe, Request(TestAction.Status, new { compact }, timeout: 2), token);
                if (last.ErrorCode == "cancelled") { await Task.Delay(50, token); continue; }
                last.EnsureSuccess();
                if (last.Data.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.String) throw new InvalidOperationException(failure.GetString());
                if (condition(last)) return last;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (IOException) { }
            await Task.Delay(50, token);
        }
        throw new TimeoutException("Condition not reached. Last state: " + last?.Data);
    }
    async Task<TestResult> ClientCommand(TestRequest r, CancellationToken token)
    {
        if (r.Client < 1 || r.Client > clients.Count) throw new ArgumentException("Invalid client index.");
        string pipe = clients[r.Client - 1].Pipe;
        var result = await LocalPipe.Send(pipe, r, token);
        if (result.Outcome == "pending")
        {
            var done = await Poll(pipe, s => s.Data.TryGetProperty("operation", out var op) && op.ValueKind == JsonValueKind.Object && op.GetProperty("request").GetGuid() == r.Id && op.GetProperty("outcome").GetString() != "pending", r.TimeoutSeconds, token);
            result = done.Data.GetProperty("operation").Deserialize<TestResult>(TestJson.Options)!;
        }
        result.EnsureSuccess(); return result;
    }
    async Task<TestResult> Dispatch(TestRequest r, CancellationToken token)
    {
        if (r.Session != launch.Session) throw new InvalidOperationException("Wrong session.");
        if (r.Action == TestAction.Operation)
            return TestResult.Ok(r, operations.Read(r.Value<Guid>("id")));
        if (r.Action == TestAction.Cancel)
        {
            var id = r.Value<Guid>("id"); var original = operations.Request(id);
            if (original.Controller.Length > 0 && original.Controller != r.Controller) throw new InvalidOperationException("Operation belongs to another controller.");
            operations.Cancel(id); await ReleaseInput(original, force: true);
            return TestResult.Ok(r, operations.Read(id));
        }
        if (r.Action == TestAction.ReleaseControl)
        {
            operations.CancelController(r.Controller);
            foreach (var client in clients.ToArray())
                try { await LocalPipe.Send(client.Pipe, Request(TestAction.ReleaseControl) with { Controller = r.Controller }, token); } catch { }
            return TestResult.Ok(r);
        }
        if (r.Action == TestAction.Observe)
        {
            if (r.Client < 1 || r.Client > clients.Count) throw new ArgumentException("Invalid client index.");
            var response = await LocalPipe.Send(clients[r.Client - 1].Pipe, r with { Generation = generation }, token);
            response.EnsureSuccess();
            return TestResult.Ok(r with { Generation = generation }, new { manifest = Manifest, operations = operations.Active(), observation = response.Data });
        }
        if (r.Action is TestAction.Status or TestAction.Stop) return await Execute(r, token);
        ActionCatalog.Validate(r);
        return await operations.Submit(r with { Async = true }, Execute, request => ReleaseInput(request), token);
    }
    async Task ReleaseInput(TestRequest r, bool force = false)
    {
        if (!force && r.Action == TestAction.Input && r.Flag("hold")) return;
        if (r.Action is not (TestAction.Move or TestAction.Input or TestAction.Capture or TestAction.Sample or TestAction.Recipe)) return;
        if (r.Client < 1 || r.Client > clients.Count) return;
        (await LocalPipe.Send(clients[r.Client - 1].Pipe, Request(TestAction.Cancel, new { id = r.Id }, r.Client, 2))).EnsureSuccess();
    }
    public async Task<TestResult> Execute(TestRequest r, CancellationToken token)
    {
        if (r.Action == TestAction.Observe) return await Dispatch(r, token);
        if (r.Session != launch.Session) throw new InvalidOperationException("Wrong session.");
        if (r.Action == TestAction.Stop) { _ = Task.Run(async () => { await Task.Delay(100); stop.Cancel(); }); return TestResult.Ok(r); }
        if (r.Action == TestAction.Status)
        {
            var snapshots = new List<JsonElement>();
            if (state == "ready") foreach (var client in clients.ToArray()) snapshots.Add((await LocalPipe.Send(client.Pipe, Request(TestAction.Status, timeout: 3), token)).Data);
            return TestResult.Ok(r, new { manifest = Manifest, clients = snapshots, server = (await LocalPipe.Send(serverPipe, Request(TestAction.Status, timeout: 3), token)).Data });
        }
        if (r.Client < 1 || r.Client > clients.Count) throw new ArgumentException("Invalid target client.");
        if (r.Generation != generation) throw new InvalidOperationException("Scenario generation changed; refresh status.");
        if (state != "ready") throw new InvalidOperationException("Session is " + state);
        using var commandStop = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token); token = commandStop.Token;
        if (r.Controller.Length > 0 && r.Action != TestAction.Capture)
        {
            var permission = await LocalPipe.Send(clients[r.Client - 1].Pipe, Request(TestAction.Observe, new { sections = new[] { "control" } }, r.Client, 3), token);
            var control = permission.Data.GetProperty("control");
            if (!control.GetProperty("allowed").GetBoolean()) throw new InvalidOperationException("control-not-granted: enable AI control in F6.");
            string controller = control.GetProperty("controller").GetString() ?? "";
            if (controller.Length > 0 && controller != r.Controller) throw new InvalidOperationException("Another AI controller owns this client.");
        }
        JsonElement selectors = TestJson.Element(new { });
        if (r.Action is TestAction.Carry or TestAction.Build or TestAction.Npc or TestAction.Weapon or TestAction.SetupProp or TestAction.Aim)
        {
            var before = await ClientCommand(r with { Action = TestAction.Status }, token);
            selectors = ReplayExport.Selectors(before.Data);
        }
        Trace(new { kind = "request", request = r, selectors, setup = r.Action is TestAction.SwitchScenario or TestAction.Relocate or TestAction.PrepareDestination or TestAction.SetupProp or TestAction.Teleport or TestAction.ResetPlayer or TestAction.ResetStation or TestAction.ResetScenario });
        TestResult result;
        try
        {
            switch (r.Action)
            {
                case TestAction.ResetScenario:
                case TestAction.SwitchScenario:
                    operations.CancelGeneration(generation, recipeOperation.Value ?? r.Id);
                    state = "switching"; Save();
                    foreach (var client in clients) (await LocalPipe.Send(client.Pipe, Request(TestAction.Disconnect), token)).EnsureSuccess();
                    if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(token); }
                    proxy?.Dispose(); proxy = null;
                    if (r.Action == TestAction.SwitchScenario) options = ScenarioCatalog.Resolve(r.Data.Deserialize<ScenarioOptions>(TestJson.Options)! with { Overrides = r.Data.EnumerateObject().Select(p => p.Name).Where(n => n is "seed" or "biome" or "landscape").ToArray() });
                    generation++; await StartServer(token);
                    for (int i = 1; i <= clients.Count; i++) await ConnectClient(i, token);
                    state = "ready"; Save(); result = TestResult.Ok(r, Manifest); break;
                case TestAction.Recipe:
                    recipeOperation.Value = r.Id;
                    try { await Recipes.Run(Manifest, r.Text("name"), token, r.Controller, Execute); } finally { recipeOperation.Value = null; }
                    result = TestResult.Ok(r, new { execution = "completed", recipe = r.Text("name") });
                    TestJson.Write(Path.Combine(launch.Root, "result.json"), new { outcome = "completed", recipe = r.Text("name"), verification = RecipeCatalog.All.FirstOrDefault(d => d.Id == r.Text("name"))?.Verification ?? "execution", session = launch.Session, generation }); break;
                case TestAction.Window: result = TestResult.Ok(r, await TestWindow.Apply(Manifest.ClientPids[r.Client - 1], r.Text("state"), token)); break;
                case TestAction.AddClient: await AddClient(token); result = TestResult.Ok(r, Manifest); break;
                case TestAction.Connect: await ConnectClient(r.Client, token); result = TestResult.Ok(r); break;
                case TestAction.PrepareDestination:
                    result = await LocalPipe.Send(serverPipe, r, token);
                    if (result.Outcome != "pending") result.EnsureSuccess();
                    var prepared = await Poll(serverPipe, s => s.Data.GetProperty("operation") is var op && op.ValueKind == JsonValueKind.Object && op.GetProperty("request").GetGuid() == r.Id && op.GetProperty("outcome").GetString() != "pending", r.TimeoutSeconds, token);
                    result = prepared.Data.GetProperty("operation").Deserialize<TestResult>(TestJson.Options)!; result.EnsureSuccess(); break;
                case TestAction.SetupProp:
                case TestAction.Teleport:
                case TestAction.ResetPlayer:
                case TestAction.ResetStation:
                    var snapshot = await ClientCommand(r with { Action = TestAction.Status }, token);
                    var data = new Dictionary<string, JsonElement>(); foreach (var property in r.Data.EnumerateObject()) data[property.Name] = property.Value;
                    data["player"] = snapshot.Data.GetProperty("player").GetProperty("id");
                    data["location"] = snapshot.Data.GetProperty("location");
                    result = await LocalPipe.Send(serverPipe, r with { Data = TestJson.Element(data) }, token); result.EnsureSuccess();
                    if (result.Data.TryGetProperty("revision", out var revision))
                        await Poll(clients[r.Client - 1].Pipe, s => s.Data.TryGetProperty("revision", out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt64() >= revision.GetInt64() && s.Data.GetProperty("playable").GetBoolean(), r.TimeoutSeconds, token);
                    break;
                case TestAction.Assert:
                    result = await ClientCommand(r with { Action = TestAction.Status }, token);
                    if (!Conditions.Matches(result.Data, r.Data, out var evidence)) throw new InvalidOperationException("Assertion failed: " + JsonSerializer.Serialize(evidence, TestJson.Options));
                    result = TestResult.Ok(r, evidence); break;
                case TestAction.Wait:
                    if (r.Data.TryGetProperty("path", out _))
                    {
                        result = await Poll(clients[r.Client - 1].Pipe, s => Conditions.Matches(s.Data, r.Data, out _), r.TimeoutSeconds, token, compact: false); break;
                    }
                    string condition = r.Text("condition", "captureReady");
                    condition = condition switch { "server-ready" => "serverReady", "capture-ready" => "captureReady", _ => condition };
                    if (condition is not ("serverReady" or "playable" or "captureReady")) throw new ArgumentException("Unknown readiness condition.");
                    result = await Poll(condition == "serverReady" ? serverPipe : clients[r.Client - 1].Pipe, Ready(condition == "serverReady" ? "ready" : condition), r.TimeoutSeconds, token); break;
                default: result = await ClientCommand(r, token); break;
            }
        }
        catch (Exception e)
        {
            if (state == "switching") { error = e.Message; state = "failed"; Save(); _ = Task.Run(async () => { await Task.Delay(100); stop.Cancel(); }); }
            // End input even if the caller stops waiting before the client completes.
            if (r.Action == TestAction.Move) try { await LocalPipe.Send(clients[r.Client - 1].Pipe, Request(TestAction.Move, new { seconds = 0 }, r.Client, 2)); } catch { }
            if (r.Action == TestAction.Recipe) TestJson.Write(Path.Combine(launch.Root, "result.json"), new { outcome = e is OperationCanceledException ? "cancelled" : "failed", recipe = r.Text("name"), session = launch.Session, generation, error = e.ToString() });
            if (e is not OperationCanceledException) await RecordFailure(r, e);
            Trace(new { kind = "result", result = TestResult.Fail(r, e) }); throw;
        }
        result = result with { Request = r.Id, Session = r.Session, Generation = generation, Client = r.Client };
        Trace(new { kind = "result", result }); return result;
    }
    async Task RecordFailure(TestRequest request, Exception error)
    {
        object? observed = null; string? capture = null;
        try
        {
            if (request.Client >= 1 && request.Client <= clients.Count)
            {
                string pipe = clients[request.Client - 1].Pipe;
                var snapshot = await LocalPipe.Send(pipe, Request(TestAction.Observe, new { sections = new[] { "diagnostics", "ui" } }, request.Client, 2));
                observed = snapshot.Data;
                await LocalPipe.Send(pipe, Request(TestAction.Cancel, new { id = request.Id }, request.Client, 2));
                string name = "failure-" + request.Id.ToString("N") + ".png";
                var r = Request(TestAction.Capture, new { name, immediate = true, retain = true }, request.Client, 2);
                await LocalPipe.Send(pipe, r);
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 2)
                {
                    var status = await LocalPipe.Send(pipe, Request(TestAction.Status, timeout: 1));
                    if (status.Data.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.Object && operation.GetProperty("request").GetGuid() == r.Id && operation.GetProperty("outcome").GetString() == "passed") { capture = operation.GetProperty("data").GetProperty("path").GetString(); break; }
                    await Task.Delay(50);
                }
            }
        }
        catch { }
        var logs = new Dictionary<string, string[]>();
        foreach (string path in Directory.EnumerateFiles(launch.Root, "*.log*"))
            try { logs[Path.GetFileName(path)] = File.ReadLines(path).TakeLast(40).Select(l => l.Length > 1000 ? l[..1000] : l).ToArray(); } catch { }
        TestJson.Write(Path.Combine(launch.Root, "failure-" + request.Id.ToString("N") + ".json"), new { request, error = error.ToString(), observed, capture, captureReadinessGuaranteed = false, logs });
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); if (endpoint is not null) await endpoint.DisposeAsync(); await operations.DisposeAsync(); proxy?.Dispose(); owned.Dispose(); stop.Dispose(); }
}

static class HarnessFiles
{
    public static string Binary(string root, string project) => SessionLauncher.Binary(root, project);
    public static string Sessions => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame", "Testing", "sessions");
    public static string SessionRoot(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid session ID.");
        return Path.Combine(Sessions, id);
    }
}
