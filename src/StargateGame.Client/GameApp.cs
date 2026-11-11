using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using FishGfx;
using FishGfx.Formats;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;
using StargateGame.Physics;

namespace StargateGame.Client;

sealed partial class GameApp
{
    readonly List<double> benchmarkCanopy = [], benchmarkCanopyCpu = [];
    readonly List<double> benchmarkWaterCpu = [], benchmarkWaterAllocations = [];
    static double P95(List<double> samples) => samples.Count == 0 ? 0 : samples.Order().ElementAt((int)(samples.Count * .95));
    readonly List<double> benchmarkAtmosphere = [], benchmarkCloud = [], benchmarkFog = [], benchmarkWater = [], benchmarkMistMap = [], benchmarkMistCpu = [], benchmarkSurfaceCpu = [], benchmarkFoliageCpu = [], benchmarkDustCpu = [], benchmarkTerrain = [], benchmarkDustGpu = [];
    readonly string[] args;
    readonly string[] fields;
    readonly Camera camera = new(), uiCamera = new();
    RenderWindow window = null!;
    TrueTypeFont font = null!;
    ClientSession? session;
    SceneRenderer? scene;
    Task<GameConnection>? connecting;
    CancellationTokenSource? connectStop;
    string status = "Enter a server address to begin.";
    int field, smokeStage;
    bool inspectionReadySent;
    bool paused, jumpQueued, selectAll, debugPhysics, debugCollision, planetPanel;
    string planetSeed = "7319";
    int planetPreset;
    float yaw, pitch;
    Vector2 mouseDelta;
    readonly Stopwatch timer = Stopwatch.StartNew();
    double audioRecordStarted;
    double? frozenVisualTime;
    double connectedAt, nextSmokeSwitch = 7;
    int smokeSwitches, smokeReconnects, smokeQualityChanges, smokeQualityCaptures, motionCaptures;
    double? walkCaptureStart;
    readonly List<double> frameMilliseconds = [];
    readonly List<(double Time, long Tick, Vector3 Raw, Vector3 Draw, Vector3 Box, Vector3 DrawBox)> movementTrace = [];
    readonly int smokeSeconds;
    readonly bool benchmark, vsync;
    readonly int benchmarkWarmup;
    readonly bool eventHorizon;
    GraphicsSettings graphicsSettings;
    readonly List<double> benchmarkAo = [], benchmarkFrames = [], benchmarkGpu = [], benchmarkCpu = [], benchmarkSpray = [], benchmarkWave = [], benchmarkGrass = [], benchmarkWeather = [], benchmarkGrassCpu = [], benchmarkWeatherCpu = [];
    readonly string captureDirectory;
    RenderTarget? benchmarkTarget;
    bool Smoke => smokeSeconds > 0;
    public GameApp(string[] args)
    {
        this.args = args;
        fields = [Arguments.Get(args, "--host", "127.0.0.1"), Arguments.Get(args, "--port", "7777"), Arguments.Get(args, "--name", "Explorer")];
        int benchmarkSeconds = Arguments.Number(args, "--benchmark-seconds", 0, 0, 300);
        benchmark = benchmarkSeconds > 0;
        benchmarkWarmup = Arguments.Number(args, "--benchmark-warmup", 10, 0, 120);
        smokeSeconds = benchmark ? benchmarkSeconds + benchmarkWarmup : Arguments.Number(args, "--smoke-seconds", 0, 0, 300);
        graphicsSettings = GraphicsSettings.Parse(Arguments.Get(args, "--graphics", "performance"));
        vsync = Arguments.Get(args, "--vsync", "on").ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgumentException("--vsync must be on or off.")
        };
        eventHorizon = Arguments.Get(args, "--event-horizon", "on").ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new ArgumentException("--event-horizon must be on or off.")
        };
        captureDirectory = Arguments.Get(args, "--capture-dir", Path.Combine(StargateGame.Shared.UserData.Root, "captures"));
    }
    public void Run()
    {
        if (UiPreview)
        {
            RunUiPreview();
            return;
        }
        using var ownedWindow = new RenderWindow(new RenderWindowOptions
        {
            Width = Arguments.Number(args, "--width", 1920, 960, 7680),
            Height = Arguments.Number(args, "--height", 1080, 600, 4320),
            Title = "StargateGame / " + fields[2],
            VSync = vsync,
            FocusOnShow = !args.Contains("--no-focus"),
            Resizable = true,
            PreferredVersion = new OpenGlVersion(4, 6),
            MinimumVersion = new OpenGlVersion(4, 0)
        });
        window = ownedWindow;
        StartupTrace.Mark("window-context-ready");
        window.Scrolled += (_, e) =>
        {
            if (planetPanel && generatorTab) { generatorScroll = Math.Clamp(generatorScroll + (e.Offset.Y > 0 ? -1 : 1), 0, 12 - GeneratorRows); return; }
            if (bookOpen)
            {
                bookScroll = Math.Max(0, bookScroll + (e.Offset.Y > 0 ? -1 : 1));
                return;
            }
            if (buildPaletteOpen)
            {
                palettePage = Math.Clamp(palettePage + (e.Offset.Y > 0 ? -1 : 1), 0, (BuildPaletteItemCount - 1) / Layout.PageSize);
                return;
            }
            if (debugPhysics && !buildMode && !paused)
            {
                debugScroll = Math.Max(0, debugScroll + (e.Offset.Y > 0 ? -3 : 3));
                nextDebugText = 0;
                return;
            }
            if (buildMode && !paused)
            {
                buildSelection = (buildSelection + (e.Offset.Y > 0 ? 1 : BuildItemCount - 1)) % BuildItemCount;
                buildRotation = 0;
                nextBuildPreview = 0;
            }
        };
        using var input = new InputManager(window);
        using var ownedFont = new TrueTypeFont(Path.Combine(AppContext.BaseDirectory, "data", "fonts", "Consolas-Regular.ttf"));
        font = ownedFont;
        using var ownedUi = new UiPainter(window.Graphics);
        ui = ownedUi;
        using var ownedGlyphs = new GlyphRenderer(window.Graphics);
        glyphs = ownedGlyphs;
        // FishGfx reports previous - current; our look angles use current - previous.
        window.MouseDelta += (_, e) =>
        {
            if (session is not null && !paused && !buildPaletteOpen && !Smoke)
                mouseDelta -= e.Delta;
        };
        window.TextInput += (_, e) =>
        {
            if (planetPanel && session is not null)
            {
                if (generatorTab) { GeneratorText(e.Text); return; }
                if (e.Text.All(c => char.IsDigit(c) || c == '-'))
                {
                    if (selectAll)
                    {
                        planetSeed = "";
                        selectAll = false;
                    }
                    if (planetSeed.Length + e.Text.Length <= 30)
                        planetSeed += e.Text;
                }
                return;
            }
            if (session is not null || Joining || e.Text.Any(char.IsControl))
                return;
            if (selectAll)
            {
                fields[field] = "";
                selectAll = false;
            }
            if (fields[field].Length + e.Text.Length <= (field == 2 ? 24 : field == 1 ? 5 : 128))
                fields[field] += e.Text;
        };
        InitializeTestControl();
        StartupTrace.Mark("ui-initialized");
        double previous = timer.Elapsed.TotalSeconds;
        try
        {
            while (!window.IsCloseRequested)
            {
                double now = timer.Elapsed.TotalSeconds;
                if (menuPresented && session is null && startupMenuFrames.Count < 36000)
                    startupMenuFrames.Add((now - previous) * 1000);
                if (Smoke && session is not null)
                    frameMilliseconds.Add((now - previous) * 1000);
                if (benchmark && session is not null && now - connectedAt > benchmarkWarmup && scene is not null)
                {
                    benchmarkFrames.Add((now - previous) * 1000);
                    benchmarkGpu.Add(scene.RenderGpuMilliseconds);
                    benchmarkAo.Add(scene.AoGpuMilliseconds);
                    benchmarkCpu.Add(scene.RenderCpuMilliseconds);
                    benchmarkWave.Add(scene.HorizonWaveGpuMilliseconds);
                    benchmarkSpray.Add(scene.GateSprayGpuMilliseconds);
                    benchmarkGrass.Add(scene.GrassGpuMilliseconds);
                    benchmarkWeather.Add(scene.WeatherGpuMilliseconds);
                    benchmarkAtmosphere.Add(scene.AtmosphereGpuMilliseconds);
                    benchmarkCloud.Add(scene.CloudGpuMilliseconds);
                    benchmarkFog.Add(scene.FogGpuMilliseconds);
                    benchmarkMistMap.Add(scene.GroundMistMapGpuMilliseconds);
                    benchmarkMistCpu.Add(scene.GroundMistMapCpuMilliseconds);
                    benchmarkWater.Add(scene.WaterGpuMilliseconds);
                    benchmarkWaterCpu.Add(scene.WaterCpuMilliseconds);
                    benchmarkWaterAllocations.Add(scene.WaterAllocatedBytes);
                    benchmarkGrassCpu.Add(scene.GrassCpuMilliseconds);
                    benchmarkWeatherCpu.Add(scene.WeatherCpuMilliseconds);
                    benchmarkSurfaceCpu.Add(scene.SurfaceCpuMilliseconds + scene.FoliageCpuMilliseconds + scene.GrassCpuMilliseconds + scene.DustCpuMilliseconds);
                    benchmarkFoliageCpu.Add(scene.FoliageCpuMilliseconds);
                    benchmarkDustCpu.Add(scene.DustCpuMilliseconds);
                    benchmarkCanopy.Add(scene.CanopyGpuMilliseconds);
                    benchmarkCanopyCpu.Add(scene.CanopyCpuMilliseconds);
                    benchmarkTerrain.Add(scene.TerrainGpuMilliseconds);
                    benchmarkDustGpu.Add(scene.World.Definition.Preset == "desert" ? scene.FogGpuMilliseconds + scene.WeatherGpuMilliseconds : 0);
                }
                previous = now;
                targetingFrame++;
                input.BeginFrame();
                window.PollEvents();
                CompleteConnection();
                PollPreparingSession();
                PumpTests(now);
                HandleInput(input);
                if (!bookInputConsumed)
                {
                    AudioInput(input);
                    LabInput(input);
                }
                if (session is not null)
                {
                    session.Poll();
                    UpdateBook();
                    InspectBuildingPicker(now - connectedAt);
                    if (scene is not null && !ReferenceEquals(scene.TerrainSource, session.Terrain))
                    {
                        scene.ChangePlanet(session.World, session.Terrain);
                    }
                    if (session.Failure is { } failure)
                        Disconnect(failure);
                    else
                    {
                        if (session.ArrivalView is { } view)
                        {
                            yaw = view.Yaw;
                            pitch = view.Pitch;
                            session.ConsumeArrivalView();
                        }
                        if (session.TransitLocked)
                            mouseDelta = Vector2.Zero;
                        yaw = MathF.IEEERemainder(yaw + mouseDelta.X * .0025f, MathF.Tau);
                        pitch = Math.Clamp(pitch - mouseDelta.Y * .0025f, -1.5f, 1.5f);
                        // The synchronized prediction clock schedules fixed steps;
                        // sample input every rendered frame instead of quantizing twice.
                        float forward = paused || session.PlanetVisualPending || buildPaletteOpen || uiInputConsumed ? 0 : (input.IsKeyDown(Key.W) ? 1 : 0) - (input.IsKeyDown(Key.S) ? 1 : 0);
                        float strafe = paused || session.PlanetVisualPending || buildPaletteOpen || uiInputConsumed ? 0 : (input.IsKeyDown(Key.D) ? 1 : 0) - (input.IsKeyDown(Key.A) ? 1 : 0);
                        if (Smoke && now - connectedAt < 1.5)
                            forward = .5f;
                        if ((testMovementReview || Smoke && args.Contains("--movement-check")))
                        {
                            double age = now - (testMovementReview ? testReviewStarted : connectedAt);
                            var staging = session.World.Definition.Props.Single(p => p.Id == (testMovementReview ? testMovementProp : 4)).Position + Vector3.UnitZ * 2;
                            yaw = 0;
                            pitch = -.15f;
                            strafe = age < 1.5 ? 0 : age < 3 ? Math.Clamp((staging.X - session.Local.Position.X) * 2, -1, 1) : age is > 8 and < 10 ? 1 : 0;
                            forward = age < 3 ? Math.Clamp((session.Local.Position.Z - staging.Z) * 2, -1, 1) : age < 7 || age > 10 ? 1 : 0;
                        }
                        if (Smoke && args.Contains("--horizon-walk-check"))
                        {
                            double age = now - connectedAt;
                            var gate = session.World.Definition.Entities.Single(e => e.Model == "stargate");
                            float z = age < 4 ? 2 : age < 6 ? .25f : age < 8 ? -2 : age < 10 ? 2 : .25f;
                            var delta = gate.Position + new Vector3(0, 0, z) - session.Local.Position;
                            yaw = 0;
                            pitch = 0;
                            strafe = Math.Clamp(delta.X * 2, -1, 1);
                            forward = Math.Clamp(-delta.Z * 2, -1, 1);
                        }
                        if (Smoke)
                        {
                            SmokeDial(now, ref strafe, ref forward);
                            InspectPickup(now, ref strafe, ref forward);
                        }
                        if (TestAutomated && !testMovementReview) { strafe = forward = 0; }
                        if (testMoveUntil > now) { strafe = testStrafe; forward = testForward; jumpQueued |= testJump; testJump = false; }
                        session.Step(strafe, forward, yaw, pitch, (testMoveUntil > now && testWalk) || !TestAutomated && !paused && !session.PlanetVisualPending && !buildPaletteOpen && input.IsKeyDown(Key.LeftShift), jumpQueued && !paused && !buildPaletteOpen && !uiInputConsumed, !paused && !buildPaletteOpen && !uiInputConsumed && input.IsKeyDown(Key.Space), (testMoveUntil > now && testCrouch) || !TestAutomated && !paused && !buildPaletteOpen && !uiInputConsumed && input.IsKeyDown(Key.LeftControl), (testMoveUntil > now && testCrouch) || !TestAutomated && !paused && !buildPaletteOpen && !uiInputConsumed && input.IsKeyDown(Key.LeftControl));
                        jumpQueued = false;
                        if (!bookInputConsumed && !uiInputConsumed && !uiMouseGuard && !session.TransitLocked)
                            BuildingInput(input);
                        else { CancelDemolition(); scene?.Demolition(null, [], false); }
                        if (Smoke)
                        {
                            SmokeActions(now);
                            SmokeAudio();
                            InspectBook(now - connectedAt);
                        }
                    }
                }
                mouseDelta = Vector2.Zero;
                if (window.Width > 0 && window.Height > 0 && window.FramebufferWidth > 0 && window.FramebufferHeight > 0)
                    Render(input, now);
                else if (session is not null && scene is not null && audio is not null)
                {
                    var a = session.RenderPrediction();
                    audio.Update(session, scene, Movement.EyePosition(a.Player), Look, a.Player, session.RemotePlayers().ToArray(), a.Bodies, now);
                }
                if (session is null)
                    audio?.Idle(now);
                if (menuPresented && !window.IsCloseRequested)
                    PumpStartup();
                if (args.Contains("--menu-seconds") && timer.Elapsed.TotalSeconds >= Arguments.Number(args, "--menu-seconds", 5, 1, 300))
                    window.IsCloseRequested = true;
                if (Smoke && timer.Elapsed.TotalSeconds > (smokeSeconds + 60) * (Arguments.Number(args, "--smoke-reconnects", 0, 0, 10) + 1))
                    throw new TimeoutException("Visual smoke did not finish.");
            }
        }
        finally
        {
            connectStop?.Cancel();
            if (connecting is not null)
            {
                try
                {
                    connecting.GetAwaiter().GetResult().Dispose();
                }
                catch (Exception)
                {
                }
            }
            DisposeLocalTesting();
            if (testPipe is not null) testPipe.DisposeAsync().AsTask().GetAwaiter().GetResult();
            DisposeStartup();
            debugTextMesh?.Dispose();
            debugTextShader?.Dispose();
            DisposeBuildThumbnails();
            rifleIcon?.Dispose();
            audio?.Dispose();
            session?.Dispose();
            scene?.Dispose();
            benchmarkTarget?.Dispose();
            connectStop?.Dispose();
            window.CaptureCursor = false;
        }
    }
    void Connect()
    {
        if (Joining)
            return;
        if (!int.TryParse(fields[1], out int port) || port < 1 || port > 65535 || string.IsNullOrWhiteSpace(fields[0]) || string.IsNullOrWhiteSpace(fields[2]))
        {
            status = "Enter a host, port (1–65535), and player name.";
            return;
        }
        connectStop?.Dispose();
        connectStop = new();
        if (startupFailure is not null)
            RestartPreparation();
        status = "Connecting...";
        startupPhase = StartupPhase.Joining;
        StartupTrace.Mark("connection-start");
        connecting = Task.Run(() => ConnectWithIdentity(fields[0].Trim(), port, fields[2].Trim(), connectStop.Token));
    }
    void CompleteConnection()
    {
        if (connecting is not { IsCompleted: true })
            return;
        try
        {
            var connection = connecting.GetAwaiter().GetResult();
            if (connectStop?.IsCancellationRequested == true)
            {
                connection.Dispose();
                status = "Connection cancelled. Ready to join again.";
                return;
            }
            StartupTrace.Mark("connection-ready");
            sessionPreparation = Task.Run(() => new ClientSession(connection));
            status = "Preparing arrival...";
        }
        catch (OperationCanceledException)
        {
            status = "Connection cancelled. Ready to join again.";
        }
        catch (Exception ex)
        {
            audio?.Clear();
            session?.Dispose();
            session = null;
            status = "Unable to connect: " + ex.Message;
            Console.Error.WriteLine(status);
            if (Smoke)
                throw;
        }
        finally
        {
            connecting = null;
        }
    }
    void Disconnect(string reason)
    {
        audio?.Clear();
        session?.Dispose();
        session = null;
        scene?.ClearPlanet();
        buildMode = false;
        buildPaletteOpen = false;
        paletteMouseGuard = false;
        previewCommand = null;
        previewEdit = null;
        CancelDemolition();
        demolitionState=null;
        window.CaptureCursor = false;
        status = reason;
        paused = false;
        inventoryOpen = false;
        planetPanel = false;
        bookOpen = false;
        bookWorld = Guid.Empty;
        jumpQueued = false;
        Console.WriteLine(reason);
    }
    void SetPause(bool value)
    {
        CancelDemolition();
        scene?.Demolition(null, [], false);
        if (!value) exposureMenu = false;
        if (value && buildPaletteOpen)
            SetBuildPalette(false);
        if (!value && audioSettingsDirty)
        {
            audio?.ApplySettings(!UiPreview);
            audioSettingsDirty = false;
        }
        uiInputConsumed = true;
        uiMouseGuard = true;
        paused = value;
        window.CaptureCursor = !paused && !Smoke && !UiPreview && !TestAutomated;
        mouseDelta = Vector2.Zero;
        jumpQueued = false;
    }
    void HandleInput(InputManager input)
    {
        bookInputConsumed = false;
        uiInputConsumed = session?.PlanetVisualPending == true;
        if (TestingInput(input)) return;
        if (!input.IsMouseButtonDown(MouseButton.Left) && !input.IsMouseButtonDown(MouseButton.Right))
            uiMouseGuard = false;
        if (session is not null)
        {
            if (InventoryInput(input)) return;
            if (bookOpen)
            {
                bookInputConsumed = true;
                BookInput(input);
                return;
            }
            if (!planetPanel && !paused && input.WasKeyPressed(Key.Tab))
            {
                bookInputConsumed = true;
                SetBook(true);
                return;
            }
            if (PaletteInput(input))
            {
                bookInputConsumed = true;
                return;
            }
            if (input.WasKeyPressed(Key.F4) && session.Developer)
            {
                if (!planetPanel) { OpenGenerator(); return; }
                planetPanel = false;
                SetPause(planetPanel);
            }
            if (planetPanel)
            {
                if (input.WasKeyPressed(Key.F2)) generatorTab = !generatorTab;
                if (generatorTab) { GeneratorInput(input); return; }
                if (input.WasMouseButtonPressed(MouseButton.Left) && DestinationTabButton.Contains(UiMouse)) { generatorTab = true; return; }
                if (input.WasKeyPressed(Key.Escape))
                {
                    planetPanel = false;
                    SetPause(false);
                    return;
                }
                if (input.WasKeyPressed(Key.C))
                    session.CancelPlanetLoad();
                if (input.WasKeyPressed(Key.Tab) && session.Destinations.Length > 0)
                {
                    planetPreset = (planetPreset + 1) % session.Destinations.Length;
                    planetSeed = string.Join('-', session.Destinations[planetPreset].Symbols);
                }
                if (input.IsKeyDown(Key.LeftControl) && input.WasKeyPressed(Key.A))
                    selectAll = true;
                if (input.WasKeyPressed(Key.Backspace))
                {
                    planetSeed = selectAll || planetSeed.Length == 0 ? "" : planetSeed[..^1];
                    selectAll = false;
                }
                bool click = input.WasMouseButtonPressed(MouseButton.Left);
                var mouse = UiMouse;
                var l = Layout;
                if (click && l.DeveloperAction(0).Contains(mouse) && session.Destinations.Length > 0)
                {
                    planetPreset = (planetPreset + 1) % session.Destinations.Length;
                    planetSeed = string.Join('-', session.Destinations[planetPreset].Symbols);
                }
                if (click && l.DeveloperAction(1).Contains(mouse))
                    selectAll = true;
                if (input.WasKeyPressed(Key.Enter) || click && l.DeveloperAction(2).Contains(mouse))
                    session.InspectDestination(planetSeed);
                if (click && l.DeveloperAction(3).Contains(mouse))
                    session.CancelPlanetLoad();
                return;
            }
            if (input.WasKeyPressed(Key.Escape))
            {
                if (buildMode)
                {
                    buildMode = false;
                    CancelDemolition();
                    scene?.Demolition(null, [], false);
                    scene?.Preview(null, false);
                    return;
                }
                SetPause(!paused);
            }
            if (input.WasKeyPressed(Key.F3))
            {
                if (input.IsKeyDown(Key.LeftShift))
                    debugCollision = !debugCollision;
                else
                    debugPhysics = !debugPhysics;
            }
            PauseKeyboard(input);
            if (PauseClick(input))
            {
                uiInputConsumed = true;
                return;
            }
            if (paused && input.WasKeyPressed(Key.G))
            {
                graphicsSettings = graphicsSettings.Next();
                scene?.SetGraphics(graphicsSettings);
            }
            if (paused && input.WasKeyPressed(Key.Enter))
                SetPause(false);
            if (paused && input.WasKeyPressed(Key.Q))
                Disconnect("Disconnected. Ready to join again.");
            if (!paused && input.WasKeyPressed(Key.Space))
                jumpQueued = true;
            return;
        }
        if (input.WasKeyPressed(Key.Escape))
        {
            if (Joining)
                CancelJoin();
            else
                window.IsCloseRequested = true;
        }
        if (Joining)
        {
            if (input.WasMouseButtonPressed(MouseButton.Left) && Layout.Join.Contains(UiMouse))
                CancelJoin();
            return;
        }
        if (input.WasKeyPressed(Key.Tab))
        {
            field = (field + 1) % 3;
            selectAll = true;
        }
        if (input.IsKeyDown(Key.LeftControl) && input.WasKeyPressed(Key.A))
            selectAll = true;
        if (input.WasKeyPressed(Key.Backspace))
        {
            fields[field] = selectAll || fields[field].Length == 0 ? "" : fields[field][..^1];
            selectAll = false;
        }
        if (input.WasKeyPressed(Key.Enter))
            Connect();
        if (input.WasMouseButtonPressed(MouseButton.Left))
        {
            Vector2 point = UiMouse;
            for (int i = 0; i < 3; i++)
                if (Layout.ConnectField(i).Contains(point))
                {
                    field = i;
                    selectAll = true;
                }
            if (Layout.Join.Contains(point))
                Connect();
        }
    }
    void SmokeActions(double now)
    {
        if (args.Contains("--weapon-smoke")) { WeaponSmoke(now); if (now - connectedAt < smokeSeconds) return; }
        if (args.Contains("--smoke-generation")) { PlanetDebugSmoke(now); return; }
        double age = now - connectedAt;
        if (args.Contains("--smoke-planets") && session!.Developer && age > nextSmokeSwitch && !session.TransitionPending)
        {
            if (session.Gate is null)
                session.SwitchPlanet(7319, PlanetSettings.Presets[(smokeSwitches + 1) % 3]);
            else
            {
                var routes = session.Destinations.Where(d => d.DhdCompatible).ToArray();
                if (routes.Length > 0)
                    session.InspectDestination(routes[smokeSwitches % routes.Length].Symbols);
            }
            smokeSwitches++;
            nextSmokeSwitch = age + 5;
            Console.WriteLine($"SMOKE switch {smokeSwitches} privateMiB={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} gpuMiB={scene!.ApproximateGpuBytes / 1048576f:F1} horizonMiB={scene.HorizonTargetBytes / 1048576f:F1} gateLightMiB={scene.GateLightTargetBytes / 1048576f:F1} environmentMiB={(scene.GrassBytes + scene.WeatherBytes) / 1048576f:F1}");
        }
        if (!benchmark && !args.Contains("--capture-only") && smokeStage == 0 && age > 2)
        {
            SetPause(true);
            window.SetClientSize(960, 600);
            smokeStage = 1;
            Console.WriteLine("SMOKE pause/release cursor and resize to 960x600");
        }
        else if (!benchmark && !args.Contains("--capture-only") && smokeStage == 1 && age > 2.4)
        {
            SetPause(false);
            smokeStage = 2;
            Console.WriteLine("SMOKE resume");
            if (args.Contains("--smoke-peer") && session!.RemotePlayers().FirstOrDefault() is { Id: > 0 } other)
            {
                var direction = other.Position - session.Local.Position;
                yaw = MathF.Atan2(direction.X, -direction.Z);
            }
        }
        else if (!benchmark && !args.Contains("--capture-only") && smokeStage == 3 && age > 4)
        {
            window.SetClientSize(1280, 800);
            debugPhysics = true;
            smokeStage = 4;
            Console.WriteLine("SMOKE resize to 1280x800 and physics overlay");
        }
        if (args.Contains("--smoke-graphics") && smokeQualityChanges < 3 && age > 6 + smokeQualityChanges * 2)
        {
            graphicsSettings = graphicsSettings.Next();
            scene!.SetGraphics(graphicsSettings);
            smokeQualityChanges++;
            Console.WriteLine($"SMOKE graphics {graphicsSettings.Quality} targetsMiB={scene.TargetBytes / 1048576f:F1}");
        }
        if (age >= smokeSeconds)
        {
            if (movementTrace.Count > 0)
            {
                Directory.CreateDirectory(captureDirectory);
                File.WriteAllLines(Path.Combine(captureDirectory, "movement.csv"), new[] { "seconds,tick,raw_x,raw_y,raw_z,draw_x,draw_y,draw_z,box_x,box_y,box_z,draw_box_x,draw_box_y,draw_box_z" }.Concat(movementTrace.Select(p => FormattableString.Invariant($"{p.Time:F6},{p.Tick},{p.Raw.X:F6},{p.Raw.Y:F6},{p.Raw.Z:F6},{p.Draw.X:F6},{p.Draw.Y:F6},{p.Draw.Z:F6},{p.Box.X:F6},{p.Box.Y:F6},{p.Box.Z:F6},{p.DrawBox.X:F6},{p.DrawBox.Y:F6},{p.DrawBox.Z:F6}"))));
            }
            Console.WriteLine($"SMOKE complete players={session!.PlayerCount} tick={session.ServerTick} position={session.Local.Position}");
            StartupTrace.Mark("streaming-snapshot", detail: $"privateMiB={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576d:F1} modelGpuMiB={(scene?.ModelGpuBytes ?? 0) / 1048576d:F1} modelBudgetMiB={(scene?.ModelBudgetBytes ?? 0) / 1048576d:F1} stagingMiB={(scene?.ModelPreparedBytes ?? 0) / 1048576d:F1} pending={scene?.ModelPending} volumeTrees={scene?.VisibleVolumeTrees} legacyLeafTrees={scene?.VisibleLegacyLeafTrees} canopySpecies={scene?.WorldCanopiesReady} canopyPending={scene?.WorldCanopiesPending}");
            if (frameMilliseconds.Count > 0)
            {
                var frames = frameMilliseconds.Order().ToArray();
                Console.WriteLine($"FRAME METRICS mean={frames.Average():F2} p95={frames[(int)(frames.Length * .95)]:F2} max={frames[^1]:F2} meshes={scene?.ResidentMeshes} pending={scene?.PendingMeshes}");
            }
            if (benchmarkFrames.Count > 0)
            {
                static string Stats(List<double> samples)
                {
                    var sorted = samples.Order().ToArray();
                    return $"mean={sorted.Average():F3} median={sorted[sorted.Length / 2]:F3} p95={sorted[(int)(sorted.Length * .95)]:F3} max={sorted[^1]:F3}";
                }
                Console.WriteLine($"AUDIO BENCHMARK samples={audioTimes.Count} update_p95_ms={P95(audioTimes):F4} voices={audio?.Active} memory={audio?.MemoryBytes} underruns={audio?.Stats.StreamUnderruns} dropped={audio?.Dropped} mixer_cpu_total_ms={audio?.MixerCpuMilliseconds:F2}");
                Console.WriteLine($"BENCHMARK {benchmarkTarget?.Width ?? window.FramebufferWidth}x{benchmarkTarget?.Height ?? window.FramebufferHeight} {graphicsSettings.Quality} vsync={vsync} samples={benchmarkFrames.Count} frame_ms {Stats(benchmarkFrames)} GPU_ms {Stats(benchmarkGpu)} AO_GPU_ms {Stats(benchmarkAo)} CPU_render_ms {Stats(benchmarkCpu)} wave_GPU_ms {Stats(benchmarkWave)} spray_GPU_ms {Stats(benchmarkSpray)} grass_GPU_ms {Stats(benchmarkGrass)} weather_GPU_ms {Stats(benchmarkWeather)} grass_CPU_ms {Stats(benchmarkGrassCpu)} weather_CPU_ms {Stats(benchmarkWeatherCpu)} grassMode={scene!.GrassRendererStatus.Replace(" ", "_")} volumePatches={scene.GrassVolumePatches} volumeMiB={scene.GrassVolumeBytes / 1048576f:F1} grassTiles={scene.GrassTiles} grassTriangles={scene.GrassTriangles} snowflakes={scene.Snowflakes} environmentMiB={(scene.GrassBytes + scene.WeatherBytes) / 1048576f:F1} targetsMiB={scene!.TargetBytes / 1048576f:F1} draws={scene.DrawCalls}");
                Console.WriteLine($"ASSETS cooked={scene.CookedModels} read_ms={scene.ModelReadMilliseconds:F2} decode_ms={scene.ModelDecodeMilliseconds:F2} queue_MiB={scene.ModelPreparedBytes / 1048576d:F2} pending={scene.ModelPending} upload_ms={scene.ModelUploadMilliseconds:F3}");
                Console.WriteLine($"CANOPY mode={(scene.CanopyLodBaseline ? "baseline" : "balanced")} GPU_ms {Stats(benchmarkCanopy)} CPU_ms {Stats(benchmarkCanopyCpu)} draws={scene.CanopyDraws} lods={string.Join(',', scene.CanopyLodCounts)} modelMiB={scene.ModelGpuBytes / 1048576d:F1}");
                Console.WriteLine($"LANDSCAPE terrain_GPU_ms {Stats(benchmarkTerrain)} preparation_CPU_ms {Stats(benchmarkSurfaceCpu)} clump_CPU_ms {Stats(benchmarkFoliageCpu)} dust_CPU_ms {Stats(benchmarkDustCpu)} dust_with_fog_GPU_ms {Stats(benchmarkDustGpu)} materialsMiB={scene.TerrainMaterialBytes / 1048576f:F1} foliageMiB={scene.FoliageBytes / 1048576f:F1} clumps={scene.FoliageInstances} clumpTriangles={scene.FoliageTriangles} sand={scene.SandParticles} dustExtinction={scene.DustExtinction:F6} surfaceMaps={scene.SurfaceMapCount}");
                Console.WriteLine($"INTERACTION CPU p95_ms hud={P95(debugHudTimes):F4} dhd={P95(dhdPickTimes):F4} collision={P95(collisionOverlayTimes):F4}");
                Console.WriteLine($"STREAM canonical={session.Terrain?.Count} detailed={scene!.ResidentMeshes - scene.FarMeshCount} far={scene.FarMeshCount} canonical_MiB={session.TerrainBytesReceived / 1048576d:F2} far_MiB={session.FarBytesReceived / 1048576d:F2} far_decode_failures={session.FarDecodeFailures} preload_MiB={session.PreloadBytesReceived / 1048576d:F2} preload_upload_max_ms={scene.PreloadUploadPeakMilliseconds:F3} preload_GPU_peak_MiB={scene.PreloadPeakGpuBytes / 1048576d:F2} preload_promotions={session.PreloadPromotions} far_decode_ms={session.FarDecodeMilliseconds:F1} terrain_decode_ms={session.TerrainDecodeMilliseconds:F1} mesh_ms={scene.MeshPreparationMilliseconds:F1} CPU_MiB={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576d:F1} scene_GPU_MiB={scene.ApproximateGpuBytes / 1048576d:F1} collision_hits={session.Physics.CollisionCacheHits} cook_max_ms={session.Physics.LongestCollisionCookMilliseconds:F2}");
                Console.WriteLine($"ATMOSPHERE GPU p95_ms sky={P95(benchmarkAtmosphere):F3} cloud={P95(benchmarkCloud):F3} fog={P95(benchmarkFog):F3} water={P95(benchmarkWater):F3} mist_map={P95(benchmarkMistMap):F3} mist_map_max={benchmarkMistMap.DefaultIfEmpty().Max():F3} mist_map_CPU={P95(benchmarkMistCpu):F3} mistMiB={scene!.GroundMistBytes / 1048576f:F2}");
                Console.WriteLine($"WATER style={(scene.RealisticWater ? "realistic" : "legacy")} diagnostic={scene.WaterDebug} view={Arguments.Get(args, "--view", "player")} camera={camera.Position} normalsReady={scene.WaterNormalsReady} GPU_ms {Stats(benchmarkWater)} CPU_ms {Stats(benchmarkWaterCpu)} allocated_bytes {Stats(benchmarkWaterAllocations)} frame_ms {Stats(benchmarkFrames)} normal_MiB={scene.WaterTextureBytes / 1048576d:F3}");
            }
            if (smokeReconnects < Arguments.Number(args, "--smoke-reconnects", 0, 0, 10))
            {
                smokeReconnects++;
                smokeStage = 0;
                smokeQualityChanges = 0;
                smokeQualityCaptures = 0;
                frameMilliseconds.Clear();
                Disconnect("SMOKE reconnect " + smokeReconnects);
                Console.WriteLine($"SMOKE cleared world; landscapeMiB={((scene?.TerrainMaterialBytes ?? 0) + (scene?.FoliageBytes ?? 0) + (scene?.DustBytes ?? 0)) / 1048576f:F1} shared models retained privateMiB={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576} horizonMiB={scene?.HorizonTargetBytes / 1048576f:F1} gateLightMiB={scene?.GateLightTargetBytes / 1048576f:F1} environmentMiB={((scene?.GrassBytes ?? 0) + (scene?.WeatherBytes ?? 0)) / 1048576f:F1} volumeMiB={scene?.VolumeBytes / 1048576f:F1} atmosphereMiB={scene?.AtmosphereBytes / 1048576f:F1}");
                Connect();
                return;
            }
            window.IsCloseRequested = true;
        }
    }
    async Task<GameConnection> ConnectWithIdentity(string host, int port, string name, CancellationToken cancellation)
    {
        using var identity = DeviceIdentity.Load(Arguments.Get(args, "--identity-profile", ""));
        int retrySeconds = Arguments.Number(args, "--connect-retry-seconds", 0, 0, 300);
        var wait = Stopwatch.StartNew();
        bool announced = false;
        while (true)
        {
            try
            {
                return await GameConnection.ConnectAsync(host, port, name, PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, cancellation, identity);
            }
            catch (System.Net.Sockets.SocketException ex) when (retrySeconds > 0 && wait.Elapsed.TotalSeconds < retrySeconds && ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
            {
                if (!announced)
                {
                    Console.WriteLine("Waiting for the local development server to start...");
                    announced = true;
                }
                await Task.Delay(500, cancellation);
            }
        }
    }
    void Render(InputManager input, double now)
    {
        // Render the requested validation size offscreen when the desktop work area
        // cannot fit it. This does not change monitor modes or gameplay resolution.
        if (Smoke && args.Contains("--exact-resolution"))
        {
            int width = Arguments.Number(args, "--width", 1920, 960, 7680), height = Arguments.Number(args, "--height", 1080, 600, 4320);
            benchmarkTarget ??= window.Graphics.CreateRenderTarget(new RenderTargetDescriptor(width, height));
            uiPreviewSize = new(width, height);
            if (scene is not null) scene.InspectionResolution = new(width, height);
        }
        if (session?.PlanetVisualPending == true && scene is not null && scene.PrepareArrival(session)) session.PlanetVisualsReady();
        InspectUi(now);
        UiBegin(now);
        if (session?.World.Definition.Environment?.DebugConfigured == true && scene is not null) { scene.TimeOfDay = null; scene.PauseSky = false; }
        bool fixedInspection = testVisualTime is not null || args.Contains("--visual-time") || testPipe is null && !args.Contains("--fixture") && !args.Contains("--environment-motion-check") && (scene?.TimeOfDay is not null || scene?.PauseSky == true);
        camera.SetPerspective(benchmarkTarget?.Width ?? window.Width, benchmarkTarget?.Height ?? window.Height, 1.45f, .05f, 6500);
        var presented = session?.RenderPrediction();
        if ((testMovementReview || Smoke && args.Contains("--movement-check")) && session is not null && presented is { } motion && movementTrace.Count < 20000)
            movementTrace.Add((now - (testMovementReview ? testReviewStarted : connectedAt), session.Physics.Tick, session.Local.Position, motion.Player.Position, session.Physics.Bodies().Single(b => b.Id == (testMovementReview ? testMovementProp : 4)).Position, motion.Bodies.Single(b => b.Id == (testMovementReview ? testMovementProp : 4)).Position));
        if (session is not null)
        {
            camera.Position = MovementCamera(presented!.Value.Player, now);
            camera.LookAt(camera.Position + new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch)));
        }
        TestCamera();
        if (Smoke && session is not null)
            VisualChecks.Camera(camera, session, ForestInspectionView(now - connectedAt), ForestCameraTime(now - connectedAt), args.Contains("--orbit"), args.Contains("--environment-traverse"), scene?.TimeOfDay);
        if (session is not null)
        {
            if (!uiInputConsumed && !uiMouseGuard)
                PickupInput(input);
            if (!bookInputConsumed && !uiInputConsumed && !uiMouseGuard && !session.TransitLocked && !carryingConsumed && !carryMouseGuard)
            {
                var pickWatch = Stopwatch.StartNew();
                DialingInput(input, camera.Position, camera.WorldForwardNormal, presented!.Value.Bodies);
                if (benchmark && now - connectedAt > benchmarkWarmup)
                    dhdPickTimes.Add(pickWatch.Elapsed.TotalMilliseconds);
                CharacterInput(input);
            }
            else
            {
                dhdHover = -1;
                if (scene is not null)
                    scene.DhdHover = -1;
            }
            if (Smoke && args.Contains("--dhd-hover-check"))
                CheckDhdHover();
        }
        uiCamera.SetOrthogonal(0, 0, UiWidth, UiHeight, -1, 1);
        if (session is not null)
        {
            var changes = session.Changes;
            if (Smoke && args.Contains("--gate-shadow-fixture"))
                changes = VisualChecks.GateShadowFixture(changes, session.World.Definition.Entities.Single(e => e.Model == "stargate"), Arguments.Get(args, "--fixture-door", "closed") == "open");
            if (Smoke && args.Contains("--weather-fixture"))
            {
                bool roof = !args.Contains("--weather-roof-off") && (!args.Contains("--weather-roof-cycle") || now - connectedAt < 10);
                scene!.WeatherFixtureRoof = roof;
                changes = VisualChecks.WeatherFixture(changes, session.World.Definition.Entities.Single(e => e.Model == "stargate"), roof);
            }
            if (testShadowFixture) changes = VisualChecks.GateShadowFixture(changes, session.World.Definition.Entities.First(e => e.Model == "stargate"), false);
            if (testWeatherFixture && scene is not null) changes = VisualChecks.WeatherFixture(changes, session.World.Definition.Entities.First(e => e.Model == "stargate"), scene.WeatherFixtureRoof);
            scene?.UpdateBuildings(changes);
        }
        if (session is not null)
        {
            session.StreamingFocus = camera.Position;
            scene?.SupplyStreaming(session);
        }
        if (session is not null) scene?.UpdateStreaming(camera.Position);
        using var frame = window.Graphics.BeginFrame();
        var remotes = session?.RemotePlayers().ToArray() ?? [];
        if (scene is not null && session is not null)
        {
            scene.CharacterStates = session.Characters;
            scene.LowerWeapon = buildMode || dhdHover >= 0 || session.Carrying;
            scene.InspectionCharacters = Arguments.Number(args, "--characters", 1, 0, 8);
            scene.HideWeapon = Arguments.Get(args,"--view","player").StartsWith("region:",StringComparison.Ordinal) || args.Contains("--tree-forest-tour") || Arguments.Get(args, "--view", "player").StartsWith("tree-forest-", StringComparison.Ordinal) || Arguments.Get(args, "--view", "player").StartsWith("horizon-", StringComparison.Ordinal) || Arguments.Get(args, "--view", "player").StartsWith("character-", StringComparison.Ordinal) || Arguments.Get(args, "--view", "player").StartsWith("settlement", StringComparison.Ordinal) || Arguments.Get(args, "--view", "player").StartsWith("tree-prototype", StringComparison.Ordinal);
            scene.InspectionAnimation = labClip >= 0 ? labClips[labClip] : Arguments.Get(args, "--animation", "") is { Length: > 0 } clip ? clip : null;
            scene.InspectionTime = labAnimationTime ?? (double.TryParse(Arguments.Get(args, "--animation-time", ""), System.Globalization.CultureInfo.InvariantCulture, out var clipTime) ? clipTime : null);
            var hintSymbols = addressBook.Selection(BookUniverse)?.Symbols ?? [];
            var hint = addressHint.Sample(session.World.Definition.LocationId, hintSymbols, now);
            scene.DhdHintActive = hintSymbols.Length == 6 && !buildMode && !bookOpen && session.Gate is { Phase: GatePhase.Idle or GatePhase.Dialing or GatePhase.Failed };
            scene.DhdHint = scene.DhdHintActive ? hint : default;
            scene.InspectActiveGate = args.Contains("--inspect-active-gate");
            scene.AuthoritativeGate = session.Gate;
            scene.EndpointGates = session.Gates;
            scene.GateVisualTick = session.GateTime;
            scene.GateConnectionStartTick = session.GateConnection?.StartTick ?? -1;
            InspectGateEffect(now - connectedAt);
            if (args.Contains("--debug-stats"))
                debugPhysics = true;
            if (args.Contains("--debug-collision"))
                debugCollision = true;
            scene.SetGraphics(graphicsSettings);
            scene.ShadowsEnabled = !args.Contains("--no-shadows");
            var snapshot = new SceneRenderer.SceneFrame(remotes, presented!.Value.Bodies, presented.Value.Player with
            {
                Yaw = yaw
            }, session.World.Definition.LocationId, session.Physics.Epoch, session.ServerTick / 60d, session.EnvironmentSeconds, session.Physics.CurrentClips());
            if (Smoke && session.World.Definition.Fixture == "graphics-lab" && Arguments.Get(args, "--view", "") == "tree-prototype-scale")
                snapshot = VisualChecks.TreeScaleReference(snapshot);
            if (Smoke && scene.TreePrototype != "off" && session.World.Definition.Fixture == "graphics-lab" && args.Contains("--tree-occlusion"))
                snapshot = VisualChecks.TreeOcclusion(snapshot);
            if (Smoke && args.Contains("--horizon-occlusion"))
                snapshot = VisualChecks.HorizonOcclusion(snapshot, session.World.Definition.Entities.Single(e => e.Model == "stargate"));
            if (Smoke && args.Contains("--horizon-props"))
                snapshot = VisualChecks.HorizonProps(snapshot, session.World.Definition.Entities.Single(e => e.Model == "stargate"), now - connectedAt, Arguments.Get(args, "--horizon-prop-mode", "motion"));
            if (Smoke && args.Contains("--horizon-players"))
                snapshot = VisualChecks.HorizonPlayers(snapshot, session.World.Definition.Entities.Single(e => e.Model == "stargate"), now - connectedAt);
            if (testHorizonProps) snapshot = VisualChecks.HorizonProps(snapshot, session.World.Definition.Entities.First(e => e.Model == "stargate"), testVisualTime ?? now - connectedAt, testPropMode);
            if (testHorizonPlayers) snapshot = VisualChecks.HorizonPlayers(snapshot, session.World.Definition.Entities.First(e => e.Model == "stargate"), testVisualTime ?? now - connectedAt);
            if (testHorizonOcclusion) snapshot = VisualChecks.HorizonOcclusion(snapshot, session.World.Definition.Entities.First(e => e.Model == "stargate"));
            if (fixedInspection)
                frozenVisualTime ??= now;
            else
                frozenVisualTime = null;
            float visualTime = float.TryParse(Arguments.Get(args, "--visual-time", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fixedTime) ? fixedTime : (float)(frozenVisualTime ?? now);
            if (testVisualTime is { } requestedTime) visualTime = requestedTime;
            scene.FreezeVisuals = fixedInspection;
            if (fixedInspection)
            {
                snapshot = snapshot with
                {
                    SimulationTime = visualTime
                };
                if (args.Contains("--visual-time"))
                    snapshot = snapshot with
                    {
                        EnvironmentSeconds = 946684800 + visualTime
                    };
            }
            scene.TreePrototype = Arguments.Get(args, "--tree-prototype", args.Contains("--tree-forest") ? "volume" : "off");
            scene.WorldVolumeTrees = Arguments.Get(args, "--trees", "volume") switch
            {
                "volume" => true,
                "mesh" => false,
                _ => throw new ArgumentException("--trees expects volume or mesh.")
            };
            scene.TreeForest = args.Contains("--tree-forest");
            scene.TreeForestSeed = Arguments.Number(args, "--tree-forest-seed", 7319, 0, int.MaxValue);
            scene.TreeSpecies = Arguments.Get(args, "--tree-species", "broadleaf");
            scene.CanopyLodBaseline = Arguments.Get(args, "--canopy-lod", "balanced") switch { "baseline" => true, "balanced" => false, _ => throw new ArgumentException("--canopy-lod expects balanced or baseline.") };
            scene.TreeVolumeInspection = Arguments.Get(args, "--tree-volume-view", "lit");
            scene.TreeSettings = scene.TreeSettings with { WindStrength = Arguments.Get(args, "--tree-wind", "on") == "off" ? 0 : .12f };
            scene.HandShadowBaseline = Smoke && args.Contains("--hand-shadow-baseline");
            scene.HandInspectionPose = Smoke ? Arguments.Get(args, "--hand-pose", "") : "";
            scene.HandInspection = Smoke ? Arguments.Get(args, "--hand-view", "lit") switch { "normals" => 1, "mapped-normals" => 2, "static" => 3, "dynamic" => 4, _ => 0 } : 0;
            scene.SettlementCamera(camera.Position);
            scene.DebugWorld = debugCollision ? session.Physics : null;
            scene.ExposureTransitHold = session.TransitWhite > 0;
            scene.ExposureDiagnosticsEnabled = debugPhysics || exposureMenu;
            ApplyTestPreview();
            scene.RenderScene(frame, camera, snapshot, visualTime, args.Contains("--settlement-overlay") ? SettlementInspection.Lines(session.World.Definition) : null);
            if (benchmark && debugCollision && now - connectedAt > benchmarkWarmup)
                collisionOverlayTimes.Add(scene.DebugLineCpuMilliseconds);
            if (audio is not null)
            {
                audio.Update(session, scene, camera.Position, camera.WorldForwardNormal, snapshot.Local, snapshot.Remotes, snapshot.Bodies, now);
                if (benchmark && now - connectedAt > benchmarkWarmup)
                    audioTimes.Add(audio.UpdateMilliseconds);
            }
            if (Smoke && args.Contains("--horizon-walk-check"))
                scene.TraceHorizonWalk(frame, camera, snapshot, visualTime);
        }
        using (var pass = frame.BeginPass(benchmarkTarget ?? window.Graphics.Backbuffer, new RenderPassDescriptor
        {
            View = new RenderView(camera),
            ClearColor = session is null ? new Color(13, 23, 30) : new Color(156, 184, 199),
            ColorLoadAction = RenderLoadAction.Clear,
            DepthLoadAction = RenderLoadAction.Clear,
            State = RenderState.Default with { CullMode = CullMode.None, BlendEnabled = false },
            Time = (float)now
        }))
        {
            if (scene is not null && session is not null)
                scene.PresentScene(pass);
            uiWatch.Restart();
            using (pass.PushView(new RenderView(uiCamera)))
            using (pass.PushState(RenderState.Default with
            {
                DepthTestEnabled = false,
                DepthWriteEnabled = false,
                CullMode = CullMode.None
            }))
            {
                if (session is null || UiConnectionPreview)
                {
                    Rect(pass, 0, 0, UiWidth, UiHeight, UiTheme.Menu);
                    DrawConnect(pass);
                }
                else
                {
                    DrawExpeditionHud(pass);
                    if (testPipe is not null && testView == "technology") Text(pass, 30, UiHeight - 90, "VISUAL INSPECTION ONLY / RINGS + ZPM", 18, UiTheme.Amber);
                    if (testPreview is not null) Text(pass, 30, UiHeight - 60, "RENDER-ONLY GATE PREVIEW", 18, UiTheme.Amber);
                    DrawDebugHud(pass, now);
                    if (fixedInspection)
                        Text(pass, 24, UiHeight - 118, "FIXED-TIME INSPECTION / animation frozen", 14, UiTheme.Amber);
                    if (!bookOpen)
                        DrawBook(pass);
                    foreach (var remote in remotes)
                    {
                        Vector4 clip = Vector4.Transform(new Vector4(remote.Position + Vector3.UnitY * 2.05f, 1), camera.View * camera.Projection);
                        if (clip.W > 0)
                        {
                            Vector2 screen = new((clip.X / clip.W + 1) * UiWidth / 2, (1 - clip.Y / clip.W) * UiHeight / 2);
                            Text(pass, screen.X - 25, screen.Y, remote.Name, 15, new Color(255, 225, 155));
                        }
                    }
                    if (debugCollision)
                        Text(pass, 24, UiHeight - 78, session.Physics.DebugGeometryIncomplete ? "Collision overlay: preparing nearby geometry" : "Collision overlay: ready", 14);

                    if (bookOpen)
                        DrawBook(pass);
                    else if (buildPaletteOpen)
                        DrawBuildingPalette(pass);
                    else if (session.PlanetVisualPending)
                    { Panel(pass, Layout.Connection); Text(pass, Layout.Connection.X + 24, Layout.Connection.Y + 60, "Preparing arrival terrain...", 20, UiTheme.Amber); }
                    else if (planetPanel)
                        DrawDeveloperConsole(pass);
                    else if (inventoryOpen)
                        DrawWeaponInventory(pass);
                    else if (testingPanel)
                        DrawTestingMenu(pass);
                    else if (paused)
                        DrawPauseConsole(pass);
                }
            }
            if (session is not null)
            {
                using (pass.PushView(new RenderView(uiCamera)))
                using (pass.PushState(RenderState.Default with
                {
                    DepthTestEnabled = false,
                    DepthWriteEnabled = false,
                    CullMode = CullMode.None
                }))
                {
                    if (session.TransitWhite > 0)
                        Rect(pass, 0, 0, UiWidth, UiHeight, new Color(255, 255, 255, (byte)(session.TransitWhite * 255)));
                    if (paused && session.TransitLocked)
                    {
                        Button(pass, Layout.TravelResume, "Resume", key: "ESC");
                        Button(pass, Layout.TravelDisconnect, "Disconnect", key: "Q");
                    }
                }
            }
            using (pass.PushView(new RenderView(uiCamera)))
            using (pass.PushState(RenderState.Default with
            {
                DepthTestEnabled = false,
                DepthWriteEnabled = false,
                CullMode = CullMode.None
            }))
                DrawUiRecoveryFixture(pass);
            uiWatch.Stop();
            if (UiChecking && uiCpuTimes.Count < 20000)
                uiCpuTimes.Add(uiWatch.Elapsed.TotalMilliseconds);
            CaptureUi(pass, now);
            RecordUi(pass, now);
            if (Smoke && session is not null)
            {
                CaptureBook(pass, now - connectedAt);
                CaptureDhdHint(pass);
            }
            if (session is not null)
            {
                CaptureGateEffect(pass, now - connectedAt);
                CaptureGateMovie(pass, now);
            }
            if (session is not null)
            {
                CaptureBuildingPicker(pass, now - connectedAt);
                CapturePickup(pass, now);
                CaptureForestMovie(pass, now - connectedAt);
                if (Smoke) CapturePrototypeForestTour(pass, now - connectedAt);
            }
            if (Smoke && args.Contains("--smoke-graphics") && smokeQualityCaptures < smokeQualityChanges && now - connectedAt > 7 + (smokeQualityChanges - 1) * 2)
            {
                Capture(pass, Path.Combine(captureDirectory, $"{fields[2]}-{graphicsSettings.Quality}-{smokeReconnects}.png"));
                smokeQualityCaptures = smokeQualityChanges;
            }
            if (Smoke && args.Contains("--travel-check") && session is not null)
            {
                string? stage = session.TransitWhite >= .999f ? "white" : smokeTravelComplete && !session.TransitLocked && session.TransitWhite == 0 ? "arrival" : smokeTravelStep == 1 ? "entry" : null;
                if (stage is not null && dialCaptures.Add("travel-" + stage))
                    Capture(pass, Path.Combine(captureDirectory, "travel-" + stage + ".png"));
            }
            if (Smoke && args.Contains("--dial-check") && !args.Contains("--gate-movie") && session?.Gate is { } gateCapture && now - connectedAt > 2)
            {
                int phase = gateCapture.Phase == GatePhase.Opening ? (int)Math.Clamp((session.GateTime - gateCapture.PhaseTick) / 15, 0, GateEffectTimeline.OpeningTicks / 15) : 0;
                string capture = $"{(gateCapture.Phase == GatePhase.Idle && gateCapture.Status.Length > 0 ? "Reset" : gateCapture.Phase.ToString())}-{gateCapture.Symbols.Length}-{phase}";
                if (dialCaptures.Add(capture))
                {
                    Capture(pass, Path.Combine(captureDirectory, $"dial-{capture}.png"));
                    Console.WriteLine($"DIAL CAPTURE {capture} status={session.DialStatus} draws={scene?.HorizonDrawCalls} lights={scene?.GateLightCount}");
                }
            }
            if (Smoke && !inspectionReadySent && scene is { PendingMeshes: 0 } && frameMilliseconds.Count >= 120 && Arguments.Get(args, "--inspection-ready-file", "") is { Length: > 0 } readyFile)
            {
                File.WriteAllText(readyFile, "Rendered 120 warmed frames with no pending meshes.");
                inspectionReadySent = true;
            }
            // A distant inspection camera may evict terrain tiles outside its view.
            bool fixtureCaptureReady = (!args.Contains("--fixture") || scene is { PendingMeshes: 0 } && (scene.ResidentMeshes == 16 || args.Contains("--tree-forest")) && frameMilliseconds.Count >= 60) && scene?.TreePrototypeReady == true && scene.WorldCanopiesPending == 0;
            if ((benchmark || args.Contains("--capture-only")) && session is not null && now - connectedAt > (benchmark ? benchmarkWarmup : Arguments.Number(args, "--capture-after", 20, 1, 300)) && fixtureCaptureReady && smokeStage == 0)
            {
                Capture(pass, Path.Combine(captureDirectory, fields[2] + "-1080p.png"));
                smokeStage = 10;
            }
            if (Smoke && args.Contains("--horizon-walk-check") && scene?.HorizonRipples > 0)
                walkCaptureStart ??= now;
            if (Smoke && walkCaptureStart is { } contactTime && motionCaptures < 6 && now > contactTime + motionCaptures * .12)
            {
                Capture(pass, Path.Combine(captureDirectory, $"walk-{motionCaptures++:D2}.png"));
            }
            if (Smoke && session is not null && (args.Contains("--horizon-motion-check") || args.Contains("--environment-motion-check")) && motionCaptures < (args.Contains("--environment-motion-check") ? 48 : 6) && now - connectedAt > 8 + motionCaptures * (args.Contains("--environment-motion-check") ? .1 : .25))
            {
                Capture(pass, Path.Combine(captureDirectory, $"motion-{motionCaptures:D2}.png"));
                Console.WriteLine($"MOTION frame={motionCaptures} seconds={now:F3} fixed={fixedInspection} ripples={scene?.HorizonRipples}");
                motionCaptures++;
            }
            if (Smoke && session is not null && args.Contains("--movement-check") && motionCaptures < 4 && now - (testMovementReview ? testReviewStarted : connectedAt) > 4.5 + motionCaptures * .25)
            {
                Capture(pass, Path.Combine(testMovementReview ? testRoot : captureDirectory, $"push-{motionCaptures:D2}.png"));
                motionCaptures++;
            }
            if (Smoke && args.Contains("--smoke-planets") && session is not null && !session.TransitionPending && nextSmokeSwitch - (now - connectedAt) < 1.5)
            {
                string path = Path.Combine(captureDirectory, fields[2] + "-" + session.World.Definition.Preset + ".png");
                if (!File.Exists(path))
                    Capture(pass, path);
            }
            if (input.WasKeyPressed(Key.F12) || Smoke && session is not null && now - connectedAt > 3 && smokeStage == 2)
            {
                Capture(pass, Path.Combine(captureDirectory, fields[2] + ".png"));
                smokeStage = 3;
            }
            if (Smoke && session is not null && now - connectedAt > 5 && smokeStage == 4)
            {
                Capture(pass, Path.Combine(captureDirectory, fields[2] + "-physics.png"));
                smokeStage = 5;
            }
            if (session is null && args.Contains("--menu-capture") && menuPresented && !menuCaptured && now >= Arguments.Number(args, "--capture-after", 2, 0, 300))
            {
                Capture(pass, Path.Combine(captureDirectory, "menu.png"));
                menuCaptured = true;
            }
            CaptureTest(pass);
            if (planetDebugCapture is { } debugCapturePath) { Capture(pass, Path.Combine(captureDirectory, debugCapturePath), true); planetDebugCapture = null; }
        }
        if (benchmarkTarget is not null)
        {
            var preview = new Camera(); preview.SetOrthogonal(0, 0, window.Width, window.Height, -1, 1);
            using var present = frame.BeginPass(window.Graphics.Backbuffer, new RenderPassDescriptor { View = new(preview), ColorLoadAction = RenderLoadAction.Clear,
                State = RenderState.Default with { DepthTestEnabled = false, DepthWriteEnabled = false, CullMode = CullMode.None } });
            // Framebuffer textures and this orthographic pass both use bottom-left origins.
            present.DrawTexturedRectangle(0, 0, window.Width, window.Height, 0, 0, 1, 1, Color.White, benchmarkTarget.ColorAttachments[0]);
        }
        frame.Present();
        testFrames++;
        if (!menuPresented)
        {
            menuPresented = true;
            StartupTrace.Mark("first-menu-presented");
        }
        if (session is not null && !firstPlayablePresented)
        {
            firstPlayablePresented = true;
            StartupTrace.Mark("first-playable-frame", detail: $"privateMiB={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576d:F1} modelGpuMiB={(scene?.ModelGpuBytes ?? 0) / 1048576d:F1} stagingMiB={(scene?.ModelPreparedBytes ?? 0) / 1048576d:F1}");
        }
        if (session is { AwaitingArrivalFrame: true } arriving)
            arriving.DestinationFrameRendered(scene?.ArrivalRegionReady(arriving.Local.Position) == true);
    }
    void DrawConnect(RenderPass pass) { if (!DrawTestingMenu(pass)) DrawConnectionConsole(pass); }
    void Text(RenderPass pass, float x, float y, string text, float size, Color? color = null)
    {
        // Snap in framebuffer pixels, including when Windows scales the window.
        Vector2 scale = UiPixelScale;
        size = MathF.Round(size * scale.Y) / scale.Y;
        Vector2 position = new(MathF.Round(x * scale.X) / scale.X,
            MathF.Round((UiHeight - y - size) * scale.Y) / scale.Y);
        Vector2 shadowOffset = new(1 / scale.X, -1 / scale.Y);
        pass.DrawText(font, position + shadowOffset, text, new Color(0, 0, 0, 165), size);
        pass.DrawText(font, position, text, color ?? UiTheme.Text, size);
    }
    void Rect(RenderPass pass, float x, float y, float w, float h, Color color)
        => pass.FillRectangle(x, UiHeight - y - h, w, h, color);
    static void Capture(RenderPass pass, string path, bool quiet = false)
    {
        int width = pass.Target.Width, height = pass.Target.Height;
        var rgba = new byte[width * height * 4];
        pass.ReadColorRgba32(rgba);
        var bgra = new byte[rgba.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int source = ((height - y - 1) * width + x) * 4, dest = (y * width + x) * 4;
                bgra[dest] = rgba[source + 2];
                bgra[dest + 1] = rgba[source + 1];
                bgra[dest + 2] = rgba[source];
                bgra[dest + 3] = 255;
            }
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, width, height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bitmap.Save(path, path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? System.Drawing.Imaging.ImageFormat.Jpeg : System.Drawing.Imaging.ImageFormat.Png);
        if (!quiet)
            Console.WriteLine("Screenshot: " + path);
    }
}
