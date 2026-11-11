using System.Diagnostics;
using System.Numerics;
using StargateGame.Building;
using StargateGame.Physics;
using StargateGame.Shared;

static class BuildingPhysicsChecks
{
    sealed class FlatTerrain : ITerrainSource
    {
        readonly Dictionary<ChunkCoordinate, TerrainChunkData> chunks = [];
        public float SampleHeight(float x, float z) => 0;
        public bool TryGetChunk(ChunkCoordinate c, out TerrainChunkData data)
        {
            if (!chunks.TryGetValue(c, out data!))
                chunks[c] = data = new(c, new int[TerrainChunkData.Count], Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray(), new byte[TerrainChunkData.Count * 4], []);
            return true;
        }
    }
    public static void Run()
    {
        var terrain = new FlatTerrain();
        var s = new StructureRecord(Guid.NewGuid(), new(5, .5f, 5), 0);
        var foundation = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.SquareFoundation, default, 0, 0, false, -.15f);
        var state = new WorldChanges(Guid.NewGuid(), Guid.NewGuid(), 1, [s], [foundation], []);
        var def = PlanetDefinition.Default with
        {
            Entities = [],
            Props = [],
            Preset = "temperate"
        };
        using (var world = new PhysicsWorld(def, true, terrain, [new(0, 0)], state))
        {
            world.AddPlayer(new(1, "Builder", new(6, 2, 6), default, 0, 0, false, 0));
            for (int i = 0; i < 120; i++)
                world.Step();
            Verify.Near(world.Player(1).Position.Y, .5f, .06f, "Jolt character stands on upward-facing foundation");
            var old = world.Save();
            var empty = state with
            {
                Revision = 2,
                Pieces = [],
                Structures = []
            };
            world.StageChanges(empty);
            while (!world.PrepareTerrainBatch([new(0, 0)]))
            {
            }
            world.SetTerrain([new(0, 0)]);
            world.Epoch++;
            for (int i = 0; i < 120; i++)
                world.Step();
            Verify.Near(world.Player(1).Position.Y, 0, .06f, "demolished foundation no longer collides");
            bool rejected = false;
            try
            {
                world.Restore(old);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            old.Dispose();
            Verify.Check(rejected, "building collision change invalidates native checkpoints");
        }
        var crossingStructure = s with
        {
            Origin = new(62, .5f, 5)
        };
        var crossingState = state with
        {
            Structures = [crossingStructure]
        };
        var required = PhysicsWorld.IncludeBuildingOwners(crossingState, [new(1, 0)]);
        Verify.Check(required.Contains(new(0, 0)) && required.Contains(new(1, 0)), "cross-chunk buildings retain canonical collider owner");
        var doorway = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.Doorway, default, 0, 0);
        var door = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.Door, default, 0, 0);
        state = state with
        {
            Pieces = [foundation, doorway, door]
        };
        using (var world = new PhysicsWorld(def, true, terrain, [new(0, 0)], state))
        {
            world.AddPlayer(new(1, "Walker", new(6.5f, .5f, 6.5f), default, 0, 0, true, 0));
            for (int i = 0; i < 90; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 1, 0, 1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z > 5.2f, "closed door stops a character");
            world.StageChanges(state with
            {
                Revision = 2,
                Pieces = [foundation, doorway, door with { Open = true }]
            });
            while (!world.PrepareTerrainBatch([new(0, 0)]))
            {
            }
            world.SetTerrain([new(0, 0)]);
            world.Epoch++;
            for (int i = 0; i < 60; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 100, 0, 1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z < 4.5f, "open doorway permits character passage");
        }
        var stairs = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.Stairs, default, 0, 0);
        using (var world = new PhysicsWorld(def, true, terrain, [new(0, 0)], state with
        {
            Pieces = [foundation, stairs]
        }))
        {
            world.AddPlayer(new(1, "Climber", new(5.7f, .7f, 5.1f), default, 0, 0, true, 0));
            for (int i = 0; i < 55; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 1, 0, -1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Y > 1.7f, "first stair flight is walkable: " + world.Player(1).Position);
            for (int i = 0; i < 25; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 50, 1, 0, 0, 0, false, false) } });
            for (int i = 0; i < 55; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 100, 0, 1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Y > 3.2f, "return stair flight reaches next storey: " + world.Player(1).Position);
        }
        var structure = new StructureRecord(Guid.NewGuid(), new(-120, .5f, -120), 0);
        List<BuildingPiece> pieces = [];
        for (int z = 0; z < 10; z++)
            for (int x = 0; x < 25; x++)
            {
                var p = new BuildPoint(x * 2, 0, z * 2, 0);
                pieces.Add(new(Guid.NewGuid(), structure.Id, PieceKind.SquareFoundation, p, 0, 0, false, -.15f));
                pieces.Add(new(Guid.NewGuid(), structure.Id, PieceKind.Wall, p, 0, 0));
                pieces.Add(new(Guid.NewGuid(), structure.Id, PieceKind.SquareFloor, p, 0, 1));
                pieces.Add(new(Guid.NewGuid(), structure.Id, PieceKind.Window, p, 0, 1));
            }
        var big = new WorldChanges(Guid.NewGuid(), Guid.NewGuid(), 1, [structure], pieces.ToArray(), []);
        var watch = Stopwatch.StartNew();
        Verify.Check(BuildingRules.Unsupported(big).Length == 0, "1000-piece multi-storey support graph");
        double support = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var chunks = PhysicsWorld.IncludeBuildingOwners(big, [new(-2, -2), new(-1, -2)]);
        using (var world = new PhysicsWorld(def, true, terrain, chunks, big))
        {
            double prepare = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            for (int i = 0; i < 1000; i++)
                world.Step();
            Console.WriteLine($"BUILD BENCHMARK pieces=1000 supportMs={support:F2} collisionPrepareMs={prepare:F2} meanPhysicsMs={watch.Elapsed.TotalMilliseconds / 1000:F3} batches={world.RebuiltBatches}");
        }
    }
}
