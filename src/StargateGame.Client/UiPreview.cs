using System.Numerics;
using FishGfx;
using FishGfx.Formats;
using FishGfx.Game;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class GameApp
{
    Vector2? uiPreviewSize;
    float uiPreviewDpi = 1;
    bool UiPreview => args.Contains("--ui-preview");
    static readonly Guid PreviewUniverse = new("2dd40ec7-ec2b-49c5-bf60-1dbd81ed26bd");
    void RunUiPreview()
    {
        // Offscreen validation must not steal focus from a live game or another agent.
        if (args.Contains("--ui-hidden"))
        {
            RenderWindow.GetMonitors();
            Glfw3.Glfw.WindowHint(Glfw3.Glfw.Hint.Visible, false);
        }
        using var ownedWindow = new RenderWindow(new RenderWindowOptions { Width = 960, Height = 600, Title = "Stargate / UI layout preview (fixture data)", Resizable = true, VSync = true, Visible = !args.Contains("--ui-hidden"), FocusOnShow = !args.Contains("--no-focus") });
        window = ownedWindow;
        using var ownedFont = new TrueTypeFont(Path.Combine(AppContext.BaseDirectory, "data/fonts/Consolas-Regular.ttf"));
        font = ownedFont;
        using var ownedUi = new UiPainter(window.Graphics);
        ui = ownedUi;
        using var ownedGlyphs = new GlyphRenderer(window.Graphics);
        glyphs = ownedGlyphs;
        // Fixture-only entries. No network connection, gameplay state, or profile writes.
        addressBook.Entries.Clear();
        addressBook.Selected.Clear();
        for (int i = 0; i < 8; i++)
            addressBook.Visit(new(PreviewUniverse, Guid.Parse($"00000000-0000-0000-0000-{i + 1:D12}"), i == 7 ? "Preview destination / exceptionally long name to verify truncation" : "Preview destination " + (i + 1), [2, 5, 9, 13, 24, 37]));
        addressBook.Select(addressBook.History(PreviewUniverse).First());
        audio = new WorldAudioController();
        using var input = new InputManager(window);
        var backgroundPath = Arguments.Get(args, "--ui-background", "");
        using var background = backgroundPath.Length > 0 ? window.Graphics.LoadTexture(backgroundPath) : null;
        RenderTarget? target = null;
        uiPreviewDpi = Arguments.Number(args, "--ui-dpi", 100, 100, 200) / 100f;
        bool interactive = args.Contains("--ui-interactive");
        if (interactive)
            uiCaptureStage = 1;
        double nextRecord = 0;
        int recordFrame = 0;
        try
        {
            while (!window.IsCloseRequested)
            {
                input.BeginFrame();
                window.PollEvents();
                if (input.WasKeyPressed(Key.F12))
                {
                    window.IsCloseRequested = true;
                    break;
                }
                if (interactive)
                {
                    int old = uiCaptureStage;
                    if (input.WasKeyPressed(Key.F1))
                        uiCaptureStage = 0;
                    if (input.WasKeyPressed(Key.F2))
                        uiCaptureStage = 1;
                    if (input.WasKeyPressed(Key.F3))
                        uiCaptureStage = 2;
                    if (input.WasKeyPressed(Key.F4))
                        uiCaptureStage = 3;
                    if (input.WasKeyPressed(Key.F5))
                        uiCaptureStage = 5;
                    if (old == uiCaptureStage)
                    {
                        if (bookOpen)
                        {
                            BookInput(input);
                            if (!bookOpen)
                                uiCaptureStage = 0;
                        }
                        else if (buildPaletteOpen)
                        {
                            paletteInspectionHolding = true;
                            PaletteInput(input);
                            if (!buildPaletteOpen)
                                uiCaptureStage = 0;
                        }
                        else if (paused && !planetPanel)
                        {
                            PauseKeyboard(input);
                            PauseClick(input);
                            AudioInput(input);
                            if (input.WasKeyPressed(Key.Escape) || input.WasKeyPressed(Key.Enter))
                                SetPause(false);
                            if (!paused)
                                uiCaptureStage = 0;
                        }
                    }
                }
                else if (input.WasKeyPressed(Key.Escape))
                {
                    window.IsCloseRequested = true;
                    break;
                }
                if (uiCaptureApplied != uiCaptureStage)
                {
                    var size = UiCaptureSizes[uiCaptureStage / UiCaptureScreens.Length];
                    uiPreviewSize = new(size.W, size.H);
                    if (target is null || target.Width != (int)(size.W * uiPreviewDpi) || target.Height != (int)(size.H * uiPreviewDpi))
                    {
                        target?.Dispose();
                        target = window.Graphics.CreateRenderTarget(new RenderTargetDescriptor((int)(size.W * uiPreviewDpi), (int)(size.H * uiPreviewDpi)));
                    }
                    window.SetClientSize(Math.Min(1280, size.W), Math.Min(800, size.H));
                    bookOpen = UiCaptureScreen == "addresses";
                    buildPaletteOpen = UiCaptureScreen == "picker";
                    paused = UiCaptureScreen is "pause" or "developer" or "travel-recovery";
                    planetPanel = UiCaptureScreen == "developer";
                    status = UiCaptureScreen == "error" ? "Unable to connect: The expedition server did not answer. Verify the server address and port, then try again. This deliberately long error checks wrapping and panel bounds." : "Enter a server address to begin.";
                    uiCaptureApplied = uiCaptureStage;
                    uiCaptureStarted = timer.Elapsed.TotalSeconds;
                }
                UiBegin(timer.Elapsed.TotalSeconds);
                uiCamera.SetOrthogonal(0, 0, UiWidth, UiHeight, -1, 1);
                using var frame = window.Graphics.BeginFrame();
                using (var pass = frame.BeginPass(target!, new RenderPassDescriptor { View = new RenderView(uiCamera), ClearColor = new Color(40, 52, 66), ColorLoadAction = RenderLoadAction.Clear, DepthLoadAction = RenderLoadAction.Clear, State = RenderState.Default with { DepthTestEnabled = false, DepthWriteEnabled = false, CullMode = CullMode.None } }))
                {
                    if (background is not null)
                        pass.DrawTexturedRectangle(0, 0, UiWidth, UiHeight, 0, 0, 1, 1, Color.White, background);
                    uiWatch.Restart();
                    if (UiCaptureScreen is "connection" or "error")
                        DrawConnectionConsole(pass);
                    else
                    {
                        DrawExpeditionHud(pass);
                        if (!bookOpen)
                            DrawBook(pass);
                        switch (UiCaptureScreen)
                        {
                            case "inventory":
                                DrawWeaponInventory(pass);
                                break;
                            case "pause":
                                DrawPauseConsole(pass);
                                break;
                            case "addresses":
                                DrawBook(pass);
                                break;
                            case "picker":
                                DrawBuildingPalette(pass);
                                break;
                            case "developer":
                                DrawDeveloperConsole(pass);
                                break;
                            case "diagnostics":
                                ui.Frame(pass, new UiRect(20, 94, Math.Min(1180, UiWidth - 40), UiHeight - 210), UiTheme.Diagnostics);
                                Text(pass, 34, 104, "DIAGNOSTICS / PREVIEW ONLY", 14, UiTheme.Amber);
                                Text(pass, 34, 136, "Live metrics require an active world session.", 14);
                                break;
                            case "travel-recovery":
                                DrawUiRecoveryFixture(pass);
                                break;
                        }
                    }
                    Text(pass, UiWidth / 2 - (interactive ? 310 : 190), UiHeight - 20, interactive ? "PREVIEW: F1 HUD / F2 PAUSE / F3 BOOK / F4 PICKER / F12 EXIT" : "UI LAYOUT PREVIEW / FIXTURE DATA", 14, UiTheme.Muted);
                    uiWatch.Stop();
                    if (uiCpuTimes.Count < 20000)
                        uiCpuTimes.Add(uiWatch.Elapsed.TotalMilliseconds);
                    if (!interactive)
                        CaptureUi(pass, timer.Elapsed.TotalSeconds);
                    else if (args.Contains("--ui-record") && timer.Elapsed.TotalSeconds >= nextRecord && recordFrame < 1200)
                    {
                        Capture(pass, Path.Combine(captureDirectory, $"interaction-{recordFrame++:D4}.png"), true);
                        nextRecord = timer.Elapsed.TotalSeconds + .125;
                    }
                }
                var previewCamera = new Camera();
                previewCamera.SetOrthogonal(0, 0, window.Width, window.Height, -1, 1);
                using (var present = frame.BeginPass(window.Graphics.Backbuffer, new RenderPassDescriptor { View = new RenderView(previewCamera), ColorLoadAction = RenderLoadAction.Clear, State = RenderState.Default with { DepthTestEnabled = false, DepthWriteEnabled = false, CullMode = CullMode.None } }))
                    present.DrawTexturedRectangle(0, 0, window.Width, window.Height, 0, 0, 1, 1, Color.White, target!.ColorAttachments[0]);
                frame.Present();
            }
        }
        finally
        {
            target?.Dispose();
            rifleIcon?.Dispose();
            DisposeBuildThumbnails();
            audio?.Dispose();
        }
    }
}
