using System.Diagnostics;
using System.Numerics;
using System.Net;
using StargateGame.Building;
using StargateGame.Persistence;
using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Physics;
using StargateGame.Server;
using StargateGame.Client;

static class BuildingChecks
{
    static void CheckWallCorners()
    {
        foreach (var shape in new[] { PieceKind.SquareFoundation, PieceKind.TriangleFoundation })
            foreach (int rotation in new[] { 0, 1, 5, 9 })
            {
                var initial = BuildingRules.Edit(WorldChanges.Empty, new(Guid.NewGuid(), 0, "place", shape, Anchor: new(-30, .5f, -30), Rotation: rotation), (_, _) => 0, (_, _) => true);
                var state = initial.State;
                var foundation = initial.Piece!;
                var walls = new List<BuildingPiece>();
                for (int edge = 0; edge < BuildingGeometry.Corners(foundation).Length; edge++)
                {
                    var kind = new[] { PieceKind.Wall, PieceKind.Window, PieceKind.Doorway }[edge % 3];
                    var placed = BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "place", kind, foundation.Id, edge), (_, _) => 0, (_, _) => true);
                    Verify.Check(placed.Valid, $"{shape} rotated {rotation}: overlapping corner {edge} remains legal: {placed.Error}");
                    if (!placed.Valid)
                        throw new Exception(placed.Error);
                    state = placed.State;
                    walls.Add(placed.Piece!);
                }
                var s = state.Structures.Single();
                Verify.Check(BuildingRules.Overlaps(BuildingGeometry.Mesh(s, walls[0]), BuildingGeometry.Mesh(s, walls[^1])), "adjacent square/triangle wall shells overlap");
                var duplicate = BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "place", PieceKind.Wall, foundation.Id, 0), (_, _) => 0, (_, _) => true);
                Verify.Check(!duplicate.Valid, "overlapping corner permission does not permit duplicate walls");
            }
        var structure = new StructureRecord(Guid.NewGuid(), Vector3.Zero, 0);
        foreach (var kind in new[] { PieceKind.Wall, PieceKind.Window, PieceKind.Doorway })
        {
            var mesh = BuildingGeometry.Mesh(structure, new(Guid.NewGuid(), structure.Id, kind, default, 0, 0));
            var bounds = BuildingGeometry.Bounds(mesh);
            Verify.Near(bounds.Min.X, -.18f, .0001f, "wall start extends beyond socket");
            Verify.Near(bounds.Max.X, 3.18f, .0001f, "wall end extends beyond socket");
            Verify.Check(BuildingGeometry.Ray(new(-.08f, 1, -1), Vector3.UnitZ, mesh) is < 1, "overlapping end cap closes the outside corner");
        }
    }
    public static async Task Run(bool network = true)
    {
        CheckWallCorners();
        BuildingPhysicsChecks.Run();
        await PersistenceChecks.Run();
        var state = WorldChanges.Empty;
        BuildEdit Place(PieceKind kind, Guid target = default, int socket = 0, Vector3 anchor = default) => BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "place", kind, target, socket, anchor), (_, _) => 0, (_, _) => true);
        var first = Place(PieceKind.TriangleFoundation, anchor: new(0, .5f, 0));
        Verify.Check(first.Valid, "triangle foundation accepted");
        state = first.State;
        for (int i = 1; i < 6; i++)
        {
            var next = Place(PieceKind.TriangleFoundation, state.Pieces[^1].Id, 2);
            Verify.Check(next.Valid, $"hexagon triangle {i}: {next.Error}");
            state = next.State;
        }
        var points = state.Pieces.SelectMany(BuildingGeometry.Corners).Distinct().ToArray();
        Verify.Check(points.Length == 7, "six exact triangles close into hexagon with seven vertices");
        var duplicate = Place(PieceKind.TriangleFoundation, state.Pieces[^1].Id, 2);
        Verify.Check(!duplicate.Valid, "hexagon closure rejects duplicate tile");
        var tall = BuildingRules.Edit(WorldChanges.Empty, new(Guid.NewGuid(), 0, "place", Anchor: new(0, .5f, 0)), (_, _) => 0, (_, _) => true);
        for (int level = 0; level < 8; level++)
        {
            var support = BuildingRules.Edit(tall.State, new(Guid.NewGuid(), tall.State.Revision, "place", PieceKind.Wall, tall.Piece!.Id), (_, _) => 0, (_, _) => true);
            Verify.Check(support.Valid, "storey wall " + (level + 1));
            tall = BuildingRules.Edit(support.State, new(Guid.NewGuid(), support.State.Revision, "place", PieceKind.SquareFloor, support.Piece!.Id), (_, _) => 0, (_, _) => true);
        }
        Verify.Check(tall.Valid && tall.Piece!.Level == 8, "eighth storey permits a flat roof");
        Verify.Check(!BuildingRules.Edit(tall.State, new(Guid.NewGuid(), tall.State.Revision, "place", PieceKind.Wall, tall.Piece!.Id), (_, _) => 0, (_, _) => true).Valid, "ninth storey rejected");
        var serialized = System.Text.Json.JsonSerializer.Serialize(state, Protocol.Json);
        var restored = System.Text.Json.JsonSerializer.Deserialize<WorldChanges>(serialized, Protocol.Json)!;
        Verify.Check(restored.Pieces.SequenceEqual(state.Pieces), "canonical building coordinates survive JSON roundtrip");
        state = WorldChanges.Empty;
        first = Place(PieceKind.SquareFoundation, anchor: new(-64, .5f, -64));
        state = first.State;
        var root = state.Pieces[0];
        var wall = Place(PieceKind.Doorway, root.Id, 0);
        Verify.Check(wall.Valid, "doorway attaches to foundation");
        state = wall.State;
        var door = Place(PieceKind.Door, wall.Piece!.Id);
        Verify.Check(door.Valid, "door fits doorway opening");
        state = door.State;
        var upper = Place(PieceKind.SquareFloor, wall.Piece.Id);
        Verify.Check(upper.Valid, "wall supports upper floor");
        state = upper.State;
        var overhang = Place(PieceKind.TriangleFloor, upper.Piece!.Id, 2);
        Verify.Check(overhang.Valid, "one-tile cantilever accepted");
        state = overhang.State;
        Verify.Check(!Place(PieceKind.Wall, overhang.Piece!.Id, 1).Valid, "cantilever cannot support upper walls");
        Verify.Check(!BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "remove", Target: root.Id), (_, _) => 0, (_, _) => true).Valid, "supporting foundation cannot be demolished");
        var open = BuildingRules.Edit(state, new(Guid.NewGuid(), state.Revision, "door", Target: door.Piece!.Id), (_, _) => 0, (_, _) => true);
        Verify.Check(open.Valid && open.State.Pieces.Single(p => p.Id == door.Piece.Id).Open, "door state toggles");
        var wallMesh = BuildingGeometry.Mesh(state.Structures[0], wall.Piece);
        var doorCentre = state.Structures[0].Origin + new Vector3(1.5f, 1, 0);
        Verify.Check(BuildingGeometry.Ray(doorCentre + new Vector3(0, 0, -1), Vector3.UnitZ, wallMesh) is null, "doorway has a real collision opening");
        var crossing = BuildingGeometry.Chunks(BuildingGeometry.Bounds(BuildingGeometry.Mesh(state.Structures[0], root))).ToArray();
        Verify.Check(crossing.Contains(new(-1, -1)), "negative chunk ownership");
        var stairState = first.State;
        var stairs = BuildingRules.Edit(stairState, new(Guid.NewGuid(), stairState.Revision, "place", PieceKind.Stairs, root.Id), (_, _) => 0, (_, _) => true);
        Verify.Check(stairs.Valid, "stairs attach to square foundation");
        var blocked = BuildingRules.Edit(WorldChanges.Empty, new(Guid.NewGuid(), 0, "place", Anchor: new(0, 3, 0)), (_, _) => 0, (_, _) => true);
        Verify.Check(!blocked.Valid, "floating foundation rejected");
        Verify.Check(!BuildingRules.Edit(WorldChanges.Empty, new(Guid.NewGuid(), 0, "place", Anchor: new(0, .5f, 0)), (_, _) => 0, (_, _) => false).Valid, "water foundation rejected");
        string directory = Path.Combine(Path.GetTempPath(), "stargate-building-tests-" + Guid.NewGuid().ToString("N"));
        using var generation = new GenerationService(7319, "temperate", settlements: new("wilderness"));
        var plan = await generation.Plan;
        Guid location, universe;
        await using (var db = new WorldRepository(directory))
        {
            var saved = await db.ResolveAsync(plan.Definition);
            location = saved.Id;
            universe = saved.Changes.UniverseId;
            state = state with
            {
                UniverseId = universe,
                LocationId = location,
                Revision = 1
            };
            await db.SaveAsync(state, 0);
            var loaded = await db.LoadAsync(location);
            Verify.Check(loaded.Changes.Pieces.OrderBy(p => p.Id).SequenceEqual(state.Pieces.OrderBy(p => p.Id)), "SQLite saves building records atomically");
            var other = await db.CreateAsync(plan.Definition);
            Verify.Check(other.Id != location && other.Changes.Pieces.Length == 0, "same-seed locations have independent changes");
            Verify.Check((await db.ResolveAsync(plan.Definition)).Id == location, "developer selector retains stable location identity");
            bool conflict = false;
            try
            {
                await db.SaveAsync(state, 0);
            }
            catch (InvalidOperationException)
            {
                conflict = true;
            }
            Verify.Check(conflict, "stale save revision rejected");
            var removed = new SceneryRemoval("tree", 123, new(0, 0));
            state = state with
            {
                Revision = 2,
                Removed = [removed]
            };
            await db.SaveAsync(state, 1);
            var parts = new WorldStateReceiver();
            foreach (var part in WorldStateWire.Split(state))
                parts.Add(part);
            Verify.Check(parts.State!.Pieces.SequenceEqual(state.Pieces) && parts.State.Removed[0] == removed, "bounded world fragments preserve changes");
        }
        await using (var db = new WorldRepository(directory))
        {
            var loaded = await db.ResolveAsync(plan.Definition);
            Verify.Check(loaded.Id == location && loaded.Changes.UniverseId == universe && loaded.Changes.Removed.Length == 1, "world identity and removals survive repository restart");
        }
        var malformed = new WorldStateReceiver();
        bool rejected = false;
        try
        {
            malformed.Add(new(Guid.NewGuid(), 0, 1, 1, [], [], [], Guid.NewGuid()));
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Verify.Check(rejected, "out-of-range world fragment index rejected");
        var bounded = new WorldStateReceiver();
        var fragment = new WorldStatePart(Guid.NewGuid(), 0, 0, 1, [], [], [], Guid.NewGuid());
        bounded.Add(fragment);
        rejected = false;
        try
        {
            bounded.Add(fragment with
            {
                Index = 1
            });
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Verify.Check(rejected, "completed world transfer rejects extra fragments");
        rejected = false;
        try
        {
            BuildingRules.ValidateState(state with
            {
                Pieces = [state.Pieces[0] with { Point = new(int.MinValue, 0, 0, 0) }]
            });
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        Verify.Check(rejected, "extreme canonical coordinates rejected without arithmetic overflow");
        if (network)
            await Network();
    }
    public static async Task Network(bool delayed = false, bool prefabs = false, bool gateZone = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "stargate-build-network-" + Guid.NewGuid().ToString("N"));
        // This fixture tests building transactions, not finding a way through a
        // particular forest. Persist a cleared work area; keep real terrain,
        // rocks and the remaining forest so validation/streaming still run.
        var plan = new PlanetPlan(7319, "temperate", settlements: new("wilderness"));
        await using (var repository = new WorldRepository(directory))
        {
            var fixture = await repository.ResolveAsync(plan.Definition);
            var removals = ChunkCoordinate.At(plan.Gate.X, plan.Gate.Z).Around(2).Select(plan.BuildChunk)
                .SelectMany(c => TreeScenery.Resolve(c, "temperate").Where(t => Vector2.Distance(new(t.Transform.M41, t.Transform.M43), new(plan.Gate.X, plan.Gate.Z)) < 70)
                    .Select(t => new SceneryRemoval("tree", t.Id, c.Coordinate))).ToArray();
            await repository.SaveAsync(fixture.Changes with
            {
                Revision = 1,
                Removed = removals
            }, 0);
        }
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, 7319, "temperate", true, directory, settlements: new("wilderness"));
        server.Start();
        await using var proxy = delayed ? new DelayProxy(server.Port, 0) : null;
        int port = proxy?.Port ?? server.Port;
        using var first = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", port, "Builder", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        using var second = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", port, "Visitor", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        async Task Pump(int ms, float strafe = 0, float forward = 0)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < ms)
            {
                first.Poll();
                second.Poll();
                first.Step(strafe, forward, 0, 0, true, false);
                second.Step(0, 0, 0, 0, false, false);
                if (first.Failure is not null || second.Failure is not null)
                    throw new Exception(first.Failure ?? second.Failure);
                await Task.Delay(10);
            }
        }
        // Generator changes can put scenery east of the clearing. Leave through
        // the reserved southern approach and wait for actual travel, not wall time.
        var gatePosition = first.World.Definition.Entities[0].Position;
        var approach = Stopwatch.StartNew();
        while (!gateZone && first.Local.Position.Z - gatePosition.Z < 36 && approach.Elapsed.TotalSeconds < 30)
            await Pump(100, 0, -1);
        await Pump(700);
        if (!gateZone)
            Verify.Check(first.Local.Position.Z - gatePosition.Z >= 36, "builder reaches the outside building fixture");
        Verify.Check(first.Changes.LocationId != Guid.Empty && first.Changes.LocationId == second.Changes.LocationId, "two clients share persistent location identity");
        BuildResult? result = null;
        Guid request = Guid.Empty;
        // Find a reachable dry unoccupied anchor. Server rejects scenery/gate protection and keeps the session alive.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            while (first.TransitionPending)
                await Pump(200);
            var pos = first.Local.Position;
            float x = MathF.Round((pos.X - 5 + attempt % 10) * 4) / 4, z = MathF.Round((pos.Z + 2 + attempt / 10) * 4) / 4;
            // Leave room for wall end caps around the foundation, outside the protected clearing.
            var gate = first.World.Definition.Entities[0].Position;
            var nearest = new Vector2(Math.Clamp(gate.X, x - .2f, x + 3.2f), Math.Clamp(gate.Z, z - .2f, z + 3.2f));
            if (!gateZone && Vector2.Distance(nearest, new(gate.X, gate.Z)) < 28.1f)
                continue;
            float h = MathF.Ceiling((new[] { first.World.SampleHeight(x, z), first.World.SampleHeight(x + 3, z), first.World.SampleHeight(x, z + 3), first.World.SampleHeight(x + 3, z + 3) }.Max() + .25f) * 4) / 4;
            if (prefabs)
                h = MathF.Ceiling((new[] { first.World.SampleHeight(x - 1, z - 1), first.World.SampleHeight(x + 1, z - 1), first.World.SampleHeight(x - 1, z + 1), first.World.SampleHeight(x + 1, z + 1) }.Max() + .25f) * 4) / 4;
            request = Guid.NewGuid();
            first.Build(new(request, first.Changes.Revision, "place", prefabs ? PieceKind.Prefab : PieceKind.SquareFoundation, Anchor: new(x, h, z), Prefab: prefabs ? "kit-stone-foundation" : ""));
            for (int i = 0; i < 150 && first.BuildPending; i++)
                await Pump(100);
            result = first.LastBuildResult;
            if (result?.Success == true)
                break;
            Console.WriteLine("BUILD ATTEMPT: " + result?.Reason);
            await Pump(220);
        }
        Verify.Check(result?.Success == true, "network placement accepted: " + result?.Reason);
        await Pump(500);
        Verify.Check(first.Changes.Pieces.Length == 1 && second.Changes.Pieces.Length == 1 && first.Changes.Revision == second.Changes.Revision, "two clients commit building topology together");
        if (gateZone)
        {
            var placed = BuildingGeometry.Bounds(BuildingGeometry.Mesh(first.Changes, first.Changes.Pieces[0]));
            var nearest = Vector3.Clamp(gatePosition, placed.Min, placed.Max);
            Verify.Check(Vector2.Distance(new(nearest.X, nearest.Z), new(gatePosition.X, gatePosition.Z)) < 28, "placement inside former gate exclusion accepted by server");
            await Pump(250);
            first.Build(new(Guid.NewGuid(), first.Changes.Revision, "remove", Target: first.Changes.Pieces[0].Id));
            for (int i = 0; i < 150 && first.BuildPending; i++)
                await Pump(100);
            await Pump(300);
            Verify.Check(first.LastBuildResult?.Success == true && first.Changes.Pieces.Length == 0 && second.Changes.Pieces.Length == 0, "gate-zone demolition replicated to both players");
            return;
        }
        Guid location = first.Changes.LocationId;
        var foundationId = first.Changes.Pieces[0].Id;
        async Task<BuildResult> Submit(BuildCommand command)
        {
            await Pump(250);
            while (first.TransitionPending)
                await Pump(100);
            first.Build(command with
            {
                Revision = first.Changes.Revision
            });
            for (int i = 0; i < 150 && first.BuildPending; i++)
                await Pump(100);
            return first.LastBuildResult ?? throw new Exception("No build result.");
        }
        var doorWallResult = await Submit(new(Guid.NewGuid(), 0, "place", prefabs ? PieceKind.Prefab : PieceKind.Doorway, foundationId, Prefab: prefabs ? "kit-assembled-door" : ""));
        Verify.Check(doorWallResult.Success, "server accepts doorway: " + doorWallResult.Reason);
        var doorway = first.Changes.Pieces.Single(p => prefabs ? p.Prefab == "kit-assembled-door" : p.Kind == PieceKind.Doorway);
        if (!prefabs)
        {
            var doorResult = await Submit(new(Guid.NewGuid(), 0, "place", PieceKind.Door, doorway.Id));
            Verify.Check(doorResult.Success, "server accepts door: " + doorResult.Reason);
        }
        else
            Verify.Check(first.Changes.Pieces.Length == 3, "complete door wall creates its working leaf atomically");
        var door = first.Changes.Pieces.Single(BuildingPrefabs.IsDoor);
        var doorStructure = first.Changes.Structures.Single(s => s.Id == door.StructureId);
        var front = prefabs ? Vector3.Transform(door.Offset, BuildingGeometry.Transform(doorStructure)) + new Vector3(.5f, 0, -2) : BuildingGeometry.WorldPoint(doorStructure, door.Point, door.Level) + new Vector3(1.5f, 0, -2);
        for (int i = 0; i < 100 && Vector2.Distance(new(first.Local.Position.X, first.Local.Position.Z), new(front.X, front.Z)) > .25f; i++)
        {
            var delta = front - first.Local.Position;
            await Pump(60, Math.Clamp(delta.X, -1, 1), Math.Clamp(-delta.Z, -1, 1));
        }
        await Pump(400);
        Vector3 DoorHit()
        {
            var mesh = BuildingGeometry.Mesh(first.Changes, first.Changes.Pieces.Single(p=>p.Id==door.Id));
            var bounds = BuildingGeometry.Bounds(mesh);
            var eye = Movement.EyePosition(first.Local);
            var direction = Vector3.Normalize((bounds.Min+bounds.Max)/2-eye);
            return eye + direction * (BuildingGeometry.Ray(eye,direction,mesh,8) ?? throw new InvalidOperationException("Door is not in reach."));
        }
        var toggle = await Submit(new(Guid.NewGuid(), 0, "door", Target: door.Id, HitPoint: DoorHit()));
        Verify.Check(toggle.Success && first.Changes.Pieces.Single(p => p.Id == door.Id).Open, "server opens door with sweep clearance: " + toggle.Reason);
        var blocked = await Submit(new(Guid.NewGuid(), 0, "remove", Target: foundationId));
        Verify.Check(!blocked.Success && blocked.Dependents?.Length > 0, "server rejects demolition with dependent walls/doors");
        long revision = first.Changes.Revision;
        var joining = GameConnection.ConnectAsync("127.0.0.1", port, "LateBuilder", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        while (!joining.IsCompleted)
            await Pump(40);
        using (var late = new ClientSession(await joining))
        {
            Verify.Check(late.Changes.Pieces.Length == 3 && late.Changes.LocationId == location, "late join restores building state before prediction");
        }
        await Pump(500);
        first.Build(new(request, 0, "place"));
        await Pump(300);
        Verify.Check(first.LastBuildResult?.Success == true && first.Changes.Revision == revision, "duplicate request returns cached result without duplicate piece");
        var piece = first.Changes.Pieces.Single(p => p.Id == foundationId);
        var beforePosition = first.Local.Position;
        for (int i = 0; i < 300 && first.World.Definition.Preset != "desert"; i++)
        {
            if (i % 10 == 0 && !first.TransitionPending)
                first.SwitchPlanet(42, "desert");
            await Pump(100);
        }
        Verify.Check(first.World.Definition.Preset == "desert" && first.Changes.LocationId != location && first.Changes.Pieces.Length == 0, "destination keeps independent building changes");
        while (first.TransitionPending)
            await Pump(100);
        for (int i = 0; i < 300 && first.World.Definition.Preset != "temperate"; i++)
        {
            if (i % 10 == 0 && !first.TransitionPending)
                first.SwitchPlanet(7319, "temperate");
            await Pump(100);
        }
        Verify.Check(first.Changes.LocationId == location && first.Changes.Pieces.Any(p => p.Id == piece.Id) && first.Changes.Pieces.Single(BuildingPrefabs.IsDoor).Open, "returning to planet restores its saved building");
        // Walk back towards the saved site after gate respawn.
        for (int i = 0; i < 120 && Vector3.Distance(first.Local.Position, beforePosition) > 1; i++)
        {
            var delta = beforePosition - first.Local.Position;
            await Pump(80, Math.Clamp(delta.X, -1, 1), Math.Clamp(-delta.Z, -1, 1));
        }
        await Pump(500);
        var doorRemove = await Submit(new(Guid.NewGuid(), 0, "remove", Target: door.Id));
        Verify.Check(doorRemove.Success, "remove saved door: " + doorRemove.Reason);
        if (!prefabs)
        {
            var wallRemove = await Submit(new(Guid.NewGuid(), 0, "remove", Target: doorway.Id));
            Verify.Check(wallRemove.Success, "remove saved doorway: " + wallRemove.Reason);
        }
        else
            Verify.Check(!first.Changes.Pieces.Any(p => p.Id == doorway.Id), "removing the leaf removes its complete door wall");
        var pending = first.Physics.Save();
        first.Build(new(Guid.NewGuid(), first.Changes.Revision, "remove", Target: piece.Id));
        for (int i = 0; i < 150 && first.BuildPending; i++)
            await Pump(100);
        Verify.Check(first.LastBuildResult?.Success == true && second.Changes.Pieces.Length == 0, "network demolition removes client colliders");
        bool invalid = false;
        try
        {
            first.Physics.Restore(pending);
        }
        catch (InvalidOperationException)
        {
            invalid = true;
        }
        pending.Dispose();
        Verify.Check(invalid, "old topology checkpoint cannot restore demolished building");
        await server.DisposeAsync();
        await using var repo = new WorldRepository(directory);
        var saved = await repo.LoadAsync(location);
        Verify.Check(saved.Changes.Revision == (prefabs ? 6 : 8) && saved.Changes.Pieces.Length == 0 && saved.Bodies.Length == 4, "shutdown saves final revision and crate state");
        Verify.Check((await repo.FindReceiptAsync(request))?.Success == true, "successful request receipt survives server restart");
    }
}
