using System.Diagnostics;
using System.Text.Json;

namespace StargateGame.Testing;

/// <summary>Asset-free orchestration shared by CLI and MCP. Never controls an unrelated server.</summary>
public sealed class HarnessClient(string? controllerId = null, bool detachOnDispose = true) : IAsyncDisposable
{
    readonly HashSet<string> owned = [];
    readonly HashSet<string> attached = [];
    readonly string controller = controllerId ?? Environment.ProcessId + ":" + Guid.NewGuid().ToString("N");
    public static SessionManifest Manifest(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid session ID.");
        return TestJson.Read<SessionManifest>(Path.Combine(SessionLauncher.NewRoot(id), "session.json"));
    }
    public static bool Alive(SessionManifest m)
    {
        if (m.State is "stopped" or "failed") return false;
        try {
            using var p = Process.GetProcessById(m.CoordinatorPid);
            var expected = m.CoordinatorStartedUtc?.UtcDateTime ?? File.GetLastWriteTimeUtc(Path.Combine(m.Root, "launch.json"));
            return !p.HasExited && Math.Abs((p.StartTime.ToUniversalTime() - expected).TotalSeconds) < 5;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public static object[] Sessions()
    {
        if (!Directory.Exists(SessionLauncher.Sessions)) return [];
        return Directory.EnumerateDirectories(SessionLauncher.Sessions).Select(path =>
        {
            try { var m = Manifest(Path.GetFileName(path)); return (object)new { m.Id, m.Options.Scenario, m.BuildRoot, m.Generation, state = Alive(m) ? m.State : "stopped", m.ClientPids, build = Alive(m) ? BuildIdentity.Inspect(m.BuildRoot) : null, attachedMenu = TestJson.Read<LaunchOptions>(Path.Combine(path, "launch.json")).AttachedClientPipe is not null }; }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException) { return null; }
        }).Where(x => x is not null).ToArray()!;
    }
    public SessionManifest Attach(string id)
    {
        var m = Manifest(id); if (!Alive(m) || m.State is "failed" or "stopped") throw new InvalidOperationException("Session is not live.");
        attached.Add(id); return m;
    }
    public async Task<SessionManifest> Start(string build, ScenarioOptions options, int clients, bool persistent, bool retainState, CancellationToken token)
    {
        if (clients is < 1 or > 8) throw new ArgumentException("Clients must be 1–8.");
        string id = Guid.NewGuid().ToString("N"), root = SessionLauncher.NewRoot(id);
        Process? process = null;
        try
        {
            process = SessionLauncher.Launch(new(id, root, Path.GetFullPath(build), ScenarioCatalog.Resolve(options), clients,
                persistent ? 0 : Environment.ProcessId, Manual: persistent, RetainState: retainState));
            owned.Add(id); attached.Add(id);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(240));
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("Coordinator exited; inspect " + root);
                if (File.Exists(Path.Combine(root, "session.json")))
                {
                    var m = Manifest(id);
                    if (m.State == "ready") { if (persistent) owned.Remove(id); return m; }
                    if (m.State == "failed") throw new InvalidOperationException(m.Error);
                }
                await Task.Delay(75, deadline.Token);
            }
        }
        catch (Exception e)
        {
            TestJson.Write(Path.Combine(root, "result.json"), new { session = id, options, outcome = "failed", stage = "startup", error = e.ToString() });
            if (process is { HasExited: false })
            {
                try { await LocalPipe.Send("stargate-test-" + id, new() { Session = id, Action = TestAction.Stop, TimeoutSeconds = 2 }); } catch { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
            }
            if (File.Exists(Path.Combine(root, "session.json"))) RecoverStopped(Manifest(id), e.Message);
            owned.Remove(id); attached.Remove(id); throw;
        }
        finally { process?.Dispose(); }
    }
    public async Task<TestResult> Send(string id, TestRequest r, CancellationToken token)
    {
        if (!attached.Contains(id)) throw new InvalidOperationException("Attach to this session explicitly first.");
        var m = Manifest(id);
        if (!Alive(m)) throw new InvalidOperationException("Coordinator is not running.");
        r = r with { Session = id, Generation = r.Generation == 0 ? m.Generation : r.Generation, Controller = controller };
        ActionCatalog.Validate(r);
        try { return await LocalPipe.Send(m.Pipe, r, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { await LocalPipe.Send(m.Pipe, new() { Session = id, Generation = m.Generation, Controller = controller, Action = TestAction.Cancel, Data = TestJson.Element(new { id = r.Id }), TimeoutSeconds = 3 }); } catch { }
            throw;
        }
    }
    public async Task<object> Detach(string id)
    {
        if (attached.Remove(id)) try { var m = Manifest(id); await LocalPipe.Send(m.Pipe, new() { Session = id, Generation = m.Generation, Action = TestAction.ReleaseControl, Controller = controller, TimeoutSeconds = 3 }); } catch { }
        return new { session = id, detached = true };
    }
    public async Task<object> Stop(string id, bool explicitlyOwned = false)
    {
        var m = Manifest(id);
        if (!owned.Contains(id) && !explicitlyOwned) return await Detach(id);
        if (Alive(m))
        {
            try { await LocalPipe.Send(m.Pipe, new() { Session = id, Action = TestAction.Stop, TimeoutSeconds = 3 }); } catch { }
            using var process = Process.GetProcessById(m.CoordinatorPid);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
        }
        RecoverStopped(Manifest(id), "Coordinator required forced cleanup or exited unexpectedly.");
        owned.Remove(id); attached.Remove(id);
        return new { session = id, manifest = Manifest(id), stopped = true };
    }
    static void RecoverStopped(SessionManifest manifest, string reason)
    {
        if (Alive(manifest) || manifest.State is "stopped" or "failed") return;
        manifest = manifest with { State = "failed", Error = reason };
        TestJson.Write(Path.Combine(manifest.Root, "session.json"), manifest);
        TestJson.Write(Path.Combine(manifest.Root, "result.json"), new { outcome = "failed", session = manifest.Id, stage = "coordinator-exit", error = reason });
        try { SessionArtifacts.Compact(TestJson.Read<LaunchOptions>(Path.Combine(manifest.Root, "launch.json"))); }
        catch (Exception e) { TestJson.Write(Path.Combine(manifest.Root, "retention.json"), new { outcome = "failed", error = e.Message }); }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (string id in owned.ToArray()) try { await Stop(id); } catch { }
        if (detachOnDispose) foreach (string id in attached.ToArray()) await Detach(id);
    }
}
