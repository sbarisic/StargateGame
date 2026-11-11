using FishGfx.Graphics;
namespace StargateGame.Client;

sealed partial class GameApp
{
    int forestMovieFrame;
    double? prototypeMovieStart;
    void CaptureForestMovie(RenderPass pass, double elapsed)
    {
        if (args.Contains("--tree-forest"))
        {
            if (scene?.TreePrototypeReady != true)
                return;
            prototypeMovieStart ??= elapsed;
            elapsed = elapsed - prototypeMovieStart.Value + 12;
        }
        if (!args.Contains("--forest-record") || elapsed < 12 || elapsed >= 32 || forestMovieFrame >= 200)
            return;
        if (elapsed < 12 + forestMovieFrame * .1)
            return;
        Capture(pass, Path.Combine(captureDirectory, $"forest-walk-{forestMovieFrame++:D4}.jpg"), true);
    }
}
