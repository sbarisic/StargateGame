using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using StargateGame.Testing;

namespace StargateGame.Harness;

sealed class Coordinator(LaunchOptions launch) : IAsyncDisposable
{
    readonly OwnedProcesses owned = new();
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
        generation, options, state, clients.Select(c => c.Process?.Id ?? launch.OwnerPid).ToArray(), server?.Id ?? 0, error);
    void Save() => TestJson.Write(Path.Combine(launch.Root, "session.json"), Manifest);
    void Trace(object data) => File.AppendAllText(Path.Combine(launch.Root, "events.jsonl"), JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, data }, TestJson.Options) + "\n");
    public async Task Run()
    {
        Directory.CreateDirectory(launch.Root); Save(); Trace(new { kind = "starting", manifest = Manifest });
        _ = MonitorOwner();
        TestJson.Write(Path.Combine(launch.Root, "build.json"), new { catalogVersion = ScenarioCatalog.Version, contentCatalog = ContentIdentity(), binaries = new[] { "StargateGame.Client", "StargateGame.Server", "StargateGame.Harness" }.ToDictionary(p => p, p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Binary(p))))) });
        endpoint = new LocalPipe(Pipe, Execute);
        try
        {
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
        finally { stop.Cancel(); if (endpoint is not null) { await endpoint.DisposeAsync(); endpoint = null; } await CloseProcesses(); if (state != "failed") state = "stopped"; Trace(new { kind = "stopped", manifest = Manifest }); Save();
            TestJson.Write(Path.Combine(launch.Root, "lifecycle.json"), new { outcome = state == "failed" ? "failed" : "passed", state, error });
            if (state == "failed" || !File.Exists(Path.Combine(launch.Root, "result.json"))) TestJson.Write(Path.Combine(launch.Root, "result.json"), new { outcome = state == "failed" ? "failed" : "passed", state, error });
            owned.Dispose(); }
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
    async Task<TestResult> Poll(string pipe, Func<TestResult, bool> condition, int seconds, CancellationToken token)
    {
        var watch = Stopwatch.StartNew(); TestResult? last = null;
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            token.ThrowIfCancellationRequested();
            if (clients.Any(c => c.Process?.HasExited == true)) throw new InvalidOperationException("Client exited. See client logs in " + launch.Root);
            if (server?.HasExited == true) throw new InvalidOperationException("Server exited. See " + WorldRoot);
            try
            {
                last = await LocalPipe.Send(pipe, Request(TestAction.Status, timeout: 2), token);
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
    public async Task<TestResult> Execute(TestRequest r, CancellationToken token)
    {
        if (r.Session != launch.Session) throw new InvalidOperationException("Wrong session.");
        if (r.Action == TestAction.Stop) { _ = Task.Run(async () => { await Task.Delay(100); stop.Cancel(); }); return TestResult.Ok(r); }
        if (r.Action == TestAction.Status)
        {
            var snapshots = new List<JsonElement>();
            if (state == "ready") foreach (var client in clients) snapshots.Add((await LocalPipe.Send(client.Pipe, Request(TestAction.Status, timeout: 3), token)).Data);
            return TestResult.Ok(r, new { manifest = Manifest, clients = snapshots, server = (await LocalPipe.Send(serverPipe, Request(TestAction.Status), token)).Data });
        }
        if (r.Client < 1 || r.Client > clients.Count) throw new ArgumentException("Invalid target client.");
        if (r.Generation != generation) throw new InvalidOperationException("Scenario generation changed; refresh status.");
        if (state != "ready") throw new InvalidOperationException("Session is " + state);
        using var commandStop = CancellationTokenSource.CreateLinkedTokenSource(token, stop.Token); token = commandStop.Token;
        Trace(new { kind = "request", request = r, setup = r.Action is TestAction.SwitchScenario or TestAction.Relocate or TestAction.PrepareDestination or TestAction.SetupProp or TestAction.Teleport or TestAction.ResetPlayer or TestAction.ResetStation or TestAction.ResetScenario });
        TestResult result;
        try
        {
            switch (r.Action)
            {
                case TestAction.ResetScenario:
                case TestAction.SwitchScenario:
                    state = "switching"; Save();
                    foreach (var client in clients) (await LocalPipe.Send(client.Pipe, Request(TestAction.Disconnect), token)).EnsureSuccess();
                    if (server is { HasExited: false }) { server.Kill(true); await server.WaitForExitAsync(token); }
                    proxy?.Dispose(); proxy = null;
                    if (r.Action == TestAction.SwitchScenario) options = ScenarioCatalog.Resolve(r.Data.Deserialize<ScenarioOptions>(TestJson.Options)! with { Overrides = r.Data.EnumerateObject().Select(p => p.Name).Where(n => n is "seed" or "biome" or "landscape").ToArray() });
                    generation++; await StartServer(token);
                    for (int i = 1; i <= clients.Count; i++) await ConnectClient(i, token);
                    state = "ready"; Save(); result = TestResult.Ok(r, Manifest); break;
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
                    JsonElement actual = result.Data;
                    foreach (var part in r.Text("path").Split('.')) actual = actual.ValueKind == JsonValueKind.Array ? actual[int.Parse(part)] : actual.GetProperty(part);
                    if (!JsonElement.DeepEquals(actual, r.Data.GetProperty("equals"))) throw new InvalidOperationException("Assertion failed: " + r.Text("path") + " = " + actual);
                    result = TestResult.Ok(r, new { path = r.Text("path"), actual }); break;
                case TestAction.Wait:
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
            Trace(new { kind = "result", result = TestResult.Fail(r, e) }); throw;
        }
        result = result with { Request = r.Id, Session = r.Session, Generation = generation, Client = r.Client };
        Trace(new { kind = "result", result }); return result;
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); if (endpoint is not null) await endpoint.DisposeAsync(); proxy?.Dispose(); owned.Dispose(); stop.Dispose(); }
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
