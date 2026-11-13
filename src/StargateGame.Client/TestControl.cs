using System.Numerics;
using System.Text.Json;
using FishGfx.Graphics;
using StargateGame.Shared;
using StargateGame.Building;
using StargateGame.Testing;

namespace StargateGame.Client;

sealed partial class GameApp
{
    readonly TestCommandQueue testQueue = new();
    LocalPipe? testPipe;
    string testSession = "", testRoot = "", testView = "player";
    int testGeneration, testFrames, testChangedFrame;
    ScenarioOptions testOptions = new();
    TestResult? testOperation;
    TestRequest? testPending;
    bool testMovementReview;
    int testMovementProp;
    double testReviewStarted;
    double testDeadline, testMoveUntil, testSampleUntil;
    float testForward, testStrafe;
    bool testJump, testCrouch, testWalk;
    Vector3? testCameraEye, testCameraTarget;
    readonly List<CanopyFrameSample> testSamples = [];
    long testSampleFrame;
    string? testCapture;
    bool testCaptureIncludeUi = true;
    bool testWeatherFixture, testHorizonProps, testHorizonPlayers, testHorizonOcclusion, testShadowFixture, testOrbit, testTraverse;
    string testPropMode = "motion";
    float? testVisualTime;
    string? testRegionalView;
    Guid testRelocationOrigin;
    GatePhase? testPreview;
    double testPreviewTime;
    bool TestAutomated => args.Contains("--test-automated");
    bool TestPlayable => session is not null && scene is not null && firstPlayablePresented && !session.PlanetVisualPending && !session.TransitionPending && !session.TransitLocked && !session.AwaitingArrivalFrame && !session.Local.TransitFrozen;
    bool TestCaptureReady => TestPlayable && testFrames > testChangedFrame + 1 && scene!.InspectionAssetsReady && scene.DeferredCaptureReady && scene.PendingMeshes == 0 && scene.WorldCanopiesPending == 0 && scene.ArrivalRegionReady(camera.Position) && (!scene.WeatherDiagnostics || scene.WeatherChecksComplete) && (!scene.LandscapeDiagnostics || scene.LandscapeChecksComplete);
    void InitializeTestControl()
    {
        string pipe = Arguments.Get(args, "--test-pipe", "");
        if (pipe.Length == 0) return;
        testSession = Arguments.Get(args, "--test-session", "");
        testGeneration = Arguments.Number(args, "--test-generation", 1, 1, int.MaxValue);
        testRoot = Path.GetFullPath(Arguments.Get(args, "--test-root", captureDirectory));
        Directory.CreateDirectory(testRoot);
        testPipe = new LocalPipe(pipe, testQueue.Enqueue);
    }
    object TestProgress() => new { playable = TestPlayable, captureReady = TestCaptureReady, generation = testGeneration, revision = session?.Changes.Revision, operation = testOperation, failure = startupFailure ?? session?.Failure, readiness = TestReadiness() };
    object TestStatus() => new
    {
        syntheticInputActive = testMoveUntil > timer.Elapsed.TotalSeconds, playable = TestPlayable, captureReady = TestCaptureReady, generation = testGeneration,
        failure = startupFailure ?? session?.Failure,
        terrainStatus = session?.TerrainStatus, loading = StartupStatus, treeModels = session?.Terrain?.Chunks.SelectMany(c => StargateGame.WorldGen.TreeScenery.Resolve(c, session.World.Definition.Preset, session.World.Definition.Fixture)).Select(t => t.Model).Distinct().Order().ToArray(), pendingMeshes = scene?.PendingMeshes, pendingCanopies = scene?.WorldCanopiesPending,
        location = session?.World.Definition.LocationId, world = session?.World.Definition.Name, worldDefinition = session?.World.Definition, player = session?.Local,
        gates = session?.Gates.Values.ToArray(), gate = session?.Gate, destinations = session?.Destinations,
        entities = session?.World.Definition.Entities, props = session?.World.Definition.Props,
        bodies = session?.Physics.Bodies(), carries = session?.Physics.Carries,
        physics = new { tick = session?.Physics.Tick, epoch = session?.Physics.Epoch, rebuiltBatches = session?.Physics.RebuiltBatches, transitionPending = session?.TransitionPending },
        navmesh = NavigationOverlayState(), npcs = session?.Npcs, npcReply = session?.LastNpcReply, npcNavigation = session?.NpcDiagnostics, npcCollision = session?.Physics.NpcMotions, npcRendered = session?.PresentedNpcs(),
        returnAddress = session?.ReturnAddress, inventory = session?.Inventory, container = session?.OpenWeaponContainer, changes = session?.Changes, pieces = session?.Changes.Pieces, doors = session?.Physics.DoorStates, revision = session?.Changes.Revision,
        operation = testOperation, camera = new { eye = camera.Position, target = camera.Position + camera.WorldForwardNormal, view = testView },
        pointLights = new { count = scene?.PointLightCount, shadowFaceUpdates = scene?.PointShadowUpdates, targetBytes = scene?.PointLightTargetBytes, shadowMs = scene?.CommonReady == true ? scene.PointShadowGpuMilliseconds : 0, lightingMs = scene?.CommonReady == true ? scene.PointLightGpuMilliseconds : 0 },
        performance = new { cpuMs = scene?.RenderCpuMilliseconds, gpuMs = scene?.CommonReady == true ? scene.RenderGpuMilliseconds : 0, modelGpuBytes = scene?.ModelGpuBytes, gbufferMs = scene?.CommonReady == true ? scene.GbufferGpuMilliseconds : 0, aoMs = scene?.CommonReady == true ? scene.AoGpuMilliseconds : 0, deferredMs = scene?.CommonReady == true ? scene.DeferredGpuMilliseconds : 0, weaponMs = scene?.CommonReady == true ? scene.WeaponGpuMilliseconds : 0, compositionMs = scene?.CommonReady == true ? scene.CompositionGpuMilliseconds : 0 },
        build = new { physicsHash = Physics.PhysicsWorld.PhysicsHash, contentHash = Physics.PhysicsWorld.ContentHash },
        resources = new { targetsBytes = scene?.CommonReady == true ? scene.TargetBytes : 0, gbufferBytes = scene?.GbufferTargetBytes, weaponBytes = scene?.WeaponDeferredTargetBytes, aoBytes = scene?.AoTargetBytes, lightingBytes = scene?.DeferredLightingTargetBytes, environmentBytes = (scene?.GrassWorldBytes ?? 0) + (scene?.WeatherBytes ?? 0), sharedGrassBytes = scene?.GrassVolumeBytes ?? 0, volumeBytes = scene?.VolumeBytes ?? 0, horizonBytes = scene?.HorizonTargetBytes ?? 0 },
        audioCues = audio?.TestCueCounts, rifle = new { ammo = session?.EquippedRifle, reloading = session?.Reloading, character = session?.LocalCharacter, effects = scene?.RifleDiagnostics, reloadAudio = audio?.TestReloadEvents, reloadMarkers = RifleReloadAudio.Markers },
        rendering = scene is null ? null : new { viewmodel=scene.ViewmodelDiagnostics, dhdFocus=new { active=LookingAtDhd, entity=dhdFocus?.Entity.Id, key=dhdHover }, grass=scene.GrassDiagnostics, canopy=scene.CanopyDiagnostics, hour = scene.TimeOfDay, quality = graphicsSettings.Quality, ao = scene.AoSettings, exposure = scene.ToneMapping, visualTime = testVisualTime, deferredReady = scene.DeferredCaptureReady, weather = scene.WeatherSettings, roof = scene.WeatherFixtureRoof, weatherCheck = scene.WeatherDiagnostics, camera = testView, gatePreview = testPreview }, options = testOptions
    };
    TestResult ExecuteTest(TestRequest r)
    {
        if (r.Session != testSession) throw new InvalidOperationException("Wrong test session.");
        if (r.Action == TestAction.Connect)
        {
            if (r.Generation < testGeneration) throw new InvalidOperationException("Stale scenario generation.");
            if (r.Text("host") != "127.0.0.1") throw new ArgumentException("Harness connections must use loopback.");
            CancelJoin(); Disconnect("Preparing local scenario.");
            testRegionalView = r.Text("regionalView");
            testGeneration = r.Generation; testOptions = ScenarioCatalog.Resolve(r.Value<ScenarioOptions>("options"));
            testWeatherFixture = testHorizonProps = testHorizonPlayers = testHorizonOcclusion = testShadowFixture = testOrbit = testTraverse = false; testVisualTime = null;
            scene?.SetTestMeadowMaterial(null); if (scene is not null) ConfigureScene();
            testView = "player"; testPreview = null; testCameraEye = testCameraTarget = null; testPending = null; testOperation = null;
            ReleaseTestInput(); testMoveUntil = 0; testFrames = 0; testChangedFrame = 0;
            fields[0] = "127.0.0.1"; fields[1] = r.Int("port").ToString();
            if (scene is not null) ApplyTestSettings(r with { Data = TestJson.Element(new { hour = testOptions.Hour, weather = testOptions.Weather, quality = testOptions.Quality }) });
            Connect(); return TestResult.Ok(r);
        }
        if (r.Generation != testGeneration) throw new InvalidOperationException("Stale scenario generation.");
        if (r.Action == TestAction.Observe) return TestResult.Ok(r, ObserveTest(r));
        if (r.Action == TestAction.ReleaseControl) { if (testController == r.Controller && testController.Length > 0) RevokeAiControl(); return TestResult.Ok(r); }
        if (r.Action == TestAction.Cancel)
        {
            bool pending = testPending?.Id == r.Value<Guid>("id");
            if (pending) FinishTest("failed", "Cancelled; already-applied gameplay effects remain.");
            if (testInputRequest == r.Value<Guid>("id") || pending) ReleaseTestInput();
            return TestResult.Ok(r);
        }
        if (r.Controller.Length > 0 && r.Action is not (TestAction.Status or TestAction.Capture))
        {
            if (!AiInputOwned) throw new InvalidOperationException("control-not-granted");
            if (testController.Length > 0 && testController != r.Controller) throw new InvalidOperationException("Another AI controller owns this client.");
            if (testController != r.Controller)
            {
                testControllerProcess?.Dispose(); testControllerProcess = null;
                if (int.TryParse(r.Controller.Split(':')[0], out int ownerPid))
                {
                    try { testControllerProcess = System.Diagnostics.Process.GetProcessById(ownerPid); }
                    catch (ArgumentException) { throw new InvalidOperationException("AI controller exited."); }
                }
                testController = r.Controller;
            }
        }
        if (r.Action == TestAction.Status) return TestResult.Ok(r, r.Flag("compact") ? TestProgress() : TestStatus());
        if (r.Action == TestAction.Stop) { window.IsCloseRequested = true; return TestResult.Ok(r); }
        if (r.Action == TestAction.Disconnect) { CancelJoin(); Disconnect("Local test disconnected."); testMoveUntil = 0; FinishTest("failed", "Disconnected."); return TestResult.Ok(r); }
        if (session is null || scene is null) throw new InvalidOperationException("No playable local session.");
        if (testPending is not null && !(r.Action == TestAction.Move && r.Number("seconds") == 0)) throw new InvalidOperationException("Another action is pending.");
        testChangedFrame = testFrames;
        switch (r.Action)
        {
            case TestAction.Input: return StartTestInput(r);
            case TestAction.Aim:
                Vector3 target;
                if (r.Data.TryGetProperty("target", out _)) target = r.Value<Vector3>("target");
                else if (session.World.Definition.Entities.FirstOrDefault(e => e.Id == r.Text("entity")) is { } entity) target = entity.Position;
                else if (session.PresentedNpcs().FirstOrDefault(n => n.Id.ToString() == r.Text("entity")) is var npc && npc.Id != Guid.Empty) target = npc.Position + Vector3.UnitY;
                else if (session.Changes.Pieces.FirstOrDefault(p => p.Id.ToString() == r.Text("entity")) is { } piece)
                {
                    var bounds = BuildingGeometry.Bounds(BuildingGeometry.Mesh(session.Changes, piece)); target = (bounds.Min + bounds.Max) / 2;
                }
                else if (session.World.Definition.Props.FirstOrDefault(p => p.PersistentId.ToString() == r.Text("entity")) is { } prop) target = session.Physics.Bodies().Single(b => b.Id == prop.Id).Position;
                else throw new ArgumentException("unsupported-target: entity is absent or cannot be aimed at.");
                var direction = target - StargateGame.Shared.Movement.EyePosition(session.Local);
                if (!Finite(target) || direction.LengthSquared() < .0001f) throw new ArgumentException("Invalid aim target.");
                direction = Vector3.Normalize(direction); yaw = MathF.Atan2(direction.X, -direction.Z); pitch = MathF.Asin(direction.Y);
                testView = "player"; testCameraEye = testCameraTarget = null; return TestResult.Ok(r);
            case TestAction.Camera:
                string view = r.Text("bookmark", "player");
                Vector3? eyePosition = r.Data.TryGetProperty("eye", out _) ? r.Value<Vector3>("eye") : null;
                Vector3? targetPosition = eyePosition is not null ? r.Value<Vector3>("target") : null;
                if (eyePosition is { } ep && targetPosition is { } tp && (!Finite(ep) || !Finite(tp) || Vector3.DistanceSquared(ep, tp) < .0001f)) throw new ArgumentException("Camera requires distinct finite eye/target positions.");
                if (eyePosition is { } ce && targetPosition is { } ct && Vector3.Cross(ct - ce, Vector3.UnitY).LengthSquared() < .000001f * (ct - ce).LengthSquared())
                    throw new ArgumentException("Camera direction cannot be parallel to its up axis; offset the target horizontally.");
                if (eyePosition is null && view != "player") VisualChecks.Camera(camera, session, TestBookmark(view), 0, false, false, scene.TimeOfDay);
                testView = view; testCameraEye = eyePosition; testCameraTarget = targetPosition;
                return TestResult.Ok(r);
            case TestAction.Settings: ApplyTestSettings(r); return TestResult.Ok(r);
            case TestAction.GatePreview:
                if (r.Text("phase", "off") != "off" && (!Enum.TryParse<GatePhase>(r.Text("phase"), true, out var phase) || !Enum.IsDefined(phase))) throw new ArgumentException("Unknown preview phase.");
                testPreview = r.Text("phase", "off") == "off" ? null : Enum.Parse<GatePhase>(r.Text("phase"), true);
                if (!double.IsFinite(r.Number("seconds")) || r.Number("seconds") < 0) throw new ArgumentException("Preview time must be finite and nonnegative.");
                testPreviewTime = r.Number("seconds"); return TestResult.Ok(r, new { renderOnly = true });
            case TestAction.AudioRecord:
                if (audio is null) throw new InvalidOperationException("Audio unavailable.");
                if (r.Flag("stop")) audio.StopRecording(); else { audio.TestCueCounts.Clear(); audio.TestReloadEvents.Clear(); audio.Record(TestOutput(r.Text("name", "audio.wav"))); } return TestResult.Ok(r);
            case TestAction.Move:
                double seconds = r.Number("seconds");
                if (seconds is < 0 or > 30 || !double.IsFinite(seconds)) throw new ArgumentException("Movement duration must be 0–30 seconds.");
                testForward = (float)r.Number("forward"); testStrafe = (float)r.Number("strafe");
                if (!float.IsFinite(testForward + testStrafe) || Math.Abs(testForward) > 1 || Math.Abs(testStrafe) > 1) throw new ArgumentException("Input must be between -1 and 1.");
                if (!double.IsFinite(r.Number("yaw", yaw)) || !double.IsFinite(r.Number("pitch", pitch)) || Math.Abs(r.Number("pitch", pitch)) > 1.55) throw new ArgumentException("Invalid look angles.");
                yaw = (float)r.Number("yaw", yaw); pitch = (float)r.Number("pitch", pitch);
                testJump = r.Flag("jump"); testCrouch = r.Flag("crouch"); testWalk = r.Flag("walk");
                testMoveUntil = timer.Elapsed.TotalSeconds + seconds;
                if (seconds == 0) { FinishTest("failed", "Input cancelled."); return TestResult.Ok(r); }
                break;
            case TestAction.UiReview:
                testUiReview = true; uiCaptureStage = 0; uiCaptureApplied = -1; break;
            case TestAction.MovementReview:
                testMovementProp = session.World.Definition.Props.First(p => p.Kind == PropKind.WoodenCrate).Id;
                testMovementReview = true; testReviewStarted = timer.Elapsed.TotalSeconds; movementTrace.Clear(); motionCaptures = 0; break;
            case TestAction.Relocate:
                if (!session.Developer) throw new InvalidOperationException("Developer relocation unavailable.");
                testRelocationOrigin = session.World.Definition.LocationId;
                session.InspectDestination(r.Value<int[]>("address")); break;
            case TestAction.Dial:
                var dhd = session.World.Definition.Entities.First(e => e.Id == r.Text("dhd", "dhd-a") || e.Model == "dhd" && r.Text("dhd").Length == 0);
                session.SelectDhd(dhd.Id);
                int key = r.Int("key");
                var eye = Movement.EyePosition(session.Local);
                bool sent = false;
                foreach (var face in DhdInteraction.Surfaces.Where(f => f.Key == key))
                {
                    var aim = Vector3.Normalize(Vector3.Transform((face.A + face.B + face.C) / 3 * dhd.Scale, dhd.Rotation) + dhd.Position - eye);
                    if (DhdInteraction.Pick(dhd, eye, aim)?.Key != key) continue;
                    session.Dial(r.Text("action", "press"), key, aim); sent = true; break;
                }
                if (!sent) throw new InvalidOperationException("DHD key is not visible from player position.");
                break;
            case TestAction.Carry:
                session.Carry(Enum.Parse<CarryAction>(r.Text("action"), true), r.Data.TryGetProperty("item", out _) ? r.Value<Guid>("item") : Guid.Empty);
                break;
            case TestAction.Npc:
                npcDebug = r.Flag("debug", npcDebug);
                session.Npc(r.Value<NpcCommand>("command") with { Request = r.Id }); break;
            case TestAction.Build:
                var buildCommand = r.Value<BuildCommand>("command") with { RequestId = r.Id, Revision = session.Changes.Revision };
                if (buildCommand.Action == "door" && buildCommand.HitPoint is null)
                {
                    var aimed = PickPiece();
                    if (aimed.Piece?.Id != buildCommand.Target) throw new InvalidOperationException("Aim the rendered camera at the requested door.");
                    buildCommand = buildCommand with { HitPoint = aimed.Point };
                }
                session.Build(buildCommand); break;
            case TestAction.Weapon:
                if (session.Inventory is null || session.WeaponPending || session.TransitLocked) throw new InvalidOperationException("Inventory is not ready for an action.");
                session.Weapon(r.Text("action"), r.Data.TryGetProperty("item", out _) ? r.Value<Guid>("item") : Guid.Empty,
                    r.Data.TryGetProperty("container", out _) ? r.Value<Guid>("container") : Guid.Empty); break;
            case TestAction.Capture:
                testCapture = TestOutput(r.Text("name", "capture-" + r.Id.ToString("N") + ".png"));
                if (!r.Flag("retain", true)) { var transient = Path.Combine(testRoot, "transient"); Directory.CreateDirectory(transient); testCapture = Path.Combine(transient, Path.GetFileName(testCapture)); }
                testCaptureIncludeUi = r.Flag("includeUi", true);
                break;
            case TestAction.Sample:
                double duration = r.Number("seconds", 3);
                if (!double.IsFinite(duration) || duration is < .1 or > 60) throw new ArgumentException("Sample duration must be 0.1–60 seconds.");
                testSamples.Clear(); testSampleFrame=scene?.PerformanceFrame??0; testSampleUntil = timer.Elapsed.TotalSeconds + duration; break;
            default: throw new ArgumentException("Unsupported client action: " + r.Action);
        }
        testPending = r; testDeadline = timer.Elapsed.TotalSeconds + Math.Clamp(r.TimeoutSeconds, 1, 300);
        return testOperation = new(r.Id, r.Session, r.Generation, "pending", TestJson.Element(new { }), Client: r.Client);
    }
    object PerformanceSummary()
    {
        object Stats(Func<CanopyFrameSample,double> select) {var v=testSamples.Select(select).Order().ToArray();return new {median=v.Length==0?0:v[v.Length/2],p95=v.Length==0?0:P95(v.ToList()),max=v.Length==0?0:v[^1]};}
        return new {frames=testSamples.Count, gpuP95Ms=testSamples.Count==0?0:P95(testSamples.Select(s=>s.GpuMs).ToList()),
            gpuMaxMs=testSamples.Count==0?0:testSamples.Max(s=>s.GpuMs), gpuMs=Stats(s=>s.GpuMs),canopyMs=Stats(s=>s.CanopyMs),depthCopyMs=Stats(s=>s.DepthCopyMs),
            canopyWithCopyMs=Stats(s=>s.CanopyMs+s.DepthCopyMs),cpuMs=Stats(s=>s.CpuMs),frameMs=Stats(s=>s.FrameMs),draws=Stats(s=>s.Draws),
            lods=Enumerable.Range(0,4).Select(i=>Stats(s=>s.Lods[i])).ToArray(),settings=scene?.CanopyDiagnostics,
            grassMs=Stats(s=>s.GrassMeshMs+s.GrassVolumeMs),grassMeshMs=Stats(s=>s.GrassMeshMs),grassVolumeMs=Stats(s=>s.GrassVolumeMs),foliageMs=Stats(s=>s.FoliageMs),grassDepthCopyMs=Stats(s=>s.GrassDepthMs),
            grassWithCopyMs=Stats(s=>s.GrassMeshMs+s.GrassVolumeMs+s.GrassDepthMs),grassPatches=Stats(s=>s.GrassPatches),grassTriangles=Stats(s=>s.GrassTriangles),
            grassLods=Enumerable.Range(0,4).Select(i=>Stats(s=>s.GrassLods[i])).ToArray(),grassSettings=scene?.GrassDiagnostics};
    }
    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    string TestOutput(string name)
    {
        if (Path.GetFileName(name) != name || name is "" or "." or "..") throw new ArgumentException("Capture name must be a filename.");
        return Path.Combine(testRoot, name);
    }
    void FinishTest(string outcome = "passed", string? error = null, object? data = null, string? errorCode = null)
    {
        if (testPending is not { } r) return;
        if (outcome != "passed" && r.Action == TestAction.Relocate) session?.CancelPlanetLoad();
        testOperation = new(r.Id, r.Session, r.Generation, outcome, TestJson.Element(data ?? new { }), error, ErrorCode: errorCode, Client: r.Client);
        if (r.Action == TestAction.Input || outcome != "passed") ReleaseTestInput();
        testPending = null; testCapture = null; testMovementReview = false; testUiReview = false; testMoveUntil = 0; testJump = false;
    }
    void PumpTests(double now)
    {
        testQueue.Drain(ExecuteTest);
        if (testPending is not { } r) return;
        if (now > testDeadline) { FinishTest("failed", "Action timed out: " + r.Action, errorCode: "timed-out"); return; }
        if (session is null) { FinishTest("failed", "Session disconnected."); return; }
        switch (r.Action)
        {
            case TestAction.MovementReview when now - testReviewStarted >= 13:
                File.WriteAllLines(TestOutput("movement.csv"), new[] { "seconds,tick,raw_x,raw_y,raw_z,draw_x,draw_y,draw_z,box_x,box_y,box_z,draw_box_x,draw_box_y,draw_box_z" }.Concat(movementTrace.Select(p => FormattableString.Invariant($"{p.Time:F6},{p.Tick},{p.Raw.X:F6},{p.Raw.Y:F6},{p.Raw.Z:F6},{p.Draw.X:F6},{p.Draw.Y:F6},{p.Draw.Z:F6},{p.Box.X:F6},{p.Box.Y:F6},{p.Box.Z:F6},{p.DrawBox.X:F6},{p.DrawBox.Y:F6},{p.DrawBox.Z:F6}"))));
                FinishTest(data: new { samples = movementTrace.Count }); break;
            case TestAction.Relocate when session.World.Definition.LocationId != testRelocationOrigin && TestPlayable: FinishTest(); break;
            case TestAction.Move when now >= testMoveUntil: FinishTest(); break;
            case TestAction.Dial when testFrames > testChangedFrame + 1 && !session.DialPending:
                FinishTest(session.LastDhdResult?.Accepted == true ? "passed" : "failed", session.LastDhdResult?.Reason, session.LastDhdResult); break;
            case TestAction.Npc when !session.NpcPending:
                FinishTest(session.LastNpcReply?.Accepted == true ? "passed" : "failed", session.LastNpcReply?.Reason, session.LastNpcReply); break;
            case TestAction.Build when !session.BuildPending:
                FinishTest(session.LastBuildResult?.Success == true ? "passed" : "failed", session.BuildStatus, session.LastBuildResult); break;
            case TestAction.Weapon when !session.WeaponPending: FinishTest(session.LastWeaponResult?.Success == true ? "passed" : "failed", session.WeaponStatus, data: new { session.Inventory, session.WeaponStatus }); break;
            case TestAction.Carry when testFrames > testChangedFrame + 10:
                bool wanted = r.Text("action").Equals("grab", StringComparison.OrdinalIgnoreCase);
                if (session.Physics.IsCarrying(session.Local.Id) == wanted) FinishTest(); break;
            case TestAction.Sample:
                if (scene is not null)
                    foreach(var sample in scene.CompletedPerformanceFrames.Where(s=>s.Frame>testSampleFrame)) {testSamples.Add(sample);testSampleFrame=sample.Frame;}
                if (now >= testSampleUntil) FinishTest(data: PerformanceSummary()); break;
        }
    }
    void ApplyTestSettings(TestRequest r)
    {
        if (scene is null) return;
        foreach (var setting in r.Data.EnumerateObject())
            if (!ScenarioCatalog.RenderSettings.Contains(setting.Name)) throw new ArgumentException("Unknown rendering setting: " + setting.Name);
        if (r.Data.TryGetProperty("exposure", out _)) ToneMappingSettings.ParseMode(r.Text("exposure"));
        if (r.Data.TryGetProperty("exposureEv", out _) && (!double.IsFinite(r.Number("exposureEv")) || r.Number("exposureEv") is < -2 or > 2)) throw new ArgumentException("Exposure EV must be finite and between -2 and 2.");
        if (r.Data.TryGetProperty("ao", out _)) AmbientOcclusionSettings.Parse(r.Text("ao"));
        if (r.Data.TryGetProperty("aoView", out _)) AmbientOcclusionSettings.InspectionId(r.Text("aoView"));
        if (r.Data.TryGetProperty("quality", out _) && !new[] { "performance", "low", "high" }.Contains(r.Text("quality"))) throw new ArgumentException("Invalid quality.");
        if (r.Data.TryGetProperty("weather", out _)) WeatherSettings.Parse(r.Text("weather"));
        if (r.Data.TryGetProperty("grassVolumeDensity", out _) && (!double.IsFinite(r.Number("grassVolumeDensity")) || r.Number("grassVolumeDensity") is < .1 or > 1))
            throw new ArgumentException("Grass volume density must be finite and between 0.1 and 1.");
        if (r.Data.TryGetProperty("grassDensity", out _) && r.Int("grassDensity") is < 0 or > 64) throw new ArgumentException("Grass density must be 0�64.");
        if (r.Data.TryGetProperty("visualTime", out var vt) && vt.ValueKind != JsonValueKind.Null && (!double.IsFinite(r.Number("visualTime")) || r.Number("visualTime") < 0)) throw new ArgumentException("Invalid visual time.");
        if (r.Data.TryGetProperty("propMode", out _) && r.Text("propMode") is not ("all" or "motion" or "stationary")) throw new ArgumentException("Unknown prop preview mode.");
        if (testOptions.Scenario == "sandbox" && testOptions.Preset != "graphics-lab") scene.SetTestingInspection(true); else scene.SetTestingInspection(false);
        if (r.Data.TryGetProperty("hour", out _) && (!double.IsFinite(r.Number("hour")) || r.Number("hour") is < 0 or > 24)) throw new ArgumentException("Hour must be 0–24.");
        if(r.Data.TryGetProperty("renderWidth",out _) || r.Data.TryGetProperty("renderHeight",out _)) {
            int w=r.Int("renderWidth"),h=r.Int("renderHeight");
            if(w<640||w>3840||h<480||h>2160)throw new ArgumentException("Render dimensions must be supplied together, within 640x480–3840x2160.");
            window.SetClientSize(w,h); scene.InspectionResolution=new(w,h);
        }
        if(r.Data.TryGetProperty("grassDepthCulling",out _))scene.GrassDepthCulling=r.Flag("grassDepthCulling");
        if(r.Data.TryGetProperty("grassShaderReuse",out _))scene.GrassShaderReuse=r.Flag("grassShaderReuse");
        if(r.Data.TryGetProperty("grassScreenLod",out _))scene.GrassScreenLod=r.Flag("grassScreenLod");
        if(r.Data.TryGetProperty("grassVolumeDensity",out _))scene.GrassVolumeDensity=(float)r.Number("grassVolumeDensity");
        if(r.Data.TryGetProperty("canopyDepthCulling",out _))scene.CanopyDepthCulling=r.Flag("canopyDepthCulling");
        if(r.Data.TryGetProperty("canopyLightLod",out _))scene.CanopyLightLod=r.Flag("canopyLightLod");
        if(r.Data.TryGetProperty("canopyHierarchicalSkip",out _))scene.CanopyHierarchicalSkip=r.Flag("canopyHierarchicalSkip");
        if (r.Data.TryGetProperty("navmesh", out _)) session?.SetNavigationOverlay(r.Flag("navmesh"));
        if (r.Data.TryGetProperty("hour", out _)) scene.TimeOfDay = (float)r.Number("hour");
        if (r.Data.TryGetProperty("quality", out _)) { graphicsSettings = GraphicsSettings.Parse(r.Text("quality")); scene.SetGraphics(graphicsSettings); }
        if (r.Data.TryGetProperty("weather", out _)) scene.WeatherSettings = scene.WeatherSettings with { Mode = WeatherSettings.Parse(r.Text("weather")) };
        if (r.Data.TryGetProperty("ao", out _)) scene.AoSettings = scene.AoSettings with { Quality = AmbientOcclusionSettings.Parse(r.Text("ao")) };
        if (r.Data.TryGetProperty("aoView", out _)) scene.AoSettings = scene.AoSettings with { Inspection = r.Text("aoView") };
        if (r.Data.TryGetProperty("exposure", out _)) scene.ToneMapping = scene.ToneMapping with { Mode = ToneMappingSettings.ParseMode(r.Text("exposure")) };
        if (r.Data.TryGetProperty("exposureEv", out _)) scene.ToneMapping = scene.ToneMapping with { Compensation = (float)r.Number("exposureEv") };
        if (r.Data.TryGetProperty("shadows", out _)) scene.ShadowsEnabled = r.Flag("shadows");
        if (r.Data.TryGetProperty("clouds", out _)) scene.CloudsEnabled = r.Flag("clouds");
        if (r.Data.TryGetProperty("volumetrics", out _)) scene.VolumetricsEnabled = r.Flag("volumetrics");
        if (r.Data.TryGetProperty("reflections", out _)) scene.WaterReflections = r.Flag("reflections");
        if (r.Data.TryGetProperty("groundMist", out _)) scene.WeatherSettings = scene.WeatherSettings with { GroundMistEnabled = r.Flag("groundMist") };
        if (r.Data.TryGetProperty("grass", out _)) scene.GroundCoverSettings = scene.GroundCoverSettings with { Enabled = r.Flag("grass") };
        if (r.Data.TryGetProperty("grassDensity", out _)) scene.GroundCoverSettings = scene.GroundCoverSettings with { DensityOverride = r.Int("grassDensity") };
        if (r.Data.TryGetProperty("parallax", out _)) scene.TerrainParallaxEnabled = r.Flag("parallax");
        if (r.Data.TryGetProperty("gateLighting", out _)) scene.GateLightingEnabled = r.Flag("gateLighting");
        if (r.Data.TryGetProperty("waves", out _)) scene.HorizonSettings = scene.HorizonSettings with { CollisionWaves = r.Flag("waves") };
        if (r.Data.TryGetProperty("visualTime", out _)) testVisualTime = r.Data.GetProperty("visualTime").ValueKind == JsonValueKind.Null ? null : (float)r.Number("visualTime");
        if (r.Data.TryGetProperty("orbit", out _)) testOrbit = r.Flag("orbit");
        if (r.Data.TryGetProperty("traverse", out _)) testTraverse = r.Flag("traverse");
        if (r.Data.TryGetProperty("meadowMaterial", out _)) scene.SetTestMeadowMaterial(r.Text("meadowMaterial") is "default" or "" ? null : r.Text("meadowMaterial"));
        if (r.Data.TryGetProperty("horizon", out _)) scene.HorizonSettings = scene.HorizonSettings with { Enabled = r.Flag("horizon") };
        if (r.Data.TryGetProperty("horizonProps", out _)) testHorizonProps = r.Flag("horizonProps");
        if (r.Data.TryGetProperty("horizonPlayers", out _)) testHorizonPlayers = r.Flag("horizonPlayers");
        if (r.Data.TryGetProperty("horizonOcclusion", out _)) testHorizonOcclusion = r.Flag("horizonOcclusion");
        if (r.Data.TryGetProperty("shadowFixture", out _)) testShadowFixture = r.Flag("shadowFixture");
        if (r.Data.TryGetProperty("propMode", out _)) { testPropMode = r.Text("propMode"); if (testPropMode is not ("all" or "motion" or "stationary")) throw new ArgumentException("Unknown prop preview mode."); }
        if (r.Data.TryGetProperty("waveCheck", out _)) scene.HorizonWaveDiagnostics = r.Flag("waveCheck");
        if (r.Data.TryGetProperty("landscapeCheck", out _)) scene.LandscapeDiagnostics = r.Flag("landscapeCheck");
        if (r.Data.TryGetProperty("stress", out _)) scene.HorizonStress = r.Flag("stress");
        if (r.Data.TryGetProperty("weatherFixture", out _)) testWeatherFixture = r.Flag("weatherFixture");
        if (r.Data.TryGetProperty("roof", out _)) scene.WeatherFixtureRoof = r.Flag("roof");
        if (r.Data.TryGetProperty("weatherCheck", out _)) scene.WeatherDiagnostics = r.Flag("weatherCheck");

    }
    void TestCamera()
    {
        if (testPipe is null || session is null || scene is null) return;
        scene.InspectionCamera = testView != "player" || testCameraEye is not null;
        if (testCameraEye is { } eye && testCameraTarget is { } target) { camera.Position = eye; camera.LookAt(target); }
        else if (testView != "player") VisualChecks.Camera(camera, session, TestBookmark(testView), testVisualTime ?? timer.Elapsed.TotalSeconds - connectedAt, testOrbit, testTraverse, scene.TimeOfDay);
        session.StreamingFocus = testView != "player" || testCameraEye is not null ? camera.Position : null;

    }
    void ApplyTestPreview()
    {
        if (testPreview is { } phase && session?.Gate is { } gate && scene is not null)
        {
            scene.AuthoritativeGate = gate with { Phase = phase, PhaseTick = 0 };
            scene.EndpointGates = session.Gates.ToDictionary(p => p.Key, p => p.Value with { Phase = phase, PhaseTick = 0 });
            scene.GateVisualTick = testPreviewTime * 60;
        }
    }
    string TestBookmark(string name) => name switch
    {
        "grass" when testOptions.Scenario=="sandbox" => "region:-26,.85,23,-26,.25,5",
        "forest" when testOptions.Scenario=="sandbox" => "region:0,3,24,0,7,-24",
        "movement" => "region:-22,3,18,-22,0,6", "equipment" => "region:22,2,15,22,1,10",
        "interior" => "region:25,3,29,25,1,23", "technology" => "region:37,3,18,37,1,10",
        "water" => "water-shallow", "region" => testRegionalView is { Length: > 0 } view ? view : throw new InvalidOperationException("This scenario has no regional bookmark."), _ => name
    };
    void CaptureTest(RenderPass pass, bool beforeUi = false)
    {
        if (beforeUi == testCaptureIncludeUi || testCapture is not { } path || !(testPending?.Flag("immediate") == true ? scene is not null : TestCaptureReady)) return;
        // Check transient effects on the frame being captured, avoiding a pipe
        // round trip between a readiness observation and the screenshot.
        if (testPending is { } request && request.Data.TryGetProperty("when", out var condition) &&
            !Conditions.Matches(TestJson.Element(TestStatus()), condition, out _)) return;
        Capture(pass, path, true); TestJson.Write(Path.ChangeExtension(path, ".json"), ObserveTest(new() { Data = TestJson.Element(new { sections = new[] { "rendering", "diagnostics" } }) }));
        FinishTest(data: new { path, metadata = Path.ChangeExtension(path, ".json") });
    }
}
