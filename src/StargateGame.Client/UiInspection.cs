using FishGfx;
using FishGfx.Graphics;
namespace StargateGame.Client;

sealed partial class GameApp
{
    // Opt-in capture harness. Uses a real local session; never writes the player's address book.
    static readonly (int W, int H)[] UiCaptureSizes = [(960, 600), (1920, 1080), (2560, 1440), (3440, 1440)];
    static readonly string[] UiCaptureScreens = ["hud", "pause", "addresses", "picker", "developer", "connection", "error", "diagnostics", "travel-recovery", "inventory"];
    bool testUiReview;
    bool UiChecking => testUiReview || args.Contains("--ui-check");
    int uiCaptureStage, uiCaptureApplied = -1;
    double uiCaptureStarted;
    readonly List<double> uiCpuTimes = [];
    readonly System.Diagnostics.Stopwatch uiWatch = new();
    double uiRecordingNext;
    int uiRecordingFrame;
    void RecordUi(RenderPass pass, double now)
    {
        if (!args.Contains("--ui-record") || UiPreview || session is null || now - connectedAt < 8 || now < uiRecordingNext || uiRecordingFrame >= 800)
            return;
        Capture(pass, Path.Combine(captureDirectory, $"interaction-{uiRecordingFrame++:D4}.png"), true);
        uiRecordingNext = now + .125;
    }
    string UiCaptureScreen => UiCaptureScreens[uiCaptureStage % UiCaptureScreens.Length];
    bool UiConnectionPreview => UiChecking && uiCaptureApplied >= 0 && UiCaptureScreen is "connection" or "error";
    void InspectUi(double now)
    {
        if (!UiChecking || session is null || (testUiReview ? !TestPlayable : now - connectedAt < 12) || uiCaptureStage >= UiCaptureSizes.Length * UiCaptureScreens.Length)
            return;
        if (uiCaptureApplied == uiCaptureStage)
            return;
        var size = UiCaptureSizes[uiCaptureStage / UiCaptureScreens.Length];
        window.SetClientSize(size.W, size.H);
        if (inventoryOpen) SetInventory(false);
        bookOpen = false;
        planetPanel = false;
        debugPhysics = false;
        buildMode = false;
        SetBuildPalette(false);
        SetPause(false);
        switch (UiCaptureScreen)
        {
            case "inventory":
                SetInventory(true);
                break;
            case "pause":
                SetPause(true);
                break;
            case "addresses":
                SetBook(true);
                break;
            case "picker":
                buildMode = true;
                paletteInspectionHolding = true;
                SetBuildPalette(true);
                break;
            case "developer":
                planetPanel = session.Developer;
                SetPause(true);
                break;
            case "connection":
                status = "Enter a server address to begin.";
                SetPause(true);
                break;
            case "error":
                status = "Unable to connect: The remote expedition server did not answer. Verify the server address and port, then try joining again. Your current expedition remains available.";
                SetPause(true);
                break;
            case "diagnostics":
                debugPhysics = true;
                break;
            case "travel-recovery":
                SetPause(true);
                break;
        }
        uiCaptureApplied = uiCaptureStage;
        uiCaptureStarted = now;
    }
    void CaptureUi(RenderPass pass, double now)
    {
        if (!UiChecking || uiCaptureApplied < 0 || now - uiCaptureStarted < 1.5)
            return;
        var size = UiCaptureSizes[uiCaptureStage / UiCaptureScreens.Length];
        string name = $"{size.W}x{size.H}-{UiCaptureScreen}";
        Capture(pass, Path.Combine(testUiReview ? testRoot : captureDirectory, name + ".png"));
        Console.WriteLine($"UI CAPTURE {name} logical={UiWidth}x{UiHeight} framebuffer={pass.Target.Width}x{pass.Target.Height} thumbnails={buildThumbnails.Count} painterMeshes=1");
        uiCaptureStage++;
        paletteInspectionHolding = false;
        if (uiCaptureStage == UiCaptureSizes.Length * UiCaptureScreens.Length)
        {
            Console.WriteLine($"UI CPU samples={uiCpuTimes.Count} mean_ms={uiCpuTimes.DefaultIfEmpty().Average():F3} p95_ms={P95(uiCpuTimes):F3} hoverStates={uiHover.Count} thumbnails={buildThumbnails.Count} painterMeshes=1");
            if (testUiReview) { testUiReview = false; if (inventoryOpen) SetInventory(false); SetPause(false); FinishTest(data: new { captures = uiCaptureStage }); } else window.IsCloseRequested = true;
        }
    }
    void DrawUiRecoveryFixture(RenderPass pass)
    {
        if (!UiChecking || uiCaptureApplied < 0 || UiCaptureScreen != "travel-recovery")
            return;
        Rect(pass, 0, 0, UiWidth, UiHeight, Color.White);
        Button(pass, Layout.TravelResume, "Resume", key: "ESC");
        Button(pass, Layout.TravelDisconnect, "Disconnect", key: "Q");
    }
}
