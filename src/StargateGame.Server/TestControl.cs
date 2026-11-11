using StargateGame.Galaxy;
using StargateGame.Persistence;
using System.Net;
using System.Numerics;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Building;
using StargateGame.Testing;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    readonly TestCommandQueue testCommands = new();
    string testSession = "";
    int testGeneration;
    TestRequest? testPreparation;
    Task<GalaxyTarget>? testResolution;
    TestResult? testPreparationResult;
    long testPreparationDeadline;
    readonly HashSet<Guid> testPreparedLocations = [];
    object[] TestRoutes() => galaxy is null || repository is null ? [] : Address.Data.Routes.Where(r => r.DhdCompatible).Select(r => {
        var association = repository.Association(galaxy, r.Key);
        string biome = r.Name == "Earth" ? "temperate" : r.Name == "Abydos" ? "desert" : new[] { "temperate", "desert", "icy" }[(int)((uint)Address.ContentSeed(galaxy.Seed, association.LocationId) % 3)];
        return (object)new { name = r.Name, address = r.GameSequence, biome, location = association.LocationId };
    }).ToArray();
    void PumpTestPreparation()
    {
        if (testPreparation is not { } r) return;
        if (Environment.TickCount64 > testPreparationDeadline) { testPreparationResult = TestResult.Fail(r, new TimeoutException("Destination preparation timed out; pending locations: " + locationLoads.Count)); testPreparation = null; return; }
        if (testResolution is not { IsCompleted: true }) return;
        try {
            var resolved = testResolution.GetAwaiter().GetResult();
            testPreparedLocations.Add(resolved.Association.LocationId); BeginLoad(resolved.Association);
            if (!contexts.Any(c => c.Association?.LocationId == resolved.Association.LocationId && c.CollisionReady)) return;
            testPreparationResult = TestResult.Ok(r, new { location = resolved.Association.LocationId }); testPreparation = null;
        } catch (Exception e) { testPreparationResult = TestResult.Fail(r, e); testPreparation = null; }
    }
    public LocalPipe? StartTestControl(string[] args)
    {
        string pipe = Arguments.Get(args, "--test-pipe", "");
        if (pipe.Length == 0) return null;
        if (!allowTestIdentities || !devPlanets || !IPAddress.IsLoopback(((IPEndPoint)listener.LocalEndpoint).Address))
            throw new InvalidOperationException("Test control requires a private loopback test server.");
        testSession = Arguments.Get(args, "--test-session", "");
        testGeneration = Arguments.Number(args, "--test-generation", 1, 1, int.MaxValue);
        PrepareTestRegionalView(Arguments.Get(args, "--test-preset", "default"));
        DiagnosticDestinationDelayMilliseconds = Arguments.Number(args, "--test-destination-delay", 0, 0, 120000);
        return new LocalPipe(pipe, testCommands.Enqueue);
    }
    TestResult ExecuteTestCommand(TestRequest r)
    {
        if (r.Session != testSession || r.Generation != testGeneration) throw new InvalidOperationException("Stale test session/generation.");
        if (r.Action == TestAction.Stop) { stop.Cancel(); return TestResult.Ok(r); }
        if (r.Action == TestAction.Status) return TestResult.Ok(r, new { ready = true, port = Port, routes = TestRoutes(), operation = testPreparationResult, pendingLocations = locationLoads.Count, repositoryPending = repository?.Pending, relocations = relocations.Count, lookups = AllSessions.Select(s => new { player = s.PlayerId, move = s.MoveLookup?.Status.ToString() }).ToArray(), regionalView = testRegionalView, tick = serverTick, physicsHash = PhysicsWorld.PhysicsHash, contentHash = PhysicsWorld.ContentHash });
        if (r.Action == TestAction.PrepareDestination)
        {
            if (testPreparation is not null || repository is null || galaxy is null || catalogue is null) throw new InvalidOperationException("Destination preparation unavailable or busy.");
            testResolution = repository.ResolveGalaxyAsync(galaxy, catalogue, Address.Resolve(r.Value<int[]>("address")).Key);
            testPreparation = r; testPreparationDeadline = Environment.TickCount64 + Math.Clamp(r.TimeoutSeconds, 1, 300) * 1000L; testPreparationResult = new(r.Id, r.Session, r.Generation, "pending", TestJson.Element(new { }), Client: r.Client);
            return testPreparationResult;
        }
        if (r.Action is not (TestAction.SetupProp or TestAction.Teleport or TestAction.ResetPlayer or TestAction.ResetStation)) throw new ArgumentException("Unsupported server action.");
        var target = contexts.Single(c => c.Physics.World.Definition.LocationId == r.Value<Guid>("location"));
        var world = target.Physics;
        if (r.Action == TestAction.SetupProp)
        {
            var prop = world.World.Definition.Props.Single(p => p.PersistentId == r.Value<Guid>("item"));
            var propPosition = r.Value<Vector3>("position");
            if (!float.IsFinite(propPosition.X + propPosition.Y + propPosition.Z)) throw new ArgumentException("Invalid prop position.");
            var snapshot = world.Snapshot();
            world.Apply(snapshot with { Bodies = snapshot.Bodies.Select(b => b.Id == prop.Id ? b with { Position = propPosition, LinearVelocity = Vector3.Zero, AngularVelocity = Vector3.Zero } : b).ToArray() }); world.Epoch++;
            return TestResult.Ok(r, new { setup = true, item = prop.PersistentId, position = propPosition });
        }
        if (r.Action == TestAction.ResetStation)
        {
            if (world.World.Definition.Fixture != "gate-pair") throw new InvalidOperationException("Station reset is available in Sandbox; use scenario reset for generated worlds.");
            string station = r.Text("station", "props");
            if (station is "props")
            {
                var original = GraphicsFixture.Create("gate-pair", world.World.Definition.Preset);
                foreach (var prop in original.Props)
                {
                    world.RemoveProp(prop.Id);
                    world.InsertProp(prop, new(prop.Id, prop.Position, Quaternion.Identity, Vector3.Zero, Vector3.Zero, false));
                }
                world.Epoch++;
                return TestResult.Ok(r, new { station });
            }
            if (station is "gate" or "gate-pair")
            {
                if (fixtureFlights.Count != 0) throw new InvalidOperationException("Wait for travelers to clear before resetting gates.");
                fixtureConnection = null; fixtureCloseRequested = false;
                fixturePreviousBodies.Clear(); fixturePreviousPlayers.Clear();
                foreach (var gate in fixtureGates.Values.ToArray()) PublishFixture(gate with { Phase = GatePhase.Idle, Symbols = [], ConnectionId = Guid.Empty, Owner = 0 });
                return TestResult.Ok(r, new { station });
            }
            if (station is "interior" or "equipment" or "movement")
            {
                if (target.Edit is not null || target.Transition is not null || target.Preparing is not null) throw new InvalidOperationException("World update in progress.");
                if (station == "equipment") fixtureContainers.Clear();
                var stationBaseline = SandboxStations.Create(world.World.Definition);
                var ids = stationBaseline.Structures.Where(s => station == "interior" ? s.Generated : station == "movement" ? s.Id == GeneratedBuildings.Identity(world.World.Definition.LocationId, "sandbox-stairs") || s.Id == GeneratedBuildings.Identity(world.World.Definition.LocationId, "sandbox-platform") : s.Id == GeneratedBuildings.Identity(world.World.Definition.LocationId, "sandbox-equipment")).Select(s => s.Id).ToHashSet();
                var changes = target.Runtime!.Changes;
                var restored = changes with
                {
                    Revision = changes.Revision + 1,
                    Structures = changes.Structures.Where(s => !ids.Contains(s.Id)).Concat(stationBaseline.Structures.Where(s => ids.Contains(s.Id))).ToArray(),
                    Pieces = changes.Pieces.Where(p => !ids.Contains(p.StructureId)).Concat(stationBaseline.Pieces.Where(p => ids.Contains(p.StructureId))).ToArray()
                };
                BuildingRules.ValidateState(restored);
                var previous = context;
                try
                {
                    context = target;
                    edit = new(null, new(r.Id, changes.Revision, "reset"), new(restored, null, "", []), Vector3.Zero);
                    preparing = PrepareCollision(PhysicsWorld.IncludeBuildingOwners(restored, physics.TerrainCoordinates));
                }
                finally { context = previous; }
                return TestResult.Ok(r, new { station, revision = restored.Revision });
            }
            if (station is "forest" or "water" or "technology") return TestResult.Ok(r, new { station, immutable = true });
            throw new InvalidOperationException("Unknown or unavailable station: " + station);
        }
        int id = r.Int("player"); var old = world.Player(id);
        Vector3 position = r.Action == TestAction.ResetPlayer ? world.World.Definition.Spawns[0] : r.Data.TryGetProperty("position", out _) ? r.Value<Vector3>("position") : r.Text("station") switch
        {
            "spawn" => world.World.Definition.Spawns[0],
            "equipment" => new(22, .03f, 11.6f), "interior" => new(25, .03f, 32),
            "movement" => new(-24, .03f, 17), "props" => new(-9, .03f, 9),
            "technology" => new(37, .03f, 16), "water" => new(0, .03f, 53), "forest" => new(30, .03f, -20),
            "gate" or "gate-pair" => world.World.Definition.Entities.First(e => e.Model == "stargate").Position + new Vector3(0, PlanetSettings.GateEmbedDepth + .03f, 3),
            "dhd" => world.World.Definition.Entities.First(e => e.Model == "dhd").Position + new Vector3(0, .03f, 1.1f),
            _ => throw new ArgumentException("This bookmark has no safe player station; provide an explicit setup position.")
        };
        if (!float.IsFinite(position.X + position.Y + position.Z) || !world.CanPlacePlayer(position, id)) throw new ArgumentException("Setup position is obstructed or invalid.");
        if (!double.IsFinite(r.Number("yaw", old.Yaw)) || !double.IsFinite(r.Number("pitch", old.Pitch)) || Math.Abs(r.Number("pitch", old.Pitch)) > 1.55) throw new ArgumentException("Invalid setup look angles.");
        var baseline = world.Snapshot();
        world.Apply(baseline with { Players = baseline.Players.Select(p => p.Id == id ? p with { Position = position, Velocity = Vector3.Zero, Yaw = (float)r.Number("yaw", old.Yaw), Pitch = (float)r.Number("pitch", old.Pitch), TransitFrozen = false } : p).ToArray() });
        world.Epoch++;
        return TestResult.Ok(r, new { player = id, position, setup = true });
    }
}
