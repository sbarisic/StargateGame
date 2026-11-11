using System.Numerics;
using System.Text.Json;
using StargateGame.Building;
using StargateGame.Persistence;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class FurnishingChecks
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IncludeFields = true };
    static SettlementLayout Layout(string template, int rotation = 0) => new(1, "village", SettlementTemplates.Get(template).Palette, [], [new(0, 0, template, new(12, 0, 12), rotation)], [], []);
    public static void Run()
    {
        Verify.Check(FurnishingCatalog.Assets.Count >= 14 && FurnishingCatalog.Interiors.Count == 12, "furnishing content covers approved packs and twelve interiors");
        foreach (var a in FurnishingCatalog.Assets)
        {
            var p = BuildingPrefabs.Get(a.Id);
            Verify.Check(p.Role == "furnishing" && !p.Selectable && p.Triangles.Length > 0 && a.Width > 0 && a.Depth > 0, "runtime asset and collision: " + a.Id);
            Verify.Check(p.Triangles.SelectMany(t => t).All(v => v.All(float.IsFinite)), "finite collision: " + a.Id);
        }
        foreach (var interior in FurnishingCatalog.Interiors)
        {
            var roles = interior.Outdoor ? new[] { "courtyard" } : new[] { "home", "dining", "shop", "storage" };
            foreach (string role in roles)
                foreach (int seed in new[] { 0, 7319, -173, 23157 })
                {
                    var plot = new SettlementPlot(7, 0, interior.Template, Vector3.Zero, 0);
                    var room = FurnishingLayouts.Create(plot, seed, role);
                    Verify.Check(room.Pieces.Length is > 0 and <= FurnishingLayouts.MaxPerPlot, $"nonempty bounded room: {interior.Template}/{role}/{seed}");
                    Verify.Check(room.Pieces.SequenceEqual(FurnishingLayouts.Create(plot, seed, role).Pieces), "repeatable furniture transforms");
                    Verify.Check(FurnishingLayouts.Fits(interior, room.Pieces) && FurnishingLayouts.Reachable(interior, room.Pieces), "floor, opening, overlap and one-metre access constraints");
                    if (role == "home")
                        Verify.Check(room.Pieces.Any(p => FurnishingCatalog.Get(p.Asset).Category == "bed"), "home has a bed");
                    if (role is "home" or "dining" or "courtyard")
                        Verify.Check(room.Pieces.Any(p => FurnishingCatalog.Get(p.Asset).Category == "table"), "living/dining area has a table");
                    if (role == "courtyard")
                        Verify.Check(room.Pieces.All(p => FurnishingCatalog.Get(p.Asset).Outdoor), "courtyard uses weather-appropriate furniture");
                    for (int rotation = 0; rotation < 12; rotation++)
                    {
                        var rotated = FurnishingLayouts.Create(plot with
                        {
                            Rotation = rotation
                        }, seed, role);
                        Verify.Check(rotated.Pieces.SequenceEqual(room.Pieces), "plot rotation leaves local layout unchanged");
                        var structure = new StructureRecord(Guid.NewGuid(), new(17, 3, -11), rotation, true);
                        foreach (var item in room.Pieces)
                        {
                            var piece = new BuildingPiece(Guid.NewGuid(), structure.Id, PieceKind.Prefab, default, item.Rotation, 0, Prefab: item.Asset, Offset: item.Position);
                            var expected = Vector3.Transform(item.Position, Matrix4x4.CreateRotationY(-rotation * MathF.PI / 6)) + structure.Origin;
                            Verify.Check(Vector3.Distance(BuildingPrefabs.Transform(structure, piece).Translation, expected) < .0001f, "30-degree placement transform");
                        }
                    }
                }
        }
        foreach (int count in new[] { 1, 3, 4, 5, 6, 7, 8, 96 })
        {
            var plots = Enumerable.Range(0, count).Select(i => new SettlementPlot(i, 0, "settlement-mega-house-" + (i % 4 + 1), new((i % 10) * 20, 0, (i / 10) * 20), i % 12)).ToArray();
            var layout = new SettlementLayout(1, "city", "mega", [], plots, [], []);
            var roles = FurnishingLayouts.AssignRoles(layout, 7319);
            Verify.Check(roles.Values.Count(r => r == "dining") == (count >= 4 ? 1 : 0) && roles.Values.Count(r => r == "shop") == (count >= 6 ? 1 : 0) && roles.Values.Count(r => r == "storage") == (count >= 8 ? 1 : 0), "service building thresholds: " + count);
            var planet = PlanetDefinition.Default with
            {
                Settlements = layout
            };
            var changes = GeneratedBuildings.Create(planet);
            Verify.Check(changes.Pieces.Length <= SettlementSettings.MaxRecords && changes.Pieces.Length < 4096, "largest settlement retains player record headroom");
            var receiver = new WorldStateReceiver();
            foreach (var part in WorldStateWire.Split(changes))
                receiver.Add(part);
            Verify.Check(receiver.State!.Pieces.SequenceEqual(changes.Pieces), "furniture uses existing world-state transport");
            Verify.Check(GeneratedBuildings.Create(planet).Pieces.SequenceEqual(changes.Pieces), "stable generated furnishing identities");
        }
        foreach (var t in SettlementTemplates.All.Where(t => FurnishingCatalog.Interior(t.Id) is null))
        {
            var state = GeneratedBuildings.Create(PlanetDefinition.Default with
            {
                Settlements = Layout(t.Id)
            });
            Verify.Check(state.Pieces.All(p => BuildingPrefabs.Get(p.Prefab).Role != "furnishing"), "excluded template stays unfurnished: " + t.Id);
        }
        // Real collision triangles must leave the space beneath a table open.
        var table = BuildingPrefabs.Get("furnishing-table-small");
        var tri = table.Triangles.Select(t => new BuildTriangle(V(t[0]), V(t[1]), V(t[2]))).ToArray();
        Verify.Check(BuildingGeometry.Ray(new(0, .3f, -1), Vector3.UnitZ, tri) is null, "table collider does not fill the space below its top");
        Verify.Near(BuildingGeometry.Ray(new(0, 1.5f, 0), -Vector3.UnitY, tri) ?? float.NaN, .75f, .02f, "table top has collision at 0.75m");
        Console.WriteLine($"FURNISHING {Verify.Count} CHECKS PASSED");
    }
    static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    sealed class Flat : ITerrainSource
    {
        public float SampleHeight(float x, float z) => 0;
        public bool TryGetChunk(ChunkCoordinate c, out TerrainChunkData data)
        {
            data = new(c, new int[TerrainChunkData.Count], Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray(), new byte[TerrainChunkData.Count * 4], []);
            return true;
        }
    }
    public static void Physics()
    {
        foreach (string template in new[] { "settlement-mega-house-1", "settlement-seahill-house-1", "settlement-desert-compound-2" })
            foreach (int rotation in new[] { 0, 1, 4 })
            {
                var layout = Layout(template, rotation);
                var def = PlanetDefinition.Default with
                {
                    Entities = [],
                    Props = [],
                    Preset = template.Contains("desert") ? "desert" : "temperate",
                    Settlements = layout
                };
                var state = GeneratedBuildings.Create(def);
                state = state with
                {
                    Pieces = state.Pieces.Select(p => BuildingPrefabs.IsDoor(p) ? p with { Open = true } : p).ToArray()
                };
                var structure = state.Structures[0];
                var m = BuildingGeometry.Transform(structure);
                var entry = SettlementTemplates.Get(template).Entrances[0];
                var local = new Vector3(entry[0], .25f, entry[2] - .5f);
                var chunks = new ChunkCoordinate(0, 0).Around(1).ToArray();
                using var world = new PhysicsWorld(def, true, new Flat(), chunks, state);
                world.AddPlayer(new(1, "Interior walker", Vector3.Transform(local, m), default, 0, 0, false, 0));
                for (int i = 0; i < 60; i++)
                    world.Step();
                for (int i = 0; i < 42; i++)
                    world.Step(new Dictionary<int, PlayerInput> { { 1, new(world.Tick + 1, 0, -1, rotation * MathF.PI / 6, 0, false, false) } });
                Matrix4x4.Invert(m, out var inverse);
                var end = Vector3.Transform(world.Player(1).Position, inverse);
                Verify.Check(end.Z > entry[2] + 1.4f && world.Player(1).Grounded, $"furnished doorway remains traversable: {template}/{rotation}");
            }
        var furniture = BuildingPrefabs.Get("furnishing-cupboard");
        var s = new StructureRecord(Guid.NewGuid(), Vector3.Zero, 0, true);
        var p = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.Prefab, default, 0, 0, Prefab: furniture.Id);
        var def2 = PlanetDefinition.Default with
        {
            Entities = [],
            Props = [],
            Preset = "temperate"
        };
        using (var world = new PhysicsWorld(def2, true, new Flat(), new ChunkCoordinate(0, 0).Around(1), new(Guid.NewGuid(), Guid.NewGuid(), 0, [s], [p], [])))
        {
            world.AddPlayer(new(1, "Collision walker", new(0, .2f, -2), default, 0, 0, false, 0));
            for (int i = 0; i < 60; i++)
                world.Step();
            for (int i = 0; i < 90; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(world.Tick + 1, 0, -1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z < -furniture.Depth / 2, "substantial furniture blocks player capsule");
        }
    }
    public static async Task Persistence()
    {
        var plan = new PlanetPlan(23157, "temperate", settlements: new("village", "mega"));
        string folder = Path.Combine(Path.GetTempPath(), "furnishings-" + Guid.NewGuid().ToString("N"));
        SavedLocation saved;
        await using (var db = new WorldRepository(folder))
        {
            saved = await db.ResolveAsync(plan.Definition);
            Verify.Check(saved.Changes.Pieces.Any(p => BuildingPrefabs.Get(p.Prefab).Role == "furnishing"), "furniture saved on initial generation");
            var door = saved.Changes.Pieces.First(BuildingPrefabs.IsDoor);
            var edit = BuildingRules.Edit(saved.Changes, new(Guid.NewGuid(), 0, "door", Target: door.Id), (_, _) => 0, (_, _) => true);
            await db.SaveAsync(edit.State, 0);
            saved = saved with
            {
                Changes = edit.State
            };
        }
        await using (var db = new WorldRepository(folder))
        {
            var again = await db.ResolveAsync(plan.Definition);
            Verify.Check(again.Changes.Pieces.OrderBy(p => p.Id).SequenceEqual(saved.Changes.Pieces.OrderBy(p => p.Id)), "reload retains furniture and edited doors without duplicates");
        }
    }
    public static void ExportFixtures()
    {
        var folder = Path.GetFullPath("artifacts/furnishings");
        Directory.CreateDirectory(folder);
        List<object> rooms = [];
        foreach (var (template, role) in new[] { ("settlement-mega-house-1", "home"), ("settlement-seahill-house-1", "home"), ("settlement-mega-house-4", "dining"), ("settlement-mega-house-3", "shop"), ("settlement-seahill-house-4", "storage"), ("settlement-desert-compound-2", "courtyard") })
        {
            var plot = new SettlementPlot(7, 0, template, Vector3.Zero, 0);
            var interior = FurnishingCatalog.Interior(template)!;
            var room = FurnishingLayouts.Create(plot, 7319, role);
            rooms.Add(new
            {
                template,
                role,
                interior.Floor,
                interior.FloorHeight,
                interior.Outdoor,
                interior.Exclusions,
                pieces = room.Pieces
            });
        }
        File.WriteAllText(Path.Combine(folder, "fixtures.json"), JsonSerializer.Serialize(rooms, Json));
        Console.WriteLine("Furnishing fixtures: " + folder);
    }
}
