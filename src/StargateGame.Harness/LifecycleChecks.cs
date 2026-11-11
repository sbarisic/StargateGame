using System.Diagnostics;
using System.Runtime.InteropServices;
using StargateGame.Testing;

namespace StargateGame.Harness;

static class LifecycleChecks
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    public static async Task<object> Run(string build, CancellationToken token)
    {
        var cases = new List<object>();
        foreach (string mode in new[] { "startup-cancellation", "commands-and-stop", "server-failure", "client-failure" })
        {
            string id = Guid.NewGuid().ToString("N"), root = SessionLauncher.NewRoot(id);
            using var coordinator = SessionLauncher.Launch(new(id, root, build, new(), OwnerPid: Environment.ProcessId));
            SessionManifest? m = null;
            var watch = Stopwatch.StartNew();
            async Task<TestResult> Send(TestAction action, object? data = null, int timeout = 20, int? generation = null) => await LocalPipe.Send("stargate-test-" + id, new() { Session = id, Generation = generation ?? m!.Generation, Action = action, Data = TestJson.Element(data ?? new { }), TimeoutSeconds = timeout }, token);
            try
            {
                while (watch.Elapsed.TotalSeconds < 180)
                {
                    token.ThrowIfCancellationRequested();
                    string manifest = Path.Combine(root, "session.json");
                    if (File.Exists(manifest))
                    {
                        m = TestJson.Read<SessionManifest>(manifest);
                        GetWindowThreadProcessId(GetForegroundWindow(), out uint foreground);
                        if (m.ClientPids.Contains((int)foreground)) throw new Exception("Automated client stole focus.");
                        if (m.State == "failed") throw new Exception(m.Error);
                        if (mode == "startup-cancellation" && m.ServerPid > 0 || m.State == "ready") break;
                    }
                    if (coordinator.HasExited) throw new Exception("Coordinator exited during startup.");
                    await Task.Delay(50, token);
                }
                if (m is null || watch.Elapsed.TotalSeconds >= 180) throw new TimeoutException("Lifecycle startup.");
                if (mode == "commands-and-stop")
                {
                    var stale = await Send(TestAction.Camera, new { bookmark = "gate-pair" }, generation: m.Generation + 1);
                    if (stale.Outcome != "failed") throw new Exception("Stale generation was accepted.");
                    var movement = await Send(TestAction.Move, new { forward = 1, seconds = 10 }, timeout: 1);
                    if (movement.Outcome != "failed") throw new Exception("Timed out movement succeeded.");
                    var state = await Send(TestAction.Status); state.EnsureSuccess();
                    if (state.Data.GetProperty("clients")[0].GetProperty("syntheticInputActive").GetBoolean()) throw new Exception("Movement remained active after timeout.");
                }
                if (mode.EndsWith("failure"))
                {
                    using var failed = Process.GetProcessById(mode == "server-failure" ? m.ServerPid : m.ClientPids[0]); failed.Kill();
                }
                else (await Send(TestAction.Stop)).EnsureSuccess();
                await coordinator.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(25), token);
                var final = TestJson.Read<SessionManifest>(Path.Combine(root, "session.json"));
                foreach (int pid in final.ClientPids.Append(final.ServerPid))
                    try { using var p = Process.GetProcessById(pid); if (!p.HasExited) throw new Exception("Owned process survived cleanup: " + pid); } catch (ArgumentException) { }
                if (final.State != (mode.EndsWith("failure") ? "failed" : "stopped")) throw new Exception("Incorrect final state: " + final.State);
                // The CLI uses this same terminal-state shortcut, so repeated stop never reconnects to a closed pipe.
                if (final.State is not ("stopped" or "failed")) throw new Exception("Stop is not idempotent.");
                cases.Add(new { mode, outcome = "passed", session = id, seconds = watch.Elapsed.TotalSeconds });
            }
            finally { if (!coordinator.HasExited) coordinator.Kill(true); }
        }
        var command = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { SessionLauncher.Binary(build, "StargateGame.Harness"), "start", "--build-root", build }) command.ArgumentList.Add(argument);
        using var cli = Process.Start(command)!;
        var output = cli.StandardOutput.ReadToEndAsync();
        await cli.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(180), token);
        string json = await output.WaitAsync(TimeSpan.FromSeconds(3), token);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        string persistent = document.RootElement.GetProperty("session").GetString()!;
        var retained = TestJson.Read<SessionManifest>(Path.Combine(SessionLauncher.NewRoot(persistent), "session.json"));
        try
        {
            if (retained.State != "ready") throw new Exception("Persistent start returned before ready.");
            (await LocalPipe.Send(retained.Pipe, new() { Session = persistent, Action = TestAction.Stop }, token)).EnsureSuccess();
            using var owner = Process.GetProcessById(retained.CoordinatorPid); await owner.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(25), token);
            cases.Add(new { mode = "persistent-start-pipeline", outcome = "passed", session = persistent });
        }
        finally { try { using var owner = Process.GetProcessById(retained.CoordinatorPid); if (!owner.HasExited) owner.Kill(true); } catch (ArgumentException) { } }
        return cases;
    }
}
