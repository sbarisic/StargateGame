using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StargateGame.Testing;

namespace StargateGame.TestHost;

[McpServerToolType]
public sealed class GameTools(HarnessClient client)
{
    public static string Repository { get; set; } = Directory.GetCurrentDirectory();
    public static string BuildRoot { get; set; } = "";
    static string Build(string? root) => string.IsNullOrEmpty(root) ? BuildRoot : Path.GetFullPath(root);
    static CallToolResult Result(object value) => new() { IsError = value is TestResult r && r.Outcome is not ("passed" or "accepted"), StructuredContent = TestJson.Element(value), Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, TestJson.Options) }] };
    static async Task<CallToolResult> Guard(Func<Task<object>> run)
    {
        try { return Result(await run()); }
        catch (Exception e) { return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { outcome = "failed", error = e.Message, code = e.GetType().Name }, TestJson.Options) }] }; }
    }
    [McpServerTool(Name = "game_catalog"), Description("Discover scenarios, typed action schemas, recipes, prerequisites, and test groups. Does not build or launch the game.")]
    public CallToolResult Catalog() => Result(ActionCatalog.Discovery());
    [McpServerTool(Name = "game_prepare"), Description("Diagnose prerequisites or explicitly prepare an incremental build. Refuses builds in use. Discovery does not need preparation.")]
    public Task<CallToolResult> Prepare(bool diagnose = true, bool rendered = true, string group = "quick", string? buildRoot = null, CancellationToken cancellationToken = default)
        => Guard(() => BuildPreparation.Prepare(Repository, Build(buildRoot), group, rendered, diagnose, cancellationToken));
    [McpServerTool(Name = "game_sessions"), Description("List local harness sessions; this does not attach or grant control.")]
    public CallToolResult Sessions() => Result(HarnessClient.Sessions());
    [McpServerTool(Name = "game_start"), Description("Start an AI-owned isolated scenario with prepared binaries. Windows stay visible without requesting focus. Host exit closes the session.")]
    public Task<CallToolResult> Start(ScenarioOptions? options = null, int clients = 1, bool retainState = false, string? buildRoot = null, CancellationToken cancellationToken = default)
        => Guard(async () => await client.Start(Build(buildRoot), options ?? new(), clients, false, retainState, cancellationToken));
    [McpServerTool(Name = "game_attach"), Description("Explicitly attach to a live local session by ID. User-owned clients start observation-only; grant control through F6. Escape revokes control.")]
    public Task<CallToolResult> Attach(string session) => Guard(() => Task.FromResult<object>(client.Attach(session)));
    [McpServerTool(Name = "game_observe"), Description("Compact current state and readiness blockers. Optional sections: ui, gates, inventory, buildings, npcs, physics, rendering, diagnostics, full. World picking uses normalized top-left coordinates.")]
    public Task<CallToolResult> Observe(string session, int targetClient = 1, string[]? sections = null, int offset = 0, int limit = 20, string filter = "", double radius = 25, double? pickX = null, double? pickY = null, CancellationToken cancellationToken = default)
        => Guard(async () => {
            var data = new Dictionary<string, object?> { ["sections"] = sections ?? [], ["offset"] = offset, ["limit"] = limit, ["filter"] = filter, ["radius"] = radius };
            if (pickX.HasValue) { data["pickX"] = pickX.Value; data["pickY"] = pickY ?? .5; }
            return await client.Send(session, new() { Action = TestAction.Observe, Client = targetClient, Data = TestJson.Element(data), TimeoutSeconds = 10 }, cancellationToken);
        });
    [McpServerTool(Name = "game_action"), Description("Submit a typed action; consult game_catalog for its data schema. Returns an operation ID promptly. Reuse request ID only for identical retries. Setup is labeled separately from gameplay.")]
    public Task<CallToolResult> Action(string session, TestRequest request, CancellationToken cancellationToken = default)
        => Guard(async () => await client.Send(session, request with { Async = true }, cancellationToken));
    [McpServerTool(Name = "game_operation"), Description("Inspect, wait up to ten seconds for, or cancel an operation. Cancellation releases synthetic input; it cannot undo committed gameplay effects.")]
    public Task<CallToolResult> Operation(string session, Guid id, bool cancel = false, int waitSeconds = 0, CancellationToken cancellationToken = default)
        => Guard(async () => await client.Send(session, new() { Action = cancel ? TestAction.Cancel : TestAction.Operation, Data = cancel ? TestJson.Element(new { id }) : TestJson.Element(new { id, waitSeconds }), TimeoutSeconds = 15 }, cancellationToken));
    [McpServerTool(Name = "game_capture"), Description("Capture the requested rendered state and return its image and metadata. Transient by default. retain=true keeps evidence. A screenshot is not a visual correctness assertion.")]
    public async Task<CallToolResult> Capture(string session, int targetClient = 1, bool retain = false, string name = "observation.png", bool includeUi = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await client.Send(session, new() { Action = TestAction.Capture, Client = targetClient, Data = TestJson.Element(new { name, retain, includeUi }) }, cancellationToken);
            response.EnsureSuccess();
            string path = response.Data.GetProperty("path").GetString()!;
            string root = Path.GetFullPath(HarnessClient.Manifest(session).Root) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Capture outside owned artifacts.");
            var metadata = TestJson.Read<JsonElement>(response.Data.GetProperty("metadata").GetString()!);
            return new() { StructuredContent = TestJson.Element(new { response, metadata }), Content = [new ImageContentBlock { Data = await File.ReadAllBytesAsync(path, cancellationToken), MimeType = "image/png" }, new TextContentBlock { Text = JsonSerializer.Serialize(new { response, metadata }, TestJson.Options) }] };
        }
        catch (Exception e) { return new() { IsError = true, Content = [new TextContentBlock { Text = e.Message }] }; }
    }
    [McpServerTool(Name = "game_recipe"), Description("Run a registered recipe or a JSON recipe in the attached session; export=true records a replay from the session trace. Long runs return an operation ID.")]
    public Task<CallToolResult> Recipe(string session, string name = "visual-check", bool export = false, CancellationToken cancellationToken = default)
        => Guard(async () => export ? ReplayExport.Export(HarnessClient.Manifest(session)) : await client.Send(session, new() { Action = TestAction.Recipe, Data = TestJson.Element(new { name }), Async = true, TimeoutSeconds = 300 }, cancellationToken));
    [McpServerTool(Name = "game_detach"), Description("Cancel this controller's work and release its attachment without terminating the user's session.")]
    public Task<CallToolResult> Detach(string session) => Guard(() => client.Detach(session));
    [McpServerTool(Name = "game_stop"), Description("Stop this host's owned session and finalize evidence. For a user-owned attached session, only detach. Safe to repeat.")]
    public Task<CallToolResult> Stop(string session) => Guard(() => client.Stop(session));
}
