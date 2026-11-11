using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Building;
using StargateGame.Physics;
using StargateGame.Persistence;
using StargateGame.Server;
using StargateGame.Client;
using System.Net;

static class SettlementChecks
{
    public static void InspectionSeeds()
    {
        var results = new List<object>();
        foreach (var (preset, type, style) in new[] { ("temperate", "city", "mega"), ("temperate", "city", "modern"), ("desert", "village", "desert"), ("icy", "village", "mega"), ("temperate", "village", "seahill"), ("desert", "ruins", "desert") })
        {
            bool found = false;
            for (int i = 0; i < 32; i++)
            {
                int seed = 7319 + i * 7919;
                var plan = new PlanetPlan(seed, preset, settlements: new(type, style));
                var l = plan.Settlements.Layout;
                if (!l.Sites.Any(s => s.Kind == type))
                    continue;
                results.Add(new
                {
                    preset,
                    type,
                    style,
                    seed,
                    plots = l.Plots.Length
                });
                Console.WriteLine($"INSPECT {preset} {type} {style} {seed}: {l.Plots.Length}");
                found = true;
                break;
            }
            Verify.Check(found, "natural-terrain inspection site exists: " + preset + " " + type + " " + style);
        }
        File.WriteAllText("artifacts/settlement-inspection.json", JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }
    sealed class Flat : ITerrainSource
    {
        public float SampleHeight(float x, float z) => 0;
        public bool TryGetChunk(ChunkCoordinate c, out TerrainChunkData data)
        {
            data = new(c, new int[TerrainChunkData.Count], Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray(), new byte[TerrainChunkData.Count * 4], []);
            return true;
        }
    }
    public static async Task Integration()
    {
        using (var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            var connect = new System.Net.Sockets.TcpClient();
            var connected = connect.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var remote = new Peer(await listener.AcceptTcpClientAsync());
            await connected;
            using var local = new Peer(connect);
            await Task.Delay(11000);
            Verify.Check(!local.Closed && !remote.Closed && !local.Incoming.TryRead(out _) && !remote.Incoming.TryRead(out _), "idle resource preparation retains connection without gameplay heartbeat events");
            local.Send(new()
            {
                Kind = "settlement-check"
            });
            using var timeout = new CancellationTokenSource(3000);
            Verify.Check((await remote.Incoming.ReadAsync(timeout.Token)).Kind == "settlement-check", "gameplay resumes after transport idle period");
        }
        foreach (var t in SettlementTemplates.All.Where(t => t.Doors.Length > 0))
        {
            var layout = new SettlementLayout(1, "village", t.Palette, [], [new(0, 0, t.Id, Vector3.Zero, 0)], [], []);
            var def = PlanetDefinition.Default with
            {
                Entities = [],
                Props = [],
                Preset = "temperate",
                Settlements = layout
            };
            var state = GeneratedBuildings.Create(def);
            var chunks = new ChunkCoordinate(0, 0).Around(1).ToArray();
            using var world = new PhysicsWorld(def, true, new Flat(), chunks, state);
            var entry = t.Entrances[0];
            var start = new Vector3(entry[0], .3f, entry[2] + 2.5f);
            world.AddPlayer(new(1, "Template walker", start, default, 0, 0, false, 0));
            for (int i = 0; i < 90; i++)
                world.Step();
            Verify.Near(world.Player(1).Position.Y, 0, .06f, t.Id + " floor supports capsule");
            for (int i = 0; i < 75; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(world.Tick + 1, 0, 1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z > entry[2], t.Id + " closed door blocks capsule");
            state = state with
            {
                Revision = 1,
                Pieces = state.Pieces.Select(p => BuildingPrefabs.IsDoor(p) ? p with { Open = true } : p).ToArray()
            };
            world.StageChanges(state);
            while (!world.PrepareTerrainBatch(chunks))
            {
            }
            world.SetTerrain(chunks);
            for (int i = 0; i < 90; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(world.Tick + 1, 0, 1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z < entry[2] - 1, t.Id + " open doorway passes capsule: " + world.Player(1).Position);
            Verify.Check(BuildingGeometry.Ray(new(0, 1, 0), Vector3.UnitY, state.Pieces.Where(p => !BuildingPrefabs.IsDoor(p)).SelectMany(p => BuildingGeometry.Mesh(state, p)).ToArray()) is < 3, t.Id + " ceiling/shelter coverage");
        }
        foreach (var t in SettlementTemplates.All.Where(t => t.Palette == "desert" && t.Kind == "building"))
        {
            var layout = new SettlementLayout(1, "village", t.Palette, [], [new(0, 0, t.Id, Vector3.Zero, 0)], [], []);
            var def = PlanetDefinition.Default with
            {
                Entities = [],
                Props = [],
                Preset = "desert",
                Settlements = layout
            };
            var state = GeneratedBuildings.Create(def);
            using var world = new PhysicsWorld(def, true, new Flat(), new ChunkCoordinate(0, 0).Around(1), state);
            var entry = t.Entrances[0];
            world.AddPlayer(new(1, "Courtyard walker", new(entry[0], .2f, entry[2] - 2), default, 0, 0, false, 0));
            for (int i = 0; i < 60; i++)
                world.Step();
            for (int i = 0; i < 120; i++)
                world.Step(new Dictionary<int, PlayerInput> { { 1, new(world.Tick + 1, 0, -1, 0, 0, false, false) } });
            Verify.Check(world.Player(1).Position.Z > entry[2] + 3 && world.Player(1).Grounded, t.Id + " open courtyard entrance is walkable");
        }
        var plan = new PlanetPlan(23157, "temperate", settlements: new("village", "seahill"));
        string folder = Path.Combine(Path.GetTempPath(), "settlement-persistence-" + Guid.NewGuid().ToString("N"));
        SavedLocation saved;
        await using (var db = new WorldRepository(folder))
        {
            saved = await db.ResolveAsync(plan.Definition);
            Verify.Check(saved.Changes.Pieces.Length > 0, "settlement atomically materialized");
            var door = saved.Changes.Pieces.First(BuildingPrefabs.IsDoor);
            var edited = BuildingRules.Edit(saved.Changes, new(Guid.NewGuid(), 0, "door", Target: door.Id), (_, _) => 0, (_, _) => true);
            await db.SaveAsync(edited.State, 0);
            saved = saved with
            {
                Changes = edited.State
            };
        }
        await using (var db = new WorldRepository(folder))
        {
            var after = await db.ResolveAsync(plan.Definition);
            Verify.Check(after.Id == saved.Id && after.Changes.Pieces.OrderBy(p => p.Id).SequenceEqual(saved.Changes.Pieces.OrderBy(p => p.Id)) && after.Changes.Structures.All(s => s.Generated), "restart retains generated IDs, ownership and open doors without duplication");
        }
        foreach (var palette in SettlementSettings.Styles.Where(s => s != "auto"))
        {
            var layout = new SettlementPlan(7319, "temperate", new(450, 0, 450), (_, _) => (0, int.MinValue), new("city", palette)).Layout;
            Verify.Check(layout.Plots.Length >= 30, "flat city reaches requested building count: " + palette + " " + layout.Plots.Length);
            var wire = JsonSerializer.SerializeToUtf8Bytes(new Message { Kind = "welcome", Planet = plan.Definition with { Settlements = layout } }, Protocol.Json);
            Verify.Check(wire.Length < 65536, "largest settlement definition fits bounded welcome: " + wire.Length);
        }
    }
    public static async Task Network(bool delayed)
    {
        var settings = new SettlementSettings("village", "mega");
        var plan = new PlanetPlan(23157, "temperate", settlements: settings);
        var plot = plan.Settlements.Layout.Plots.First(p => SettlementTemplates.Get(p.Template).Doors.Length > 0);
        var template = SettlementTemplates.Get(plot.Template);
        var e = template.Entrances[0];
        var matrix = Matrix4x4.CreateRotationY(-plot.Rotation * MathF.PI / 6) * Matrix4x4.CreateTranslation(plot.Position);
        var spawn = Vector3.Transform(new(e[0], .1f, e[2] - 1.2f), matrix);
        var def = plan.Definition with
        {
            Spawns = Enumerable.Range(0, 8).Select(i => spawn + new Vector3((i % 2) * 1.2f, 0, -(i / 2) * .5f)).ToArray()
        };
        string directory = Path.Combine(Path.GetTempPath(), "settlement-network-" + Guid.NewGuid().ToString("N"));
        await using (var repository = new WorldRepository(directory))
            await repository.ResolveAsync(def);
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, plan.Seed, plan.Preset, true, directory, settlements: settings);
        server.Start();
        await using var proxy = delayed ? new DelayProxy(server.Port, 0) : null;
        int port = proxy?.Port ?? server.Port;
        using var first = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", port, "Settlement visitor", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        var joining = GameConnection.ConnectAsync("127.0.0.1", port, "Observer", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        while (!joining.IsCompleted)
        {
            first.Poll();
            first.Step(0, 0, 0, 0, false, false);
            if (first.Failure is not null)
                throw new Exception(first.Failure);
            await Task.Delay(10);
        }
        using var second = new ClientSession(await joining);
        async Task Pump(int ms)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < ms)
            {
                first.Poll();
                second.Poll();
                first.Step(0, 0, 0, 0, false, false);
                second.Step(0, 0, 0, 0, false, false);
                if (first.Failure is not null || second.Failure is not null)
                    throw new Exception(first.Failure ?? second.Failure);
                await Task.Delay(10);
            }
        }
        await Pump(1500);
        var readiness = Stopwatch.StartNew();
        while ((first.TransitionPending || second.TransitionPending) && readiness.Elapsed.TotalSeconds < 45)
            await Pump(100);
        var firstFurniture = first.Changes.Pieces.Where(p => p.Kind == PieceKind.Prefab && BuildingPrefabs.Get(p.Prefab).Role == "furnishing").OrderBy(p => p.Id).ToArray();
        var secondFurniture = second.Changes.Pieces.Where(p => p.Kind == PieceKind.Prefab && BuildingPrefabs.Get(p.Prefab).Role == "furnishing").OrderBy(p => p.Id).ToArray();
        Verify.Check(firstFurniture.Length > 0 && firstFurniture.SequenceEqual(secondFurniture), "both clients receive identical furniture identities and transforms under latency/jitter");
        var structure = GeneratedBuildings.Identity(first.World.Definition.LocationId, $"structure:{plot.Id}");
        var door = first.Changes.Pieces.First(p => p.StructureId == structure && BuildingPrefabs.IsDoor(p));
        for (int attempt = 0; attempt < 12; attempt++)
        {
            first.Build(new(Guid.NewGuid(), first.Changes.Revision, "door", Target: door.Id));
            for (int i = 0; i < 150 && first.BuildPending; i++)
                await Pump(100);
            if (first.LastBuildResult?.Success == true || first.LastBuildResult?.Reason != "World update in progress; try again.")
                break;
            await Pump(500);
        }
        await Pump(500);
        Verify.Check(first.LastBuildResult?.Success == true, "two-client generated door accepted: " + first.LastBuildResult?.Reason);
        Verify.Check(second.Changes.Pieces.Single(p => p.Id == door.Id).Open, "generated door opens on remote client under latency/jitter");
        first.Build(new(Guid.NewGuid(), first.Changes.Revision, "remove", Target: door.Id));
        for (int i = 0; i < 150 && first.BuildPending; i++)
            await Pump(100);
        Verify.Check(first.LastBuildResult?.Success == false && second.Changes.Pieces.Any(p => p.Id == door.Id), "server rejects generated door demolition");
        Guid location = first.World.Definition.LocationId;
        await server.DisposeAsync();
        await using var db = new WorldRepository(directory);
        var saved = await db.LoadAsync(location);
        Verify.Check(saved.Changes.Pieces.Where(p => p.Kind == PieceKind.Prefab && BuildingPrefabs.Get(p.Prefab).Role == "furnishing").OrderBy(p => p.Id).SequenceEqual(firstFurniture), "server persistence matches client furniture records");
        Verify.Check(saved.Changes.Pieces.Single(p => p.Id == door.Id).Open, "network door state persists after server shutdown");
    }
    public static void Run(bool corpus)
    {
        var rows = new List<object>();
        foreach (var preset in PlanetSettings.Presets)
            for (int i = 0; i < (corpus ? 32 : 2); i++)
            {
                int seed = 7319 + i * 7919;
                var watch = Stopwatch.StartNew();
                var plan = new PlanetPlan(seed, preset);
                var l = plan.Settlements.Layout;
                Console.WriteLine($"SETTLEMENT {preset} {seed} {l.Outcome}/{l.Palette}: {l.Plots.Length} plots, {l.Roads.Length} roads in {watch.ElapsedMilliseconds} ms");
                rows.Add(new
                {
                    preset,
                    seed,
                    l.Outcome,
                    l.Palette,
                    sites = l.Sites,
                    plots = l.Plots.Length,
                    rejections = l.Rejections,
                    milliseconds = watch.ElapsedMilliseconds
                });
                foreach (var p in l.Plots)
                {
                    var half = SettlementPlan.Half(p);
                    if (Vector2.Distance(new(p.Position.X, p.Position.Z), new(plan.Gate.X, plan.Gate.Z)) - half.Length() < 40)
                        throw new Exception("Gate reservation violated");
                    for (float z = -half.Y; z <= half.Y; z += 1)
                        for (float x = -half.X; x <= half.X; x += 1)
                        {
                            var sample = plan.Sample((int)MathF.Round(p.Position.X + x), (int)MathF.Round(p.Position.Z + z));
                            if (sample.Water != int.MinValue || Math.Abs(sample.Height / 1000f - p.Position.Y) > .08f)
                                throw new Exception($"Non-flat or wet plot {preset}/{seed}/{p.Template} at {x},{z}: {sample.Height / 1000f} != {p.Position.Y}");
                        }
                }
                foreach (var a in l.Plots)
                    foreach (var b in l.Plots.Where(b => b.Id > a.Id))
                    {
                        var extent = SettlementPlan.Half(a) + SettlementPlan.Half(b);
                        Verify.Check(Math.Abs(a.Position.X - b.Position.X) >= extent.X || Math.Abs(a.Position.Z - b.Position.Z) >= extent.Y, "generated footprints do not overlap");
                    }
                if (l.Roads.Length > 0)
                {
                    Verify.Check(l.Roads.All(r => r.Width is 2 or 4 or 8), "narrow lane and street widths");
                    var road = l.Roads[0];
                    var coordinate = ChunkCoordinate.At(road.A.X, road.A.Z);
                    var chunk = plan.BuildChunk(coordinate);
                    var decoded = ChunkCodec.Decode(ChunkCodec.Encode(chunk));
                    Verify.Check(chunk.Roads.Span.SequenceEqual(decoded.Roads.Span), "road coverage survives canonical transfer");
                    var neighbour = coordinate.Around(1).First(c => c != coordinate);
                    _ = plan.BuildChunk(neighbour);
                    Verify.Check(ChunkCodec.Encode(chunk).SequenceEqual(ChunkCodec.Encode(plan.BuildChunk(coordinate))), "chunk arrival order preserves graded terrain and roads");
                }
                if (l.Plots.FirstOrDefault() is { } firstPlot)
                {
                    var c = ChunkCoordinate.At(firstPlot.Position.X, firstPlot.Position.Z);
                    var chunk = plan.BuildChunk(c);
                    var detail = TerrainMesher.Build(chunk, 8).Positions.ToHashSet();
                    for (int z = 0; z < 64; z++)
                        for (int x = 0; x < 64; x++)
                            if (chunk.GradedPlot(x, z))
                                Verify.Check(detail.Contains(chunk.Point(x, z)), "distant terrain retains canonical graded plot samples");
                }
                var definition = plan.Definition with
                {
                    LocationId = Guid.Parse("c9115646-1156-4b1a-a616-fef39831857c")
                };
                var state = GeneratedBuildings.Create(definition);
                Verify.Check(state.Pieces.Length <= SettlementSettings.MaxRecords && state.Structures.All(s => s.Generated), "bounded generated ownership " + seed + " " + preset);
                var root = state.Pieces.FirstOrDefault();
                if (root is not null)
                {
                    Verify.Check(!BuildingRules.Edit(state, new(Guid.NewGuid(), 0, "remove", Target: root.Id), (_, _) => 0, (_, _) => true).Valid, "generated demolition rejected");
                    Verify.Check(!BuildingRules.Edit(state, new(Guid.NewGuid(), 0, "place", PieceKind.Prefab, root.Id, Prefab: "kit-floor-wooddark"), (_, _) => 0, (_, _) => true).Valid, "generated attachment rejected");
                }
                foreach (var door in state.Pieces.Where(BuildingPrefabs.IsDoor))
                {
                    Verify.Check(BuildingRules.Edit(state, new(Guid.NewGuid(), 0, "door", Target: door.Id), (_, _) => 0, (_, _) => true).Valid, "generated door usable");
                }
                if (i == 0)
                {
                    var repeat = new PlanetPlan(seed, preset);
                    Verify.Check(JsonSerializer.Serialize(l, Protocol.Json) == JsonSerializer.Serialize(repeat.Settlements.Layout, Protocol.Json), "repeatable settlement " + preset);
                }
            }
        Directory.CreateDirectory("artifacts");
        File.WriteAllText("artifacts/settlement-corpus.json", JsonSerializer.Serialize(rows, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
    }
}
