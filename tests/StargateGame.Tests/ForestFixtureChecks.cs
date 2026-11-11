using System.Numerics;
using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Client;

static class ForestFixtureChecks
{
    public static void Run()
    {
        var d = GraphicsFixture.Create("gate-pair");
        var chunks = GraphicsFixture.Chunks.Select(c => GraphicsFixture.BuildChunk(d, c)).ToArray();
        var trees = chunks.SelectMany(c => TreeScenery.Resolve(c, d.Preset, d.Fixture)).ToArray();
        Console.WriteLine($"FOREST count={trees.Length} species={string.Join(',', trees.GroupBy(t => t.Model).Select(g => $"{g.Key}:{g.Count()}"))}");
        Verify.Check(trees.Length == 140 && trees.Select(t => t.Id).Distinct().Count() == 140, "forest has 140 unique persistent tree identities");
        Verify.Check(trees.GroupBy(t => t.Model).Count() == 4 && trees.GroupBy(t => t.Model).All(g => g.Count() == 35), "four approved leafy species in equal proportions");
        Verify.Check(trees.All(t => t.Max.X <= -24 || t.Min.X >= 24 || t.Max.Z <= -17 || t.Min.Z >= 23), "complete tree bounds leave the central 48 x 40 m clearing open");
        Verify.Check(trees.All(t => Math.Abs(t.Transform.Translation.X) < 100 && Math.Abs(t.Transform.Translation.Z) < 100), "tree roots stay inside playable bounds");
        Verify.Check(trees.All(a => trees.All(b => a.Id == b.Id || Vector2.Distance(new(a.Transform.M41, a.Transform.M43), new(b.Transform.M41, b.Transform.M43)) >= 7)), "forest keeps at least seven metre trunk spacing");
        Verify.Check(trees.All(t => GraphicsFixture.ForestPathDistance(t.Transform.M41, t.Transform.M43) >= 3.3f), "walking paths have trunk clearance");
        Verify.Check(chunks.All(c => c.Heights.ToArray().All(h => h == 0)), "gate-pair terrain remains flat");
        var reversed = GraphicsFixture.Chunks.Reverse().SelectMany(c => TreeScenery.Resolve(GraphicsFixture.BuildChunk(d, c), d.Preset, d.Fixture)).OrderBy(t => t.Id).ToArray();
        Verify.Check(trees.OrderBy(t => t.Id).SequenceEqual(reversed), "placement and transforms do not depend on chunk arrival order");
        Verify.Check(!GraphicsFixture.Forest(GraphicsFixture.Create("graphics-lab")) && !GraphicsFixture.Forest(GraphicsFixture.Create("gate-pair", "desert")), "forest atmosphere is limited to the temperate gate-pair fixture");
        var tree = trees[0];
        var root = tree.Transform.Translation;
        var chunk = chunks.Single(c => c.Coordinate == ChunkCoordinate.At(root.X, root.Z));
        var neighbours = chunks.ToDictionary(c => c.Coordinate);
        var canopy = new SurfaceCanopy(new(root.X, root.Z), new(7));
        var full = SurfaceEcology.Build(chunk, neighbours, [canopy], [], default, d);
        var cleared = SurfaceEcology.Build(chunk, neighbours, [], [], default, d);
        Verify.Check(full.Sample(root.X, root.Z).X > .5f && cleared.Sample(root.X, root.Z).X == 0, "tree removal removes the canopy input shared by litter and mist");
        Console.WriteLine($"FOREST {Verify.Count} CHECKS PASSED");
    }
}
