using System.Diagnostics;
using System.Text.Json;
using StargateGame.Harness;
using StargateGame.Testing;

try
{
    if (args.Length == 0) throw new ArgumentException("Use list, run, start, status, wait, action, capture, stop, or check.");
    string verb = args[0];
    if (verb == "serve")
    {
        var launch = TestJson.Read<LaunchOptions>(args[1]);
        using var log = new StreamWriter(Path.Combine(launch.Root, "coordinator.log"), append: true) { AutoFlush = true };
        Console.SetOut(log); Console.SetError(log);
        await using var coordinator = new Coordinator(launch); await coordinator.Run(); return;
    }
    string Get(string key, string fallback = "") { int i = Array.IndexOf(args, "--" + key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    int Number(string key, int fallback) => int.Parse(Get(key, fallback.ToString()), System.Globalization.CultureInfo.InvariantCulture);
    bool Has(string key) => args.Contains("--" + key);
    void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, TestJson.Options));
    if (verb == "list") { Print(ActionCatalog.Discovery()); return; }
    using var hostCancellation = new CancellationTokenSource();
    if (Has("control-stdin")) _ = Task.Run(async () => { if (await Console.In.ReadLineAsync() == "cancel") hostCancellation.Cancel(); });
    string build = Path.GetFullPath(Get("build-root", AppContext.BaseDirectory));
    if (verb == "check") { Environment.ExitCode = await CheckRunner.Run(build, Get("group", "quick"), Get("repo-root", Directory.GetCurrentDirectory()), args.Where(a => new[] { "--delayed", "--stress", "--explore", "--slow", "--cold" }.Contains(a)).ToArray(), hostCancellation.Token); return; }
    if (verb is "start" or "run")
    {
        var options = ScenarioCatalog.Resolve(new(Get("scenario", "sandbox"), Get("preset", "default"), Number("seed", 7319),
            Get("biome", "temperate"), Get("landscape", "Inland"), Get("settlement", "wilderness"), Get("style", "auto"),
            double.Parse(Get("hour", "12"), System.Globalization.CultureInfo.InvariantCulture), Get("weather", "off"), Get("quality", "performance"),
            Has("cold"), Has("delayed"), Number("terrain-rate", 0), Number("destination-delay", 0), Get("overrides", string.Join(",", new[] { "seed", "biome", "landscape" }.Where(n => Has(n)))).Split(',', StringSplitOptions.RemoveEmptyEntries)));
        string id = Guid.NewGuid().ToString("N"), root = HarnessFiles.SessionRoot(id);
        int count = Number("clients", 1); if (count is < 1 or > 8) throw new ArgumentException("Clients must be 1–8.");
        var launch = new LaunchOptions(id, root, build, options, count, verb == "run" && !Has("keep-open") ? Environment.ProcessId : 0, Manual: false, RetainState: Has("retain-state"));
        Process? process = null;
        SessionManifest? manifest = null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation.Token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        bool success = false; var clock = Stopwatch.StartNew();
        try
        {
            process = SessionLauncher.Launch(launch);
            while (clock.Elapsed.TotalSeconds < Number("timeout", 240))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                string path = Path.Combine(root, "session.json");
                if (File.Exists(path)) { manifest = TestJson.Read<SessionManifest>(path); if (manifest.State == "ready") break; if (manifest.State is "failed" or "stopped") throw new InvalidOperationException(manifest.Error ?? manifest.State); }
                if (process.HasExited) throw new InvalidOperationException("Coordinator exited before readiness.");
                await Task.Delay(100, cancellation.Token);
            }
            if (manifest?.State != "ready") throw new TimeoutException("Session startup timed out: " + root);
            if (verb == "run" && File.Exists(Get("recipe"))) File.Copy(Get("recipe"), Path.Combine(root, "recipe.json"));
            if (verb == "run") await Recipes.Run(manifest, Get("recipe", "visual-check"), cancellation.Token);
            manifest = TestJson.Read<SessionManifest>(Path.Combine(root, "session.json"));
            success = true;
            TestJson.Write(Path.Combine(root, "result.json"), new { outcome = "completed", verification = RecipeCatalog.All.FirstOrDefault(r => r.Id == Get("recipe"))?.Verification ?? "execution", session = id, options, recipe = verb == "run" ? Get("recipe", "visual-check") : null, elapsedSeconds = clock.Elapsed.TotalSeconds });
        }
        catch (Exception e) { TestJson.Write(Path.Combine(root, "result.json"), new { outcome = "failed", session = id, options, recipe = Get("recipe", "visual-check"), error = e.ToString() }); throw new InvalidOperationException($"Run {id} failed. Artifacts: {root}. {e.Message}", e); }
        finally
        {
            if (!success || verb == "run" && !Has("keep-open"))
            {
                try { await LocalPipe.Send("stargate-test-" + id, new() { Session = id, Action = TestAction.Stop, TimeoutSeconds = 3 }); } catch { }
                if (process is { HasExited: false }) try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); } catch (TimeoutException) { process.Kill(true); }
            }
            process?.Dispose();
        }
        var finalManifest = TestJson.Read<SessionManifest>(Path.Combine(root, "session.json"));
        if (finalManifest.State == "failed") { TestJson.Write(Path.Combine(root, "result.json"), new { outcome = "failed", execution = success ? "completed" : "failed", cleanup = "failed", session = id, recipe = Get("recipe"), error = finalManifest.Error }); throw new InvalidOperationException(finalManifest.Error); }
        Print(new { outcome = "completed", execution = success ? "completed" : "failed", cleanup = finalManifest.State == "stopped" ? "completed" : "session-retained", session = id, artifacts = root, elapsedSeconds = clock.Elapsed.TotalSeconds, manifest = TestJson.Read<SessionManifest>(Path.Combine(root, "session.json")) });
        return;
    }
    string session = Get("session"); string sessionRoot = HarnessFiles.SessionRoot(session);
    var m = TestJson.Read<SessionManifest>(Path.Combine(sessionRoot, "session.json"));
    if (m.State is not ("stopped" or "failed"))
    {
        bool alive; try { using var owner = Process.GetProcessById(m.CoordinatorPid); alive = !owner.HasExited && owner.StartTime.ToUniversalTime() >= File.GetLastWriteTimeUtc(Path.Combine(sessionRoot, "launch.json")).AddSeconds(-5); } catch (ArgumentException) { alive = false; }
        if (!alive) { m = m with { State = "failed", Error = "Coordinator exited unexpectedly; its job releases owned processes." }; TestJson.Write(Path.Combine(sessionRoot, "session.json"), m); }
    }
    if (m.State is "stopped" or "failed")
    {
        if (verb is "stop" or "status") { Print(new { outcome = "passed", session, state = m.State, manifest = m }); return; }
        throw new InvalidOperationException("Session is " + m.State + ": " + m.Error);
    }
    var request = verb == "action" ? TestJson.Read<TestRequest>(Get("request")) : new TestRequest
    {
        Action = verb switch { "status" => TestAction.Status, "stop" => TestAction.Stop, "wait" => TestAction.Wait, "capture" => TestAction.Capture, _ => throw new ArgumentException("Unknown operation.") },
        Data = TestJson.Element(new { condition = Get("condition", "captureReady"), name = Get("name", "capture.png") }), TimeoutSeconds = Number("timeout", 120)
    };
    if (request.Session.Length > 0 && request.Session != session) throw new ArgumentException("Request/session mismatch.");
    request = request with { Session = session, Generation = request.Generation == 0 ? m.Generation : request.Generation, Client = Number("client", request.Client) };
    var response = await LocalPipe.Send(m.Pipe, request); Print(response); response.EnsureSuccess();
}
catch (Exception e)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new { outcome = "failed", error = e.Message }, TestJson.Options)); Environment.ExitCode = 1;
}
