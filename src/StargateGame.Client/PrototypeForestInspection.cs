using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class GameApp
{
    double? treeForestTourStart;
    int treeForestTourCapture, treeForestWalkCapture;
    static readonly string[] TreeForestViews = ["above", "near", "inside", "far"];

    string ForestInspectionView(double elapsed)
    {
        if (!args.Contains("--tree-forest-tour"))
            return Arguments.Get(args, "--view", "player");
        if (scene?.TreeForest == true && scene.TreePrototypeReady)
            treeForestTourStart ??= elapsed;
        int phase = treeForestTourStart is { } start ? (int)((elapsed - start) / 3) : 0;
        return "tree-forest-" + (phase < TreeForestViews.Length ? TreeForestViews[phase] : "walk");
    }
    double ForestCameraTime(double elapsed) => args.Contains("--tree-forest-tour") && treeForestTourStart is { } start
        ? 20 + Math.Max(0, elapsed - start - 12) : elapsed;

    void CapturePrototypeForestTour(RenderPass pass, double elapsed)
    {
        if (!args.Contains("--tree-forest-tour") || treeForestTourStart is not { } start)
            return;
        double tourTime = elapsed - start;
        if (treeForestTourCapture < 4 && tourTime >= treeForestTourCapture * 3 + 1)
        {
            Capture(pass, Path.Combine(captureDirectory, "forest-" + TreeForestViews[treeForestTourCapture++] + ".png"));
        }
        if (tourTime >= 12 && tourTime < 20 && treeForestWalkCapture < 80 && tourTime >= 12 + treeForestWalkCapture * .1)
            Capture(pass, Path.Combine(captureDirectory, $"forest-walk-{treeForestWalkCapture++:D4}.jpg"), true);
    }
}
