using System.Numerics;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class WorldGenChecks
{
    public static async Task Run()
    {
        foreach (string preset in PlanetSettings.Presets)
        {
            var plan = new PlanetPlan(7319, preset);
            PlanetSettings.Validate(plan.Definition);
            var left = plan.BuildChunk(new(-1, 0));
            var right = plan.BuildChunk(new(0, 0));
            Verify.Check(Enumerable.Range(0, 65).All(z => left.Height(64, z) == right.Height(0, z) && left.Normal(64, z) == right.Normal(0, z)), preset + " shared edges and normals");
            Verify.Check(left.Water.Span.ToArray().Select((w, i) => w == int.MinValue || w - left.Heights.Span[i] is >= 0 and <= 12000).All(v => v), preset + " bounded water depth");
            Verify.Check(Enumerable.Range(-10, 21).All(x => Enumerable.Range(-10, 21).Where(z => x * x + z * z <= 100).All(z => plan.Sample((int)plan.Gate.X + x, (int)plan.Gate.Z + z).Height == (int)MathF.Round(plan.Gate.Y * 1000))), preset + " filled flat clearing");
            var bytes = ChunkCodec.Encode(left);
            Verify.Check(ChunkCodec.Encode(ChunkCodec.Decode(bytes)).SequenceEqual(bytes), preset + " canonical codec");
            Verify.Check(ChunkCodec.Encode(plan.BuildChunk(new(-1, 0))).SequenceEqual(bytes), preset + " deterministic regeneration");
            var flow = plan.Downstream.Span.ToArray();
            var heights = plan.DrainageHeight.Span.ToArray();
            Verify.Check(flow.Select((p, i) => p < 0 || heights[p] < heights[i]).All(v => v), preset + " drainage reaches lower outlets");
        }
        var timer = Stopwatch.StartNew();
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string preset in PlanetSettings.Presets)
            foreach (int seed in new[] { 0, 1, -1, 42, 7319, int.MaxValue })
            {
                var plan = new PlanetPlan(seed, preset);
                var centre = ChunkCoordinate.At(plan.Gate.X, plan.Gate.Z);
                var chunk = plan.BuildChunk(centre);
                var neighbour = plan.BuildChunk(new(centre.X + 1, centre.Z));
                fingerprint.AppendData(ChunkCodec.Encode(chunk));
                fingerprint.AppendData(ChunkCodec.Encode(neighbour));
                Verify.Check(Enumerable.Range(0, 65).All(z => chunk.Height(64, z) == neighbour.Height(0, z) &&
                    chunk.Material(64, z) == neighbour.Material(0, z) && chunk.WaterHeight(64, z).Equals(neighbour.WaterHeight(0, z))), $"{preset}/{seed} canonical border data");
                Verify.Check(plan.Definition.Spawns.All(p => plan.Sample((int)p.X, (int)p.Z).Water == int.MinValue && Math.Abs(plan.Sample((int)p.X, (int)p.Z).Height / 1000f - p.Y) < .0011f), $"{preset}/{seed} dry flat spawns");
                Verify.Check(Enumerable.Range(0, 34).All(z => Math.Abs(plan.Sample((int)plan.Gate.X, (int)plan.Gate.Z + z + 1).Height - plan.Sample((int)plan.Gate.X, (int)plan.Gate.Z + z).Height) < 700), $"{preset}/{seed} walkable gate approach");
                var catalogue = new SceneryCatalogue(1, [new("test-tree", "tree", "synthetic", 1, .8f, 1.2f, 45, null), new("test-rock", "rock", "synthetic", 1, .6f, 1.8f, 90, "box")]);
                var before = ChunkCodec.Hash(ChunkCodec.Encode(chunk));
                var instances = SceneryPlacement.Resolve(chunk, catalogue);
                Verify.Check(instances.SequenceEqual(SceneryPlacement.Resolve(chunk, catalogue with
                {
                    Assets = catalogue.Assets.Reverse().ToArray()
                })) && before == ChunkCodec.Hash(ChunkCodec.Encode(chunk)), $"{preset}/{seed} catalogue order preserves candidates and heights");
                Verify.Check(SceneryPlacement.Resolve(chunk, SceneryCatalogue.Empty).Count == 0, "empty production scenery catalogue");
                foreach (int step in new[] { 1, 2, 4, 8 })
                {
                    var mesh = TerrainMesher.Build(chunk, step);
                    Verify.Check(mesh.Positions.Length % 3 == 0 && mesh.Positions.All(v => float.IsFinite(v.Y)) &&
                        Enumerable.Range(0, 65).All(z => mesh.Positions.Contains(chunk.Point(64, z))), $"{preset}/{seed} LOD {step} retains canonical border");
                }
            }
        Console.WriteLine($"TERRAIN FINGERPRINT {Convert.ToHexString(fingerprint.GetHashAndReset())} corpusMs={timer.ElapsedMilliseconds}");
        using var a = new GenerationService(7319, "temperate");
        using var b = new GenerationService(14, "desert");
        var chunks = await Task.WhenAll(a.RequestAsync(new(0, 0)), b.RequestAsync(new(0, 0)), a.RequestAsync(new(-1, 0))).WaitAsync(TimeSpan.FromSeconds(30));
        Verify.Check((await a.Plan).Seed == 7319 && (await b.Plan).Seed == 14, "LayerProcGen isolates concurrent planets");
        Verify.Check(chunks[0].Coordinate == new ChunkCoordinate(0, 0), "LayerProcGen async dependencies");
        a.ReleaseExcept(new HashSet<ChunkCoordinate>());
        Verify.Check(ChunkCodec.Hash(ChunkCodec.Encode(await a.RequestAsync(new(0, 0)))) == ChunkCodec.Hash(ChunkCodec.Encode(chunks[0])), "chunk cache reload");
    }
}
