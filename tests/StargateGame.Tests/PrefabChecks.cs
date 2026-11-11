using System.Numerics;
using System.Text.Json;
using StargateGame.Building;
using StargateGame.Persistence;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class PrefabChecks
{
    sealed class Flat : ITerrainSource
    {
        public float SampleHeight(float x, float z) => 0;
        public bool TryGetChunk(ChunkCoordinate c, out TerrainChunkData data)
        {
            data = new(c, new int[TerrainChunkData.Count], Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray(), new byte[TerrainChunkData.Count * 4], []);
            return true;
        }
    }
    public static WorldChanges Base()
    {
        WorldChanges state = WorldChanges.Empty;
        BuildingPiece Place(string id, Guid target = default, int socket = 0)
        {
            var result = BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "place", PieceKind.Prefab, target, socket, new(-2, .5f, -2), Prefab: id), (_, _) => 0, (_, _) => true);
            Verify.Check(result.Valid, "prefab placement " + id + ": " + result.Error);
            state = result.State;
            return result.Piece!;
        }
        BuildingPiece At(string id, Vector3 offset)
        {
            var definition = BuildingPrefabs.Get(id);
            foreach (var parent in state.Pieces)
                foreach (var socket in BuildingPrefabs.Sockets(parent, definition).Select((s, i) => (s, i)))
                {
                    var p = BuildingPrefabs.Attach(parent, socket.s);
                    if (Vector3.DistanceSquared(p.Offset, offset) < .0001f)
                        return Place(id, parent.Id, socket.i);
                }
            throw new Exception("Missing socket for " + id + " at " + offset);
        }
        var root = Place("kit-stone-foundation");
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++)
                if (x != 0 || z != 0)
                    At("kit-stone-foundation", new(x * 2, 0, z * 2));
        foreach (var floor in state.Pieces.ToArray())
            Place("kit-floor-wooddark", floor.Id);
        BuildingPiece? doorway = null;
        for (int x = 0; x < 3; x++)
        {
            var front = At(x == 1 ? "kit-wall-unevenbrick-door-flat" : "kit-wall-unevenbrick-window-wide-flat", new(x * 2, 0, -1));
            if (x == 1)
                doorway = front;
            else
                Place("kit-window-wide-flat1", front.Id);
            At("kit-wall-unevenbrick-straight", new(x * 2, 0, 5));
        }
        for (int z = 0; z < 3; z++)
        {
            At("kit-wall-unevenbrick-straight", new(-1, 0, z * 2));
            At("kit-wall-unevenbrick-straight", new(5, 0, z * 2));
        }
        Place("kit-doorframe-flat-brick", doorway!.Id);
        var door = Place("kit-door-1-flat", doorway.Id);
        var roof = At("kit-roof-roundtiles-6x6", new(2, 3, 2));
        Place("kit-roof-front-brick6", roof.Id, 0);
        Place("kit-roof-front-brick6", roof.Id, 1);
        Verify.Check(BuildingRules.Unsupported(state).Length == 0, "six-metre native-grid base has connected support");
        Verify.Check(!BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "remove", Target: root.Id), (_, _) => 0, (_, _) => true).Valid, "supporting prefab foundation cannot be removed");
        var open = BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "door", Target: door.Id), (_, _) => 0, (_, _) => true);
        Verify.Check(open.Valid && open.Piece!.Open, "imported wooden door opens");
        var s = state.Structures.Single();
        var closedBounds = BuildingGeometry.Bounds(BuildingGeometry.Mesh(s, door));
        var openBounds = BuildingGeometry.Bounds(BuildingGeometry.Mesh(s, open.Piece!));
        Verify.Check(openBounds.Max.Z - openBounds.Min.Z > .9f && closedBounds.Max.Z - closedBounds.Min.Z < .2f, "door collision rotates with the leaf");
        var duplicate = BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "place", PieceKind.Prefab, doorway.Id, Prefab: door.Prefab), (_, _) => 0, (_, _) => true);
        Verify.Check(!duplicate.Valid, "occupied prefab socket rejects duplicate");
        return state;
    }
    public static async Task Run()
    {
        Verify.Check(BuildingPrefabs.All.Count == 31 && BuildingPrefabs.All.Select(p => p.Id).Distinct().Count() == 31, "28 retained components and three complete assemblies");
        Verify.Check(BuildingPrefabs.Palette.Any(p => p.Id == "kit-assembled-door") && !BuildingPrefabs.Palette.Any(p => p.Role is "insert" or "frame" or "trim" or "door"), "picker contains complete walls instead of their loose components");
        foreach (int rotation in new[] { 0, 1, 5, 6, 7, 11 })
        {
            var foundation = BuildingRules.Edit(WorldChanges.Empty, new(Guid.NewGuid(), 0, "place", PieceKind.Prefab, Anchor: new(0, .5f, 0), Rotation: rotation, Prefab: "kit-stone-foundation"), (_, _) => 0, (_, _) => true);
            var placed = BuildingRules.Edit(foundation.State, new(Guid.NewGuid(), 1, "place", PieceKind.Prefab, foundation.Piece!.Id, Prefab: "kit-assembled-door"), (_, _) => 0, (_, _) => true);
            Verify.Check(placed.Valid && placed.State.Pieces.Length == 3, "door assembly places wall and moving leaf atomically at rotation " + rotation);
            BuildingRules.ValidateState(placed.State);
            var door = placed.State.Pieces.Single(BuildingPrefabs.IsDoor);
            var opened = BuildingRules.Edit(placed.State, new(Guid.NewGuid(), placed.State.Revision, "door", Target: door.Id), (_, _) => 0, (_, _) => true);
            Verify.Check(opened.Valid && opened.Piece!.Open, "assembled leaf opens independently of its wall");
            var s = placed.State.Structures.Single();
            var transform = BuildingPrefabs.Transform(s, placed.Piece!);
            var eye = Vector3.Transform(new(0, 1, -2), transform);
            var ray = Vector3.TransformNormal(Vector3.UnitZ, transform);
            Verify.Check(BuildingGeometry.Ray(eye, ray, BuildingGeometry.Mesh(placed.State, placed.Piece!), 3) is null, "assembled doorway frame preserves collision opening");
            Verify.Check(BuildingGeometry.Ray(eye, ray, BuildingGeometry.Mesh(placed.State, door), 3) is not null && BuildingGeometry.Ray(eye, ray, BuildingGeometry.Mesh(opened.State, opened.Piece!), 3) is null, "closed/open assembled door changes actual passage collision");
            var removed = BuildingRules.Edit(opened.State, new(Guid.NewGuid(), opened.State.Revision, "remove", Target: door.Id), (_, _) => 0, (_, _) => true);
            Verify.Check(removed.Valid && removed.State.Pieces.Length == 1, "demolishing assembly removes its owned leaf without orphaning it");
        }
        foreach (var def in BuildingPrefabs.All)
        {
            var s = new StructureRecord(Guid.NewGuid(), new(-64, 2, -64), 1);
            var p = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.Prefab, default, 2, 0, Prefab: def.Id);
            var mesh = BuildingGeometry.Mesh(s, p);
            var b = BuildingGeometry.Bounds(mesh);
            Verify.Check(mesh.Length > 0 && float.IsFinite(b.Min.X) && b.Max.Y > b.Min.Y, "finite collision for " + def.Id);
        }
        var state = Base();
        BuildingRules.ValidateState(state);
        var corners = BuildingCorners.Pieces(state);
        Verify.Check(corners.Count == 4 && corners.Select(p => p.Rotation).Distinct().Count() == 4, "existing base derives four correctly oriented corner trims");
        Verify.Check(corners.All(p => BuildingCorners.OwnedMesh(state, p.Parent).Length > 0), "automatic corners share imported collision geometry");
        var manual = state with
        {
            Pieces = state.Pieces.Append(corners[0] with
            {
                Id = Guid.NewGuid()
            }).ToArray()
        };
        Verify.Check(BuildingCorners.Pieces(manual).Count == 3, "explicit corner trim suppresses automatic duplicate");
        Verify.Check(state.Pieces.Length == 37, "derived corners do not alter persisted piece count");
        var wall = state.Pieces.Single(p => p.Id == corners[0].Parent);
        var removedWall = state with
        {
            Pieces = state.Pieces.Where(p => p.Id != wall.Id).ToArray()
        };
        Verify.Check(BuildingCorners.Pieces(removedWall).Count == 3, "removing a joined wall removes its corner trim");
        var rotated = state with
        {
            Structures = state.Structures.Select(s => s with { Rotation = 3 }).ToArray()
        };
        Verify.Check(BuildingCorners.Pieces(rotated).Count == 4, "corner detection remains stable under whole-building rotation");
        var chunks = new ChunkCoordinate(0, 0).Around(1).ToArray();
        using (var world = new PhysicsWorld(PlanetDefinition.Default with
        {
            Entities = [],
            Props = [],
            Preset = "temperate"
        }, true, new Flat(), chunks, state))
        {
            world.AddPlayer(new(1, "Prefab walker", new(0, 2, -1), default, 0, 0, false, 0));
            for (int i = 0; i < 120; i++)
                world.Step();
            Verify.Near(world.Player(1).Position.Y, .51f, .06f, "character stands on imported wooden floor");
            Verify.Check(world.Player(1).Surface == GroundSurface.Wood, "wooden floor supplies wood footsteps");
            for (int i = 0; i < 90; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 1, 0, 1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z > -2.8f, "imported closed door blocks the character");
            world.StageChanges(state with
            {
                Revision = state.Revision + 1,
                Pieces = state.Pieces.Select(p => BuildingPrefabs.IsDoor(p) ? p with { Open = true } : p).ToArray()
            });
            while (!world.PrepareTerrainBatch(chunks))
            {
            }
            world.SetTerrain(chunks);
            world.Epoch++;
            for (int i = 0; i < 90; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 100, 0, 1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z < -3.5f, "imported doorway is passable when the leaf opens: " + world.Player(1).Position);
        }
        var clone = JsonSerializer.Deserialize<WorldChanges>(JsonSerializer.Serialize(state, Protocol.Json), Protocol.Json)!;
        Verify.Check(clone.Pieces.SequenceEqual(state.Pieces), "prefab identity, transforms and parent links survive wire roundtrip");
        var root = state.Pieces[0];
        bool bad = false;
        try
        {
            BuildingRules.ValidateState(state with
            {
                Pieces = state.Pieces.Select(p => p.Id == root.Id ? p with { Parent = state.Pieces[^1].Id } : p).ToArray()
            });
        }
        catch (InvalidDataException)
        {
            bad = true;
        }
        Verify.Check(bad, "cyclic prefab attachments rejected");
        foreach (string unknown in new[] { "not-a-model", "../wall" })
            Verify.Check(!BuildingRules.Edit(WorldChanges.Empty, new(Guid.NewGuid(), 0, "place", PieceKind.Prefab, Prefab: unknown), (_, _) => 0, (_, _) => true).Valid, "unknown prefab ID rejected");
        Verify.Check(!BuildingRules.Edit(WorldChanges.Empty, new(Guid.NewGuid(), 0, "place", PieceKind.Prefab, Anchor: new(0, .5f, 0), Prefab: root.Prefab), (_, _) => 0, (_, _) => false).Valid, "wet prefab foundation rejected");
        Verify.Check(!BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "place", PieceKind.Prefab, root.Id, 0, Prefab: root.Prefab), (_, _) => -5, (_, _) => true).Valid, "attached foundation cannot bridge unsupported terrain");
        string directory = Path.Combine(Path.GetTempPath(), "stargate-prefab-" + Guid.NewGuid().ToString("N"));
        using var generation = new GenerationService(7319, "temperate");
        var plan = await generation.Plan;
        Guid location;
        await using (var db = new WorldRepository(directory))
        {
            var saved = await db.ResolveAsync(plan.Definition);
            location = saved.Id;
            state = state with
            {
                UniverseId = saved.Changes.UniverseId,
                LocationId = location,
                Revision = 1
            };
            await db.SaveAsync(state, 0);
        }
        await using (var db = new WorldRepository(directory))
        {
            var loaded = await db.LoadAsync(location);
            Verify.Check(loaded.Changes.Pieces.OrderBy(p => p.Id).SequenceEqual(state.Pieces.OrderBy(p => p.Id)), "native kit survives repository restart without regenerating old structures");
        }
    }
    public static async Task Demo(string directory)
    {
        using var generation = new GenerationService(7319, "temperate");
        var plan = await generation.Plan;
        var state = Base();
        float best = float.MaxValue;
        Vector3 origin = default;
        for (int z = (int)plan.Gate.Z - 80; z < plan.Gate.Z + 80; z += 4)
            for (int x = (int)plan.Gate.X - 80; x < plan.Gate.X + 80; x += 4)
            {
                float distance = Vector2.Distance(new(x, z), new(plan.Gate.X, plan.Gate.Z));
                if (distance < 40 || distance > 80)
                    continue;
                var samples = Enumerable.Range(-4, 9).SelectMany(iz => Enumerable.Range(-4, 9).Select(ix => plan.Sample(x + ix, z + iz))).ToArray();
                if (samples.Any(p => p.Water != int.MinValue))
                    continue;
                float lo = samples.Min(p => p.Height) / 1000f, hi = samples.Max(p => p.Height) / 1000f;
                float score = hi - lo + distance * .001f;
                if (score < best)
                {
                    best = score;
                    origin = new(x - 2, hi + .25f, z - 2);
                }
            }
        state = state with
        {
            Structures = state.Structures.Select(s => s with { Origin = origin }).ToArray()
        };
        var removed = new List<SceneryRemoval>();
        foreach (var c in ChunkCoordinate.At(origin.X, origin.Z).Around(2))
        {
            var chunk = await generation.RequestAsync(c);
            foreach (var p in chunk.Placements.Where(p => Vector2.Distance(new(p.Position.X, p.Position.Z), new(origin.X + 2, origin.Z + 2)) < 14))
                removed.Add(new(p.Category, p.Id, c));
        }
        await using var db = new WorldRepository(directory);
        var saved = await db.ResolveAsync(plan.Definition);
        await db.SaveAsync(state with
        {
            UniverseId = saved.Changes.UniverseId,
            LocationId = saved.Id,
            Revision = saved.Changes.Revision + 1,
            Removed = removed.ToArray()
        }, saved.Changes.Revision);
        Console.WriteLine($"PREFAB DEMO origin={origin} pieces={state.Pieces.Length}");
    }
}
