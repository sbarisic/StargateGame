using System.Diagnostics;
using FishGfx;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Testing;

namespace StargateGame.Client;

sealed partial class GameApp
{
    string testingPage = "home", testingMessage = "";
    bool testingPanel;
    int testingBookmark, testingStation;
    ScenarioOptions menuScenario = new();
    Task? localWork;
    Process? localCoordinator;
    SessionManifest? localManifest;
    string? previousProfile;
    AddressBookState? previousBook;
    AmbientOcclusionSettings? previousAo;
    ToneMappingSettings? previousTone;
    string[]? previousConnection;
    GraphicsSettings? previousGraphics;
    StargateGame.Audio.AudioSettings? previousAudio;
    string ManualPreferences => Path.Combine(StargateGame.Shared.UserData.Root, "Testing", "last-scenario.json");
    UiRect TestingPanel => new((UiWidth - 680) / 2, Math.Max(12, (UiHeight - 570) / 2), 680, 570);
    UiRect TestButton(int row, int column = 0) => new(TestingPanel.X + 24 + column * 320, TestingPanel.Y + 80 + row * 46, 310, 38);
    bool LocalBusy => localWork is { IsCompleted: false };
    void PollLocalTesting()
    {
        if (localWork is { IsCompleted: true } task)
        {
            localWork = null;
            try { task.GetAwaiter().GetResult(); testingMessage = "Ready"; }
            catch (Exception e) { testingMessage = e.Message; if (session is null) { DisposeLocalTesting(); testingPage = "scenarios"; } }
        }
    }
    void StartLocalScenario(ScenarioOptions value)
    {
        if (LocalBusy) return;
        menuScenario = ScenarioCatalog.Resolve(value); TestJson.Write(ManualPreferences, menuScenario);
        testingMessage = "Starting " + menuScenario.Scenario + "...";
        if (localManifest is not null)
        {
            LocalCommand(TestAction.SwitchScenario, menuScenario); return;
        }
        string root;
        try { root = SessionLauncher.FindBuildRoot(AppContext.BaseDirectory); }
        catch (Exception e) { testingMessage = e.Message; return; }
        testSession = Guid.NewGuid().ToString("N"); testGeneration = 1;
        string run = SessionLauncher.NewRoot(testSession);
        testRoot = Path.Combine(run, "client-1"); Directory.CreateDirectory(testRoot);
        previousProfile = Environment.GetEnvironmentVariable("STARGATEGAME_USER_DATA");
        Environment.SetEnvironmentVariable("STARGATEGAME_USER_DATA", Path.Combine(testRoot, "profile"));
        previousBook = addressBook; addressBook = AddressBookState.Load(BookPath);
        previousAo = aoSettings; aoSettings = AmbientOcclusionSettings.Load();
        previousTone = toneMappingSettings; toneMappingSettings = ToneMappingSettings.Load();
        previousConnection = (string[])fields.Clone(); previousGraphics = graphicsSettings;
        if (audio is not null) previousAudio = new() { Master = audio.Settings.Master, Effects = audio.Settings.Effects, Ambience = audio.Settings.Ambience, Ui = audio.Settings.Ui, Muted = audio.Settings.Muted };
        string pipe = "stargate-test-" + testSession + "-menu";
        testPipe = new LocalPipe(pipe, testQueue.Enqueue);
        var launch = new LaunchOptions(testSession, run, root, menuScenario, OwnerPid: Environment.ProcessId, AttachedClientPipe: pipe, Manual: true);
        Process coordinator;
        try { coordinator = localCoordinator = SessionLauncher.Launch(launch); }
        catch (Exception e) { DisposeLocalTesting(); testingMessage = e.Message; return; }
        localWork = Task.Run(async () =>
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed.TotalSeconds < 240)
            {
                string file = Path.Combine(run, "session.json");
                if (File.Exists(file))
                {
                    var manifest = TestJson.Read<SessionManifest>(file);
                    if (manifest.State == "ready") { localManifest = manifest; return; }
                    if (manifest.State == "failed") throw new InvalidOperationException(manifest.Error);
                }
                if (coordinator.HasExited) throw new InvalidOperationException("Local coordinator exited.");
                await Task.Delay(100);
            }
            throw new TimeoutException("Local scenario startup timed out. See " + run);
        });
        testingPanel = false;
    }
    void LocalCommand(TestAction action, object? data = null)
    {
        if (LocalBusy || localManifest is null) return;
        var manifest = localManifest;
        testingMessage = action + "...";
        localWork = Task.Run(async () =>
        {
            var result = await LocalPipe.Send(manifest.Pipe, new() { Session = manifest.Id, Generation = manifest.Generation, Action = action, Data = TestJson.Element(data ?? new { }), TimeoutSeconds = 240 });
            result.EnsureSuccess();
            localManifest = TestJson.Read<SessionManifest>(Path.Combine(manifest.Root, "session.json"));
        });
    }
    bool TestingInput(InputManager input)
    {
        PollLocalTesting();
        if (UiPreview) return false;
        bool click = input.WasMouseButtonPressed(MouseButton.Left);
        if (session is not null)
        {
            if (input.WasKeyPressed(Key.F6) && localManifest is not null) { testingPanel = !testingPanel; SetPause(testingPanel); }
            if (!testingPanel) return false;
            uiInputConsumed = true; bookInputConsumed = true;
            if (input.WasKeyPressed(Key.Escape)) { testingPanel = false; SetPause(false); return true; }
            if (!click || LocalBusy) return true;
            for (int i = 0; i < 3; i++) if (TestButton(i, 0).Contains(UiMouse)) StartLocalScenario(menuScenario with { Scenario = ScenarioCatalog.Scenarios[i].Id });
            var definition = ScenarioCatalog.Scenarios.Single(s => s.Id == menuScenario.Scenario);
            var bookmarks = definition.Bookmarks; var stations = definition.Stations;
            if (TestButton(1, 1).Contains(UiMouse)) testingStation = (testingStation + 1) % stations.Length;
            if (TestButton(2, 1).Contains(UiMouse)) LocalCommand(TestAction.Teleport, new { station = stations[testingStation % stations.Length] });
            if (TestButton(3).Contains(UiMouse)) testingBookmark = (testingBookmark + 1) % bookmarks.Length;
            if (TestButton(3, 1).Contains(UiMouse)) LocalCommand(TestAction.Camera, new { bookmark = bookmarks[testingBookmark % bookmarks.Length] });
            if (TestButton(4).Contains(UiMouse)) LocalCommand(TestAction.ResetPlayer);
            if (TestButton(4, 1).Contains(UiMouse)) { if (menuScenario.Scenario == "sandbox") LocalCommand(TestAction.ResetStation, new { station = stations[testingStation % stations.Length] }); else testingMessage = "Generated worlds use Reset Player or Reset Scenario."; }
            if (TestButton(5).Contains(UiMouse)) LocalCommand(TestAction.ResetScenario);
            if (TestButton(5, 1).Contains(UiMouse)) LocalCommand(TestAction.AddClient);
            if (TestButton(6).Contains(UiMouse)) LocalCommand(TestAction.Capture, new { name = "manual-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".png" });
            if (TestButton(6, 1).Contains(UiMouse)) { testingPanel = false; OpenGenerator(); }
            if (TestButton(7).Contains(UiMouse)) { menuScenario = menuScenario with { Hour = (menuScenario.Hour + 6) % 24 }; LocalCommand(TestAction.Settings, new { hour = menuScenario.Hour }); }
            if (TestButton(7, 1).Contains(UiMouse)) { graphicsSettings = graphicsSettings.Next(); scene?.SetGraphics(graphicsSettings); }
            if (TestButton(8).Contains(UiMouse)) { testingPanel = false; SetPause(false); }
            if (TestButton(8, 1).Contains(UiMouse)) { Disconnect("Local test closed."); DisposeLocalTesting(); testingPanel = false; testingPage = "home"; }
            return true;
        }
        if (localCoordinator is not null && input.WasKeyPressed(Key.Escape)) { CancelJoin(); DisposeLocalTesting(); testingPage = "scenarios"; return true; }
        if (args.Contains("--host") || args.Contains("--connect") || Smoke || testPipe is not null) return false;
        if (testingPage == "remote")
        {
            if (input.WasKeyPressed(Key.Escape) && !Joining) { testingPage = "home"; return true; }
            return false;
        }
        if (input.WasKeyPressed(Key.Escape)) { if (testingPage == "home") window.IsCloseRequested = true; else testingPage = "home"; return true; }
        if (!click) return true;
        if (testingPage == "home")
        {
            if (TestButton(0).Contains(UiMouse))
            {
                testingPage = "scenarios";
                try { if (File.Exists(ManualPreferences)) menuScenario = ScenarioCatalog.Resolve(TestJson.Read<ScenarioOptions>(ManualPreferences)); } catch (Exception) { menuScenario = new(); }
            }
            if (TestButton(0, 1).Contains(UiMouse)) testingPage = "remote";
        }
        else
        {
            for (int i = 0; i < 3; i++) if (TestButton(i).Contains(UiMouse)) StartLocalScenario(menuScenario with { Scenario = ScenarioCatalog.Scenarios[i].Id });
            if (TestButton(3).Contains(UiMouse)) { string[] biomes = ["temperate", "icy", "desert"]; menuScenario = menuScenario with { Biome = biomes[(Array.IndexOf(biomes, menuScenario.Biome) + 1) % 3], Preset = "default" }; }
            if (TestButton(4).Contains(UiMouse)) { var presets = ScenarioCatalog.Presets; menuScenario = menuScenario with { Preset = presets[(Array.FindIndex(presets, p => p.Id == menuScenario.Preset) + 1) % presets.Length].Id }; }
            if (TestButton(5).Contains(UiMouse)) menuScenario = menuScenario with { Landscape = menuScenario.Landscape == "Coastal" ? "Inland" : "Coastal", Preset = "default" };
            if (TestButton(6).Contains(UiMouse)) { string[] settlements = ["wilderness", "village", "city", "ruins"]; menuScenario = menuScenario with { Settlement = settlements[(Array.IndexOf(settlements, menuScenario.Settlement) + 1) % settlements.Length] }; }
            if (TestButton(8).Contains(UiMouse)) testingPage = "home";
        }
        return true;
    }
    bool DrawTestingMenu(RenderPass pass)
    {
        if (session is null && localCoordinator is not null)
        {
            Panel(pass, TestingPanel, true);
            Text(pass, TestingPanel.X + 24, TestingPanel.Y + 30, "LOCAL TESTING", 22, UiTheme.Amber);
            Wrapped(pass, new(TestingPanel.X + 24, TestingPanel.Y + 95, TestingPanel.W - 48, 160), testingMessage + "\n" + StartupStatus + "\nEscape to cancel", 18, UiTheme.Cyan);
            return true;
        }
        bool main = session is null && testingPage != "remote" && !UiPreview && !Smoke && !args.Contains("--host") && !args.Contains("--connect") && testPipe is null;
        if (!main && !testingPanel) return false;
        Panel(pass, TestingPanel, true);
        Text(pass, TestingPanel.X + 24, TestingPanel.Y + 25, testingPanel ? "LOCAL TESTING / F6" : "STARGATE / TESTING", 22, UiTheme.Amber);
        if (main && testingPage == "home") { Button(pass, TestButton(0), "Local Testing"); Button(pass, TestButton(0, 1), "Connect to Server"); }
        else
        {
            for (int i = 0; i < 3; i++) Button(pass, TestButton(i), ScenarioCatalog.Scenarios[i].Name);
            if (!testingPanel)
            {
                Button(pass, TestButton(3), "Biome: " + menuScenario.Biome); Button(pass, TestButton(4), "Preset: " + menuScenario.Preset);
                Button(pass, TestButton(5), "Landscape: " + menuScenario.Landscape); Button(pass, TestButton(6), "Settlement: " + menuScenario.Settlement); Button(pass, TestButton(8), "Back");
            }
            else
            {
                var definition = ScenarioCatalog.Scenarios.Single(s => s.Id == menuScenario.Scenario);
            var bookmarks = definition.Bookmarks; var stations = definition.Stations;
                Button(pass, TestButton(1, 1), "Station: " + stations[testingStation % stations.Length]);
                Button(pass, TestButton(2, 1), "Move player to station");
                Button(pass, TestButton(3), "View: " + bookmarks[testingBookmark % bookmarks.Length]); Button(pass, TestButton(3, 1), "Inspect camera");
                Button(pass, TestButton(4), "Reset Player"); Button(pass, TestButton(4, 1), "Reset Station");
                Button(pass, TestButton(5), "Reset Scenario"); Button(pass, TestButton(5, 1), "Add second client");
                Button(pass, TestButton(6), "Capture"); Button(pass, TestButton(6, 1), "Planet settings / F4");
                Button(pass, TestButton(7), "Time: " + menuScenario.Hour); Button(pass, TestButton(7, 1), "Graphics quality");
                Button(pass, TestButton(8), "Resume"); Button(pass, TestButton(8, 1), "Stop and return");
            }
        }
        Wrapped(pass, new(TestingPanel.X + 24, TestingPanel.Bottom - 58, TestingPanel.W - 48, 50), testingMessage, 14, UiTheme.Cyan);
        return true;
    }
    void DisposeLocalTesting()
    {
        if (localCoordinator is null && testPipe is null) return;
        var coordinator = localCoordinator; string closingSession = testSession;
        // Never block the render thread on a command that may need that thread.
        if (coordinator is not null) _ = Task.Run(async () =>
        {
            try
            {
                if (!coordinator.HasExited)
                {
                    try { await LocalPipe.Send("stargate-test-" + closingSession, new() { Session = closingSession, Action = TestAction.Stop, TimeoutSeconds = 2 }); } catch { }
                    try { await coordinator.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(12)); } catch (TimeoutException) { coordinator.Kill(true); }
                }
            }
            catch (InvalidOperationException) { }
            finally { coordinator.Dispose(); }
        });
        localCoordinator = null; localManifest = null; localWork = null;
        if (testPipe is not null) { testPipe.DisposeAsync().AsTask().GetAwaiter().GetResult(); testPipe = null; }
        Environment.SetEnvironmentVariable("STARGATEGAME_USER_DATA", previousProfile);
        if (previousBook is not null) addressBook = previousBook;
        if (previousAo is not null) aoSettings = previousAo;
        if (previousTone is not null) toneMappingSettings = previousTone;
        if (previousGraphics is not null) graphicsSettings = previousGraphics;
        if (previousConnection is not null) Array.Copy(previousConnection, fields, fields.Length);
        if (scene is not null) { scene.AoSettings = aoSettings; scene.ToneMapping = toneMappingSettings; scene.SetGraphics(graphicsSettings); }
        if (audio is not null) { var restored = previousAudio ?? StargateGame.Audio.AudioSettings.Load(); audio.Settings.Master = restored.Master; audio.Settings.Effects = restored.Effects; audio.Settings.Ambience = restored.Ambience; audio.Settings.Ui = restored.Ui; audio.Settings.Muted = restored.Muted; audio.ApplySettings(false); }
        scene?.SetTestingInspection(false); if (scene is not null) scene.InspectionCamera = false;
        testWeatherFixture = false; testPreview = null; testView = "player"; testPending = null; testMoveUntil = 0;
    }
}
