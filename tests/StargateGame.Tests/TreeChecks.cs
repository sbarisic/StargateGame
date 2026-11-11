using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class TreeChecks
{
    public static void Run()
    {
        var seen = new HashSet<string>();
        foreach (var preset in PlanetSettings.Presets)
        {
            var plan = new PlanetPlan(7319, preset);
            var chunks = ChunkCoordinate.All.Select(plan.BuildChunk).ToArray();
            var before = chunks.Select(c => ChunkCodec.Hash(ChunkCodec.Encode(c))).ToArray();
            var trees = chunks.SelectMany(c => TreeScenery.Resolve(c, preset)).OrderBy(t => t.Id).ToArray();
            var reverse = chunks.Reverse().Select(c => ChunkCodec.Decode(ChunkCodec.Encode(c))).SelectMany(c => TreeScenery.Resolve(c, preset)).OrderBy(t => t.Id).ToArray();
            Verify.Check(trees.Length > 0 && trees.SequenceEqual(reverse), preset + " tree placement stable across order/cache reload");
            Verify.Check(before.SequenceEqual(chunks.Select(c => ChunkCodec.Hash(ChunkCodec.Encode(c)))), preset + " trees do not change terrain or rock candidates");
            Verify.Check(trees.Select(t => t.Id).Distinct().Count() == trees.Length, preset + " tree IDs unique");
            Verify.Check(trees.All(t => Vector2.Distance(new(t.Transform.M41, t.Transform.M43), new(plan.Gate.X, plan.Gate.Z)) >= 28), preset + " tree roots exclude gate clearing");
            Verify.Check(trees.All(t => Math.Abs(t.Transform.M12) + Math.Abs(t.Transform.M21) + Math.Abs(t.Transform.M23) + Math.Abs(t.Transform.M32) < 1e-6f), preset + " trees remain upright on slopes");
            var chunkByCoordinate = chunks.ToDictionary(c => c.Coordinate);
            Verify.Check(trees.All(t =>
            {
                var p = t.Transform.Translation;
                var c = chunkByCoordinate[ChunkCoordinate.At(p.X, p.Z)];
                float expected = t.Model == "Leave-Tree-01a" ? 1.5f : t.Model is "RealisticTree_01" or "RealisticTree_02" ? .15f : .10f;
                return Math.Abs(c.Sample(p.X, p.Z) - p.Y - expected) < .0001f;
            }), preset + " planting depth stays fixed across tree sizes and terrain slopes");
            Verify.Check(preset != "desert" || trees.All(t => t.Model == "Broadleaf_Desktop"), "desert uses only bare saplings");
            if (preset != "desert")
            {
                Verify.Check(trees.Length > (preset == "temperate" ? 2000 : 700), preset + " supports substantially denser tree populations");
                Verify.Check(chunks.Any(c => TreeScenery.Resolve(c, preset).Count >= 25), preset + " includes dense forest stands");
                var forestCells = chunks.SelectMany(c => Enumerable.Range(0, 8).SelectMany(z => Enumerable.Range(0, 8).Select(x => new Vector2(c.Coordinate.X * 64 + x * 8 + 4, c.Coordinate.Z * 64 + z * 8 + 4)))).ToArray();
                Verify.Check(forestCells.Count(p => plan.ForestCover(p.X, p.Y) < .1f) > forestCells.Length / 5 && forestCells.Count(p => plan.ForestCover(p.X, p.Y) > .9f) > forestCells.Length / 10, preset + " retains both meadows and forest patches");
                float fraction = trees.Count(t => plan.ForestCover(t.Transform.M41, t.Transform.M43) > .9f) / (float)trees.Length;
                float area = forestCells.Count(p => plan.ForestCover(p.X, p.Y) > .9f) / (float)forestCells.Length;
                Verify.Check(fraction > area * 1.4f, preset + " tree density follows broad forest cover");
                Verify.Check(trees.Any(t => t.Min.X < ChunkCoordinate.At(t.Transform.M41, t.Transform.M43).X * 64 || t.Max.X > (ChunkCoordinate.At(t.Transform.M41, t.Transform.M43).X + 1) * 64), preset + " crowns cross chunk boundaries without empty strips");
                Verify.Check(chunks.All(c => c.Placements.Where(p => p.Category == "tree").All(p => !float.IsFinite(c.WaterHeight((int)p.Position.X - c.Coordinate.X * 64, (int)p.Position.Z - c.Coordinate.Z * 64)))), preset + " forest roots exclude wet terrain");
            }
            foreach (var tree in trees)
                seen.Add(tree.Model);
            Console.WriteLine($"TREES {preset}: {trees.Length} [{string.Join(", ", trees.GroupBy(t => t.Model).Select(g => $"{g.Key}={g.Count()}"))}]");
        }
        Verify.Check(TreeScenery.Models.Count() == 10 && TreeScenery.Models.All(seen.Contains), "all six retained and four new tree variants placed");
        Verify.Check(!TreeScenery.RenderModels.Any(n => n.StartsWith("TreePack3D_")), "retired Tree Pack 3D meshes are not loaded for rendering");
        Verify.Check(PlanetSettings.Presets.All(p => TreeScenery.Catalogue(p).Assets.All(a => !a.Model.StartsWith("TreePack3D_"))), "retired Tree Pack 3D is absent from every biome catalogue");
        foreach (var model in new[] { "RealisticTree_01", "RealisticTree_02", "MoreRealisticTree_01", "MoreRealisticTree_02" })
        {
            Verify.Check(TreeScenery.Catalogue("temperate").Assets.Any(a => a.Model == model) &&
                !TreeScenery.Catalogue("icy").Assets.Any(a => a.Model == model) && !TreeScenery.Catalogue("desert").Assets.Any(a => a.Model == model), model + " is restricted to temperate worlds");
            Verify.Check(TreeScenery.RenderModel(model, 0) == model && TreeScenery.RenderModel(model, 40) == model &&
                TreeScenery.RenderModel(model, 40.01f) == model + "_Lod1" && TreeScenery.RenderModel(model, 90) == model + "_Lod1" &&
                TreeScenery.RenderModel(model, 90.01f) == model + "_Lod2" && TreeScenery.RenderModel(model, 180) == model + "_Lod2" &&
                TreeScenery.RenderModel(model, 180.01f) == model + "_Lod3" && TreeScenery.RenderModel(model, float.MaxValue) == model + "_Lod3", model + " uses all four detail levels at the configured boundaries");
            using var proxy = typeof(PhysicsWorld).Assembly.GetManifestResourceStream("StargateGame.Physics.Collision." + model + ".sgc");
            Verify.Check(proxy is { Length: > 12 }, model + " wood collision packaged for authority and prediction");
            using var proxyReader = new BinaryReader(proxy!);
            Verify.Check(proxyReader.ReadUInt32() == 0x31434753, model + " collision format");
            int vertices = proxyReader.ReadInt32(), triangles = proxyReader.ReadInt32();
            Verify.Check(vertices > 0 && triangles is >= 1100 and <= 1200 && proxy!.Length == 12L + 12L * (vertices + triangles), model + " uses compact wood-only collision");
        }
        Verify.Check(TreeScenery.RenderModel("Leave-Tree-01a", 40) == "Leave-Tree-01a" && TreeScenery.RenderModel("Leave-Tree-01a", 100).EndsWith("Lod1") && TreeScenery.RenderModel("Leave-Tree-01a", 200).EndsWith("Lod2"), "tree distance LOD selection");
        var coordinate = new ChunkCoordinate(0, 0);
        TerrainChunkData Flat(int[] water) => new(coordinate, new int[TerrainChunkData.Count], water, new byte[TerrainChunkData.Count * 4], [new(100, "tree", new(32, 0, 32), 0, 1)]);
        var dry = Flat(Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray());
        Verify.Check(TreeScenery.Resolve(Flat(Enumerable.Repeat(100, TerrainChunkData.Count).ToArray()), "icy").Count == 0, "tree roots exclude water");
        var treeFixture = TreeScenery.Resolve(dry, "icy").Single();
        // Recover the centre of the main spruce stem at chest height, independent of canopy pivot.
        using var stream = typeof(PhysicsWorld).Assembly.GetManifestResourceStream("StargateGame.Physics.Collision." + treeFixture.Model + ".sgc")!;
        using var reader = new BinaryReader(stream);
        reader.ReadUInt32();
        int count = reader.ReadInt32();
        reader.ReadInt32();
        List<Vector3> band = [];
        for (int i = 0; i < count; i++)
        {
            var p = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (p.Y > .8f && p.Y < 1.5f)
                band.Add(p);
        }
        var centre = Vector3.Transform((band.Aggregate(Vector3.Min) + band.Aggregate(Vector3.Max)) / 2, treeFixture.Transform);
        centre.Y = 0;
        var definition = new PlanetPlan(7319, "icy").Definition with
        {
            Entities = [],
            Props = []
        };
        var store = new TerrainStore(definition.SettingsHash);
        store.Put(dry, false);
        using var world = new PhysicsWorld(definition, true, store, [coordinate]);
        world.AddPlayer(new(1, "Trunk", centre + Vector3.UnitZ * 3, default, 0, 0, true, 0));
        float nearest = float.MaxValue;
        for (int i = 0; i < 100; i++)
        {
            world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 1, 0, 1, 0, 0, false, false) } });
            var p = world.Player(1).Position;
            nearest = Math.Min(nearest, Vector2.Distance(new(p.X, p.Z), new(centre.X, centre.Z)));
        }
        Verify.Check(nearest > .3f && Math.Abs(world.Player(1).Position.X - centre.X) > .35f && world.Player(1).Grounded, "spruce trunk deflects walking without entering its interior");
        using var checkpoint = world.Save();
        for (int i = 0; i < 10; i++)
            world.Step();
        var expected = world.Snapshot();
        world.Restore(checkpoint);
        for (int i = 0; i < 10; i++)
            world.Step();
        Verify.Check(world.Snapshot().Players.SequenceEqual(expected.Players), "tree contact checkpoint replay");
        var baselineHash = ChunkCodec.Hash(ChunkCodec.Encode(dry));
        var removed = new WorldChanges(Guid.NewGuid(), Guid.NewGuid(), 1, [], [], [new("tree", treeFixture.Id, coordinate)]);
        world.StageChanges(removed);
        while (!world.PrepareTerrainBatch([coordinate]))
        {
        }
        world.CancelChanges();
        Verify.Check(world.SceneryMesh("tree", treeFixture.Id, coordinate).Any(), "cancelled tree clearing retains active collision");
        world.StageChanges(removed);
        while (!world.PrepareTerrainBatch([coordinate]))
        {
        }
        world.SetTerrain([coordinate]);
        world.Epoch++;
        Verify.Check(!world.SceneryMesh("tree", treeFixture.Id, coordinate).Any() && ChunkCodec.Hash(ChunkCodec.Encode(dry)) == baselineHash, "scenery tombstone removes collision without changing canonical terrain");
        Verify.Check(TreeScenery.Resolve(dry, "icy").Single() == treeFixture, "removing scenery does not reroll neighbouring placement");
        world.RemovePlayer(1);
        world.AddPlayer(new(1, "Cleared", centre + Vector3.UnitZ * 3, default, 0, 0, true, 0));
        for (int i = 0; i < 100; i++)
            world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 1, 0, 1, 0, 0, false, false) } });
        Verify.Check(Math.Abs(world.Player(1).Position.X - centre.X) < .1f && world.Player(1).Position.Z < centre.Z - 2, "character walks through cleared trunk position");
        world.SetTerrain([]);
        bool invalid = false;
        try
        {
            world.Restore(checkpoint);
        }
        catch (InvalidOperationException)
        {
            invalid = true;
        }
        Verify.Check(invalid, "tree chunk unload invalidates checkpoints");
    }
}
