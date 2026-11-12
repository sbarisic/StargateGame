using FishGfx;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class GameApp
{
    double fpsWindowStart = double.NaN;
    int fpsFrames;
    string fpsLabel = "FPS --";

    void DrawFpsCounter(RenderPass pass)
    {
        // Wall-clock frame intervals include rendering and presentation waits,
        // and remain independent of paused/frozen simulation or visual time.
        double now = timer.Elapsed.TotalSeconds;
        if (double.IsNaN(fpsWindowStart))
            fpsWindowStart = now;
        else
        {
            fpsFrames++;
            double elapsed = now - fpsWindowStart;
            if (elapsed >= .5)
            {
                fpsLabel = $"{fpsFrames / elapsed:F0} FPS";
                fpsFrames = 0;
                fpsWindowStart = now;
            }
        }

        float x = Math.Max(4, UiWidth - 108);
        Rect(pass, x, 3, 100, 22, new Color(0, 0, 0, 190));
        Text(pass, x + 8, 6, fpsLabel, 14, Color.White);
    }
}
