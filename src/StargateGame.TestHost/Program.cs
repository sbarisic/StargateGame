using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using StargateGame.Testing;
using StargateGame.TestHost;

string Get(string key, string fallback = "") { int i = Array.IndexOf(args, "--" + key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
bool Has(string key) => args.Contains("--" + key);
int Number(string key, int fallback) => int.Parse(Get(key, fallback.ToString()), System.Globalization.CultureInfo.InvariantCulture);
void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, TestJson.Options));
string operation = args.FirstOrDefault() ?? "list", repository = Path.GetFullPath(Get("repo-root", Directory.GetCurrentDirectory()));
string build = Path.GetFullPath(Get("build-root", Path.Combine(repository, "artifacts", "testing-build")));
GameTools.Repository = repository; GameTools.BuildRoot = build;
try
{
    if (operation == "mcp")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders(); builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<HarnessClient>();
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<GameTools>(new JsonSerializerOptions(TestJson.Options) { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
        using var host = builder.Build();
        try { await host.RunAsync(); } finally { await host.Services.GetRequiredService<HarnessClient>().DisposeAsync(); }
        return;
    }
    using var cancellation = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    if (operation == "list") { Print(ActionCatalog.Discovery()); return; }
    if (operation == "sessions") { Print(HarnessClient.Sessions()); return; }
    if (operation is "prepare" or "doctor") { Print(await BuildPreparation.Prepare(repository, build, Get("group", "quick"), !Has("headless"), operation == "doctor", cancellation.Token, Get("content-cache"))); return; }
    await using var client = new HarnessClient("cli", detachOnDispose: false);
    if (operation is "start" or "run" or "check")
    {
        bool rendered = operation != "check" || BuildPreparation.Expand(Get("group", "quick")).Any(g => g is "harness-processes" or "ui-render");
        if (!Has("no-build")) await BuildPreparation.Prepare(repository, build, Get("group", "quick"), rendered, false, cancellation.Token, Get("content-cache"), checks: operation == "check");
        // The existing executable owns run/check reporting and persistent CLI-start semantics.
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add(SessionLauncher.Binary(build, "StargateGame.Harness"));
        foreach (string arg in args) info.ArgumentList.Add(arg);
        info.ArgumentList.Add("--control-stdin");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
        var errors = process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        try { await process.WaitForExitAsync(cancellation.Token); }
        catch (OperationCanceledException)
        {
            await process.StandardInput.WriteLineAsync("cancel"); await process.StandardInput.FlushAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); } catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
        }
        await Task.WhenAll(output, errors);
        Environment.ExitCode = process.ExitCode; return;
    }
    string session = Get("session"); var manifest = HarnessClient.Manifest(session);
    if (operation == "cleanup") { Print(SessionArtifacts.Compact(TestJson.Read<LaunchOptions>(Path.Combine(manifest.Root, "launch.json")), !Has("delete"))); return; }
    if (operation == "stop") { Print(await client.Stop(session, explicitlyOwned: true)); return; }
    if (operation == "status" && !HarnessClient.Alive(manifest)) { Print(new { manifest, stopped = true }); return; }
    client.Attach(session);
    if (operation == "attach") { Print(manifest); return; }
    if (operation == "detach") { Print(await client.Detach(session)); return; }
    if (operation == "recipe" && Has("export")) { Print(ReplayExport.Export(manifest)); return; }
    var request = operation == "action" ? TestJson.Read<TestRequest>(Get("request")) : new TestRequest
    {
        Action = operation switch { "status" => TestAction.Status, "observe" => TestAction.Observe, "capture" => TestAction.Capture, "wait" => TestAction.Wait, "operation" => TestAction.Operation, "cancel" => TestAction.Cancel, "recipe" => TestAction.Recipe, _ => throw new ArgumentException("Unknown operation.") },
        TimeoutSeconds = Number("timeout", 120),
        Data = operation switch
        {
            "status" => TestJson.Element(new { }),
            "observe" => TestJson.Element(new { sections = Get("sections").Split(',', StringSplitOptions.RemoveEmptyEntries), offset = Number("offset", 0), limit = Number("limit", 20), filter = Get("filter") }),
            "capture" => TestJson.Element(new { name = Get("name", "capture.png"), retain = true }),
            "operation" => TestJson.Element(new { id = Get("id"), waitSeconds = Number("wait-seconds", 0) }),
            "cancel" => TestJson.Element(new { id = Get("id") }),
            "recipe" => TestJson.Element(new { name = Get("recipe", "visual-check") }),
            _ => TestJson.Element(new { condition = Get("condition", "captureReady") })
        }
    };
    if (Has("client")) request = request with { Client = Number("client", 1) };
    if (Has("async")) request = request with { Async = true };
    var response = await client.Send(session, request, cancellation.Token); Print(response);
    if (response.Outcome is not ("passed" or "accepted")) Environment.ExitCode = 1;
}
catch (Exception e)
{
    string? output = null;
    if (operation is "run" or "check" or "prepare")
    {
        output = Path.Combine(build, "checks", "host-failure-" + Guid.NewGuid().ToString("N"), "result.json");
        TestJson.Write(output, new { outcome = "failed", operation, recipe = Get("recipe"), group = Get("group"), error = e.ToString(), cleanup = "no-session-launched" });
    }
    Print(new { outcome = "failed", error = e.Message, code = e.GetType().Name, output }); Environment.ExitCode = 1;
}
