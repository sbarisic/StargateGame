using System.Numerics;
using System.Text.Json;
using System.Security.Cryptography;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Testing;
using StargateGame.Building;

namespace StargateGame.Client;

sealed partial class GameApp
{
    InputManager? testInput;
    bool testAiGranted, testRawInputActive, testAiRevoked;
    string testController = "";
    System.Diagnostics.Process? testControllerProcess;
    double testInputUntil;
    Guid testInputRequest;
    bool AiInputOwned => TestAutomated && !testAiRevoked || testAiGranted;
    sealed record TestUiControl(string Id, string Label, bool Enabled, UiRect Bounds);
    readonly Dictionary<string, TestUiControl> testUi = [];
    void RegisterTestUi(string id, string label, UiRect bounds, bool enabled)
    {
        if (testPipe is not null) testUi[id] = new(id, label, enabled, bounds);
    }
    string TestUiRevision => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { controls = testUi.Values, width = UiWidth, height = UiHeight }, TestJson.Options)))[..16];
    object TestReadiness() => new
    {
        playable = TestPlayable, captureReady = TestCaptureReady,
        blockers = new Dictionary<string, bool>
        {
            ["connection"] = session is not null, ["scene"] = scene is not null,
            ["playable-frame"] = firstPlayablePresented, ["arrival"] = session is not null && !session.PlanetVisualPending && !session.TransitionPending && !session.TransitLocked && !session.AwaitingArrivalFrame,
            ["subsequent-frame"] = testFrames > testChangedFrame + 1,
            ["inspection-assets"] = scene?.InspectionAssetsReady == true, ["deferred"] = scene?.DeferredCaptureReady == true,
            ["meshes"] = scene?.PendingMeshes == 0, ["canopies"] = scene?.WorldCanopiesPending == 0,
            ["view-terrain"] = scene?.ArrivalRegionReady(camera.Position) == true,
            ["weather-check"] = scene is not null && (!scene.WeatherDiagnostics || scene.WeatherChecksComplete),
            ["landscape-check"] = scene is not null && (!scene.LandscapeDiagnostics || scene.LandscapeChecksComplete)
        }.Where(p => !p.Value).Select(p => new { name = p.Key, reason = p.Key switch
        {
            "connection" => "No connected gameplay session", "scene" => "Renderer has no world scene", "playable-frame" => "First playable frame has not been presented",
            "arrival" => "Destination loading or arrival acknowledgment pending", "subsequent-frame" => "Requested changes need a subsequent rendered frame",
            "inspection-assets" => "Visible inspection models are pending", "deferred" => "Deferred rendering assets are pending",
            "meshes" => "Terrain meshes pending: " + scene?.PendingMeshes, "canopies" => "Canopy work pending: " + scene?.WorldCanopiesPending,
            "view-terrain" => "Terrain around the requested view is not ready", _ => "Requested diagnostic check has not completed"
        } }).ToArray()
    };
    object ObserveTest(TestRequest r)
    {
        int offset = Math.Max(0, r.Int("offset")), limit = Math.Clamp(r.Int("limit", 20), 1, 100);
        string filter = r.Text("filter");
        string[] sections = r.Data.TryGetProperty("sections", out var fields) ? fields.Deserialize<string[]>()! : [];
        var known = new[] { "control", "ui", "gates", "inventory", "buildings", "npcs", "physics", "rendering", "diagnostics", "full" };
        if (sections.Any(s => !known.Contains(s))) throw new ArgumentException("Unknown observation section.");
        var result = new Dictionary<string, object?>
        {
            ["session"] = testSession, ["generation"] = testGeneration, ["observedUtc"] = DateTimeOffset.UtcNow,
            ["ageMilliseconds"] = 0, ["frame"] = testFrames, ["simulationTick"] = session?.ServerTick, ["presentationTick"] = session?.PresentationTick,
            ["stateSources"] = new { player = "predicted", camera = "rendered", entities = "replicated-authoritative definitions", bodies = "predicted" },
            ["location"] = session?.World.Definition.LocationId, ["player"] = session?.Local,
            ["camera"] = new { eye = camera.Position, target = camera.Position + camera.WorldForwardNormal, view = testView },
            ["viewport"] = new { width = window.Width, height = window.Height, uiWidth = UiWidth, uiHeight = UiHeight },
            ["readiness"] = TestReadiness(), ["operation"] = testOperation,
            ["control"] = new { allowed = AiInputOwned, controller = testController, syntheticInputActive = testRawInputActive || testMoveUntil > timer.Elapsed.TotalSeconds }
        };
        if (session is not null)
        {
            double radius = r.Number("radius", 25); if (!double.IsFinite(radius) || radius < 0 || radius > 4096) throw new ArgumentException("Radius must be 0–4096 metres.");
            var bodyPositions = session.Physics.Bodies().ToDictionary(b => b.Id, b => b.Position);
            var nearby = session.World.Definition.Entities.Select(e => new { id = e.Id, kind = e.Model, position = e.Position })
                .Concat(session.World.Definition.Props.Select(p => new { id = p.PersistentId.ToString(), kind = p.Model, position = bodyPositions.GetValueOrDefault(p.Id, p.Position) }))
                .Concat(session.PresentedNpcs().Select(n => new { id = n.Id.ToString(), kind = "npc", position = n.Position }))
                .Where(e => Vector3.Distance(e.position, session.Local.Position) <= radius && (e.kind.Contains(filter, StringComparison.OrdinalIgnoreCase) || e.id.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
            result["nearby"] = new { total = nearby.Length, offset, limit, items = nearby.Skip(offset).Take(limit) };
            if (sections.Contains("gates")) result["gates"] = session.Gates.Values.ToArray();
            if (sections.Contains("inventory")) result["inventory"] = new { personal = session.Inventory, container = session.OpenWeaponContainer };
            if (sections.Contains("buildings")) result["buildings"] = new { revision = session.Changes.Revision, total = session.Changes.Pieces.Length, items = session.Changes.Pieces.Skip(offset).Take(limit), structures = session.Changes.Structures.Skip(offset).Take(limit) };
            if (sections.Contains("npcs")) result["npcs"] = new { records = session.Npcs.Records.Skip(offset).Take(limit), status = session.Npcs.Status.Skip(offset).Take(limit), motionSource = "rendered", motions = session.PresentedNpcs().Skip(offset).Take(limit), navigation = session.NpcDiagnostics };
            if (sections.Contains("physics")) result["physics"] = new { bodies = session.Physics.Bodies().Skip(offset).Take(limit), carries = session.Physics.Carries };
            if (r.Data.TryGetProperty("pickX", out _))
            {
                float x = (float)r.Number("pickX"), y = (float)r.Number("pickY");
                if (x is < 0 or > 1 || y is < 0 or > 1 || !float.IsFinite(x + y)) throw new ArgumentException("Pick coordinates must be normalized 0–1.");
                Matrix4x4.Invert(camera.View * camera.Projection, out var inverse);
                var point = Vector4.Transform(new Vector4(x * 2 - 1, 1 - y * 2, 1, 1), inverse);
                Vector3 direction = Vector3.Normalize(new Vector3(point.X, point.Y, point.Z) / point.W - camera.Position);
                var building = session.Physics.BuildingQuery.Ray(camera.Position, direction);
                if (building.Piece is not null && session.Physics.InteractionBlocked(camera.Position, direction, Vector3.Distance(camera.Position, building.Point) - .015f, ignorePlayer: session.Local.Id, ignoreBuildings: true)) building = (null, building.Point);
                Guid? npc = null; float nearest = 20;
                foreach (var n in session.PresentedNpcs())
                {
                    var bounds = new BuildBounds(n.Position - new Vector3(.32f, 0, .32f), n.Position + new Vector3(.32f, 1.8f, .32f));
                    if (BuildingQuery.RayBounds(camera.Position, direction, bounds, nearest, out float distance) && !session.Physics.InteractionBlocked(camera.Position, direction, Math.Max(0, distance - .05f), ignorePlayer: session.Local.Id, ignoreNpc: n.Id)) { nearest = distance; npc = n.Id; }
                }
                result["pick"] = new { prop = session.Physics.PickProp(session.Local.Id, camera.Position, direction, StargateGame.Shared.Movement.PhysicsGunRange), building = building.Piece?.Id, point = building.Point, npc };
            }
        }
        if (sections.Contains("ui")) result["ui"] = new { revision = TestUiRevision, controls = testUi.Values.ToArray(), pointer = UiMouse, inventoryOpen, buildPaletteOpen, spawnMenuOpen, paused, testingPanel };
        if (sections.Contains("rendering")) result["rendering"] = new { quality = graphicsSettings.Quality, hour = scene?.TimeOfDay, gpuMs = scene?.CommonReady == true ? scene.RenderGpuMilliseconds : 0, cpuMs = scene?.RenderCpuMilliseconds };
        if (sections.Contains("diagnostics")) result["diagnostics"] = new { failure = startupFailure ?? session?.Failure, loading = StartupStatus, pendingMeshes = scene?.PendingMeshes, pendingCanopies = scene?.WorldCanopiesPending, deferred = scene?.DeferredReadiness };
        if (sections.Contains("full")) result["full"] = TestStatus();
        return result;
    }
    void ReleaseTestInput() { testInput?.ReleaseSynthetic(); testRawInputActive = false; testInputRequest = Guid.Empty; testMoveUntil = 0; testJump = false; mouseDelta = Vector2.Zero; }
    void RevokeAiControl()
    {
        testAiRevoked = true; testAiGranted = false; testController = ""; testControllerProcess?.Dispose(); testControllerProcess = null; ReleaseTestInput(); FinishTest("failed", "AI control revoked; applied gameplay effects remain.");
    }
    void PumpTestInput(double now) { if (testControllerProcess?.HasExited == true) RevokeAiControl(); if (testRawInputActive && now >= testInputUntil) { ReleaseTestInput(); if (testPending?.Action == TestAction.Input) FinishTest(); } }
    TestResult StartTestInput(TestRequest r)
    {
        if (testInput is null) throw new InvalidOperationException("Input unavailable.");
        double seconds = r.Number("seconds");
        if (!double.IsFinite(seconds) || seconds is < 0 or > 30) throw new ArgumentException("Input duration must be 0–30 seconds.");
        string[] Strings(string key) => r.Data.TryGetProperty(key, out var value) ? value.Deserialize<string[]>()! : [];
        var keys = Strings("keys").Select(k => Enum.Parse<Key>(k, true)).ToArray();
        var buttons = Strings("buttons").Select(k => Enum.Parse<MouseButton>(k, true)).ToArray();
        if (keys.Any(k => !Enum.IsDefined(k)) || buttons.Any(b => !Enum.IsDefined(b))) throw new ArgumentException("Unknown key/button.");
        float x = (float)r.Number("x", .5), y = (float)r.Number("y", .5);
        if (r.Text("controlId") is { Length: > 0 } id)
        {
            if (r.Text("uiRevision") != TestUiRevision || !testUi.TryGetValue(id, out var control) || !control.Enabled) throw new InvalidOperationException("stale-ui: observe the visible UI again.");
            x = (control.Bounds.X + control.Bounds.W / 2) / UiWidth; y = (control.Bounds.Y + control.Bounds.H / 2) / UiHeight;
            buttons = [MouseButton.Left];
        }
        float dx = (float)r.Number("dx"), dy = (float)r.Number("dy"), scroll = (float)r.Number("scroll");
        if (!float.IsFinite(x + y + dx + dy + scroll) || x is < 0 or > 1 || y is < 0 or > 1 || Math.Abs(dx) > 2000 || Math.Abs(dy) > 2000 || Math.Abs(scroll) > 20 || r.Text("text").Length > 128) throw new ArgumentException("Input outside bounds.");
        testInput.SyntheticPointer = new(x * window.Width, y * window.Height);
        testInput.SetSynthetic(keys, buttons); mouseDelta += new Vector2(dx, dy);
        if (scroll != 0) HandleScroll(scroll);
        if (r.Text("text").Length > 0) HandleText(r.Text("text"));
        testRawInputActive = true; testInputRequest = r.Id; testInputUntil = timer.Elapsed.TotalSeconds + seconds;
        if (r.Flag("hold")) return TestResult.Ok(r, new { heldUntilSeconds = seconds, release = "cancel, replacement input, Escape, or deadline" });
        testPending = r; testDeadline = timer.Elapsed.TotalSeconds + r.TimeoutSeconds;
        return testOperation = new(r.Id, r.Session, r.Generation, "pending", TestJson.Element(new { }), Client: r.Client);
    }
}
