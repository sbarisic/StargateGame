using System.Numerics;
using System.Diagnostics;
using StargateGame.Client;
using StargateGame.Shared;

static class LandscapeVisualChecks
{
    public static void Run()
    {
        float[] planted = Enumerable.Repeat(1f, 8).ToArray(), bare = new float[8];
        foreach (string preset in new[] { "temperate", "desert", "icy" })
        {
            float Cover(float[] layers) => TerrainGrass.Coverage(preset, new(42, -17), Vector3.Normalize(new Vector3(2, 1, 0)),
                new(0, 1, .3f, 0), new(.4f, .3f, 10, 1), new(-1, 2), 42, layers);
            Verify.Check(Cover(planted) > .99f && Cover(bare) == 0,
                $"grass follows selected images even on steep/beach terrain in {preset}");
        }
        Verify.Check(GroundCover.TerrainCoverage(Vector3.UnitY, new(.1f, 0, 0, 0)) == 0, "physical road reservation remains clear");
        var tile = new GrassTile(-144, 114);
        var random = new Random(41);
        var tufts = Enumerable.Range(0, 350).Select(i => new GrassTuft(
            new(tile.X * 8 + (float)random.NextDouble() * 8, 25, tile.Z * 8 + (float)random.NextDouble() * 8),
            .2f, 0, .5f, .8f, .2f + (float)random.NextDouble() * .25f)).ToArray();
        _ = HybridGrass.FootprintCoverage(tile, tufts, default);
        var timer = Stopwatch.StartNew();
        var actual = HybridGrass.FootprintCoverage(tile, tufts, default);
        double optimized = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        float error = 0;
        for (int z = 0; z < 33; z++) for (int x = 0; x < 33; x++)
        {
            var p = new Vector2(tile.X * 8 + x * .25f, tile.Z * 8 + z * .25f);
            float expected = tufts.Max(t => Math.Clamp((t.Footprint + .24f - Vector2.Distance(p, new(t.Root.X, t.Root.Z))) * 5, 0, 1));
            error = Math.Max(error, Math.Abs(actual[z * 33 + x] - expected));
        }
        Verify.Check(error < .00001f, "rasterized grass coverage matches brute force, including negative world coordinates");
        Console.WriteLine($"GRASS FIELD single-tile CPU optimized={optimized:F3}ms reference={timer.Elapsed.TotalMilliseconds:F3}ms");
        Verify.Check(actual.SequenceEqual(HybridGrass.FootprintCoverage(tile, tufts.Reverse().ToArray(), default)), "grass coverage is independent of candidate ordering");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        bool cancelled = false;
        try { HybridGrass.FootprintCoverage(tile, tufts, stop.Token); } catch (OperationCanceledException) { cancelled = true; }
        Verify.Check(cancelled, "grass field cancellation is retained");
        var chunk = new TerrainChunkData(new(-18, 14), Enumerable.Repeat(25000, TerrainChunkData.Count).ToArray(),
            Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray(), new byte[TerrainChunkData.Count * 4], []);
        var planet = PlanetDefinition.Default with { Preset = "temperate", Entities = [], Spawns = [] };
        var settings = new GroundCoverSettings();
        var mesh = GroundCover.Instances(tile, chunk, planet, settings, [], default);
        var volume = GroundCover.Instances(tile, chunk, planet, settings, [], default, volumeOnly: true);
        Verify.Check(volume.Count > 0 && volume.Count == mesh.Count && volume.Records.Length == 0 && volume.Surface!.SequenceEqual(mesh.Surface!), "volume-only preparation drops unused records without changing roots or coverage");
        foreach (int quality in new[] { 0, 1, 2 })
        {
            var hybrid = settings.EffectiveBudget(quality);
            var fallback = (settings with { Hybrid = false }).EffectiveBudget(quality);
            int terrainRange = quality == 1 ? 256 : quality == 2 ? 512 : 384;
            Verify.Check(hybrid.Range > fallback.Range && hybrid.Range + 64 <= terrainRange, "extended grass fits streamed terrain and its exclusion neighbours");
        }
        Console.WriteLine($"LANDSCAPE VISUAL {Verify.Count} CHECKS PASSED");
    }
}
