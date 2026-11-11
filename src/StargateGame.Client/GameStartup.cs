using System.Diagnostics;
using StargateGame.Audio;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class GameApp
{
    enum StartupPhase
    {
        UiReady, PreparingResources, Joining, Playable
    }
    StartupPhase startupPhase = StartupPhase.UiReady;
    bool menuPresented, menuCaptured, startupStarted, sceneConfigured, firstPlayablePresented;
    string? startupFailure;
    readonly CancellationTokenSource startupCancellation = new();
    readonly AudioSettings startupAudioSettings = AudioSettings.Load();
    Task<CookedAssets>? assetPreparation;
    Task<WorldAudioController>? audioPreparation;
    Task<ClientSession>? sessionPreparation;
    ClientSession? preparingSession;
    bool Joining => connecting is not null || sessionPreparation is not null || preparingSession is not null;
    string StartupStatus => startupFailure ?? (startupPhase == StartupPhase.Joining ? "Joining / " : "") + (preparingSession is not null ? scene?.PreparationStage ?? "Preparing resources..." : scene?.CommonReady == true ? "Resources ready" : scene?.PreparationStage ?? "Preparing resources...");
    readonly List<double> startupMenuFrames = [];
    void ReportMenuTiming()
    {
        if (startupMenuFrames.Count == 0)
            return;
        StartupTrace.Mark("menu-frame-timing", detail: $"samples={startupMenuFrames.Count} p95={P95(startupMenuFrames):F2}ms max={startupMenuFrames.Max():F2}ms (includes presentation wait and preparation)");
        startupMenuFrames.Clear();
    }

    void BeginPreparation()
    {
        startupPhase = Joining ? StartupPhase.Joining : StartupPhase.PreparingResources;
        bool loose = args.Contains("--loose-assets");
        assetPreparation = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            string? development = args.Contains("--dev-assets")
                ? Arguments.Get(args, "--dev-assets", "")
                : loose ? null : StargateGame.Testing.SessionLauncher.DevelopmentAssets(AppContext.BaseDirectory);
            var assets = new CookedAssets(Path.Combine(AppContext.BaseDirectory, "assets"), loose,
                development);
            StartupTrace.Mark("asset-index-ready", clock.Elapsed.TotalMilliseconds, assets.IsCooked ? "cooked" : "loose");
            return assets;
        });
    }
    void RestartPreparation()
    {
        scene?.Dispose();
        scene = null;
        sceneConfigured = false;
        startupFailure = null;
        BeginPreparation();
    }
    void PumpStartup()
    {
        if (!startupStarted)
        {
            startupStarted = true;
            BeginPreparation();
            bool enabled = Arguments.Get(args, "--audio", "on") switch
            {
                "on" => true,
                "off" => false,
                _ => throw new ArgumentException("--audio must be on or off.")
            };
            if (enabled)
                audioPreparation = Task.Run(() =>
                {
                    var clock = Stopwatch.StartNew();
                    var result = new WorldAudioController(settings: startupAudioSettings, cancellation: startupCancellation.Token) { Trace = args.Contains("--audio-trace") };
                    StartupTrace.Mark("audio-ready", clock.Elapsed.TotalMilliseconds);
                    return result;
                });
            if (args.Contains("--connect") || args.Contains("--host") || Smoke)
                Connect();
            return;
        }
        if (audioPreparation is { IsCompleted: true } audioTask)
        {
            audioPreparation = null;
            try
            {
                audio = audioTask.GetAwaiter().GetResult();
                string record = Arguments.Get(args, "--audio-record", "");
                if (record.Length > 0)
                {
                    audio.Record(record);
                    audioRecordStarted = timer.Elapsed.TotalSeconds;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Audio unavailable; continuing silently: " + ex.Message);
                StartupTrace.Mark("audio-unavailable", detail: ex.Message);
            }
        }
        if (startupFailure is not null)
            return;
        try
        {
            if (assetPreparation is { IsCompleted: true } assets)
            {
                assetPreparation = null;
                var ready = assets.GetAwaiter().GetResult();
                try
                {
                    scene = new SceneRenderer(window.Graphics, ready) { ShaderCacheEnabled = !args.Contains("--no-shader-cache") };
                }
                catch
                {
                    ready.Dispose();
                    throw;
                }
                return;
            }
            if (scene is null)
                return;
            if (!scene.CommonReady)
            {
                scene.PumpPreparation();
                return;
            }
            if (!sceneConfigured)
            {
                ConfigureScene();
                sceneConfigured = true;
                if (testPipe is not null) ApplyTestSettings(new() { Data = StargateGame.Testing.TestJson.Element(new { hour = testOptions.Hour, weather = testOptions.Weather, quality = testOptions.Quality }) });
                return;
            }
            if (preparingSession is not null && scene.PrepareArrival(preparingSession))
            {
                ReportMenuTiming();
                session = preparingSession;
                preparingSession = null;
                session.PrepareGateVisuals = _ => scene.PrepareGateVisualResources();
                planetSeed = session.World.Definition.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                planetPreset = Math.Max(0, Array.IndexOf(PlanetSettings.Presets, session.World.Definition.Preset));
                yaw = session.Local.Yaw;
                pitch = session.Local.Pitch;
                paused = false;
                window.CaptureCursor = !Smoke && !TestAutomated;
                connectedAt = timer.Elapsed.TotalSeconds;
                firstPlayablePresented = false;
                startupPhase = StartupPhase.Playable;
                StartupTrace.Mark("arrival-resources-ready");
                Console.WriteLine($"Connected #{session.Local.Id} to {session.World.Definition.Id}");
            }
        }
        catch (Exception ex)
        {
            startupFailure = "Resource preparation failed: " + ex.Message;
            status = startupFailure;
            StartupTrace.Mark("preparation-failed", detail: ex.ToString());
            CancelJoin();
            // Release half-uploaded resources while the graphics context is valid.
            scene?.Dispose();
            scene = null;
            sceneConfigured = false;
        }
    }
    void PollPreparingSession()
    {
        if (sessionPreparation is { IsCompleted: true } task)
        {
            sessionPreparation = null;
            try
            {
                var ready = task.GetAwaiter().GetResult();
                if (connectStop?.IsCancellationRequested == true || startupFailure is not null)
                {
                    ready.Dispose();
                    return;
                }
                preparingSession = ready;
                StartupTrace.Mark("collision-session-ready");
            }
            catch (Exception ex)
            {
                status = "Unable to prepare arrival: " + ex.Message;
                StartupTrace.Mark("arrival-failed", detail: ex.ToString());
            }
        }
        if (preparingSession is not { } pending)
            return;
        pending.Poll();
        if (pending.Failure is { } failure)
        {
            CancelJoin();
            status = "Unable to connect: " + failure;
            return;
        }
        pending.Step(0, 0, pending.Local.Yaw, pending.Local.Pitch, false, false);
    }
    void CancelJoin()
    {
        connectStop?.Cancel();
        preparingSession?.Dispose();
        preparingSession = null;
        status = startupFailure ?? "Connection cancelled. Ready to join again.";
        startupPhase = StartupPhase.PreparingResources;
    }
    void DisposeStartup()
    {
        ReportMenuTiming();
        startupCancellation.Cancel();
        preparingSession?.Dispose();
        preparingSession = null;
        // No worker owns GL resources. Late CPU completions are disposed without
        // blocking window shutdown or publishing a cancelled connection.
        if (sessionPreparation is { } sessionTask)
            _ = sessionTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Dispose();
                else
                    _ = t.Exception;
            });
        if (audioPreparation is { } audioTask)
            _ = audioTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Dispose();
                else
                    _ = t.Exception;
            });
        if (assetPreparation is { } assetTask)
            _ = assetTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Dispose();
                else
                    _ = t.Exception;
            });
        startupCancellation.Dispose();
    }
}
