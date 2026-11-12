using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using JoltPhysicsSharp;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Physics;

public readonly record struct DebugLine(Vector3 A, Vector3 B, Vector3 Color);

/// <summary>A single-owner simulation. Snapshot data is portable; native checkpoints belong to this world only.</summary>
public sealed partial class PhysicsWorld : IDisposable
{
    const uint StaticLayer = 0, PropLayer = 1, CharacterLayer = 2, StairPropLayer = 3, StairPlayerLayer = 4;
    readonly PhysicsSystem system;
    readonly JobSystemThreadPool jobs;
    readonly nint allocator;
    readonly List<Shape> shapes = [];
    readonly List<BodyID> ownedBodies = [];
    readonly SortedDictionary<int, BodyID> props = [];
    readonly SortedDictionary<int, CharacterVirtual> characters = [];
    readonly SortedDictionary<int, PlayerState> players = [];
    readonly SortedDictionary<int, AppliedCommand> commands = [];
    readonly List<DebugLine> staticLines = [];
    readonly HashSet<Checkpoint> checkpoints = [];
    readonly SortedDictionary<ChunkCoordinate, (BodyID Body, Shape Shape)> terrainBodies = [];
    readonly Dictionary<ChunkCoordinate, Shape> preparedTerrain = [];
    readonly ITerrainSource? terrainSource;
    readonly bool hasWorldGeometry;
    public IReadOnlyCollection<ChunkCoordinate> TerrainCoordinates => terrainBodies.Keys;
    readonly Shape characterShape, crouchShape;
    readonly NativeObject[] transferredFilters;
    bool disposed;
    long topology;
    public PlanetWorld World
    {
        get;
    }
    public long Tick
    {
        get;
        private set;
    }
    public long Epoch
    {
        get;
        set;
    } = 1;
    public IEnumerable<PlayerState> Players => players.Values;
    public PlayerState Player(int id) => players[id];
    public static string ContentHash
    {
        get;
    } = ComputeContentHash();
    public static string PhysicsHash
    {
        get;
    } = ComputePhysicsHash();
    // Serialize native lifecycle/steps across worlds because joltc maintains a process-global registry.
    static readonly object NativeGate = new();

    public PhysicsWorld(PlanetDefinition definition, bool buildWorld = true, ITerrainSource? terrain = null, IEnumerable<ChunkCoordinate>? collisionChunks = null, WorldChanges? changes = null)
    {
        // Build immutable triangle data before taking the process-wide native lock.
        if (terrain is not null && collisionChunks is not null)
            foreach (var c in collisionChunks)
                if (terrain.TryGetChunk(c, out var data))
                    PrepareCollisionInput(data, definition.Preset, changes ?? WorldChanges.Empty, definition.Fixture).GetAwaiter().GetResult();
        if (collisionChunks is not null && changes is not null)
        {
            var wanted = collisionChunks.ToHashSet();
            foreach (var group in changes.Pieces.GroupBy(p => Owner(changes, p)).Where(g => wanted.Contains(g.Key)))
                BuildingInput(changes, group.Key, group).GetAwaiter().GetResult();
        }
        lock (NativeGate)
        {
            Native.Initialize();
            terrainSource = terrain;
            hasWorldGeometry = buildWorld;
            World = new(definition, terrain);
            Changes = changes ?? WorldChanges.Empty;
            var pairs = new ObjectLayerPairFilterTable(5);
            pairs.EnableCollision(StaticLayer, PropLayer);
            pairs.EnableCollision(StaticLayer, CharacterLayer);
            pairs.EnableCollision(PropLayer, PropLayer);
            pairs.EnableCollision(PropLayer, CharacterLayer);
            pairs.EnableCollision(StairPropLayer, PropLayer);
            pairs.EnableCollision(StairPlayerLayer, CharacterLayer);
            var broad = new BroadPhaseLayerInterfaceTable(5, 2);
            broad.MapObjectToBroadPhaseLayer(StaticLayer, 0);
            broad.MapObjectToBroadPhaseLayer(StairPropLayer, 0);
            broad.MapObjectToBroadPhaseLayer(StairPlayerLayer, 0);
            broad.MapObjectToBroadPhaseLayer(PropLayer, 1);
            broad.MapObjectToBroadPhaseLayer(CharacterLayer, 1);
            var filter = new ObjectVsBroadPhaseLayerFilterTable(broad, 2, pairs, 5);
            system = new(new()
            {
                MaxBodies = 4096,
                MaxBodyPairs = 32768,
                MaxContactConstraints = 16384,
                ObjectLayerPairFilter = pairs,
                BroadPhaseLayerInterface = broad,
                ObjectVsBroadPhaseLayerFilter = filter
            });
            Native.Own(system);
            // JoltPhysicsSharp 2.23.0 does not transfer wrapper ownership, although joltc's
            // PhysicsSystem destructor deletes these filters. Treat the wrappers as borrowed
            // and dispose their managed registrations after the system destroys the native filters.
            transferredFilters = [pairs, broad, filter];
            foreach (var ownedBySystem in transferredFilters)
                Native.TransferOwnership(ownedBySystem);
            jobs = new(new JobSystemThreadPoolConfig { maxJobs = 2048, maxBarriers = 8, numThreads = 1 });
            allocator = Native.SG_AllocatorCreate();
            system.Gravity = new(0, -Movement.Gravity, 0);
            characterShape = Own(new RotatedTranslatedShape(new Vector3(0, Movement.Height / 2, 0), Quaternion.Identity,
                Own(new CapsuleShape(Movement.Height / 2 - Movement.Radius, Movement.Radius))));
            crouchShape = Own(new RotatedTranslatedShape(new Vector3(0, Movement.CrouchHeight / 2, 0), Quaternion.Identity,
                Own(new CapsuleShape(Movement.CrouchHeight / 2 - Movement.Radius, Movement.Radius))));
            try
            {
                if (buildWorld)
                {
                    if (terrain is null)
                        AddMesh(World.Triangles().ToArray(), Vector3.Zero, true);
                    else
                        SetTerrain(collisionChunks ?? []);
                    foreach (var entity in definition.Entities)
                        entityBodies[entity.Id] = AddMesh(ReadCollision(entity.Model).Select(t => (Vector3.Transform(t.A, entity.Rotation), Vector3.Transform(t.B, entity.Rotation), Vector3.Transform(t.C, entity.Rotation))).ToArray(), entity.Position, false, entity.Scale);
                    float h = definition.HalfSize;
                    float wallHeight = terrain is null ? 48 : 2048;
                    AddStaticBox(new(h + .5f, 16, 0), new(.5f, wallHeight, h + 1));
                    AddStaticBox(new(-h - .5f, 16, 0), new(.5f, wallHeight, h + 1));
                    AddStaticBox(new(0, 16, h + .5f), new(h + 1, wallHeight, .5f));
                    AddStaticBox(new(0, 16, -h - .5f), new(h + 1, wallHeight, .5f));
                    foreach (var prop in definition.Props.OrderBy(p => p.Id))
                        AddProp(prop);
                }
                system.OptimizeBroadPhase();
            }
            catch
            {
                Dispose();
                throw;
            }
        }
    }
    T Own<T>(T shape) where T : Shape
    {
        Native.Own(shape);
        shapes.Add(shape);
        return shape;
    }
    public void PrepareTerrain(IEnumerable<ChunkCoordinate> coordinates)
    {
        foreach (var c in coordinates.Order())
        {
            if (terrainBodies.ContainsKey(c) && !dirtyTerrain.Contains(c) || preparedTerrain.ContainsKey(c))
                continue;
            if (terrainSource is null || !terrainSource.TryGetChunk(c, out var chunk))
                throw new InvalidOperationException("Collision chunk is not ready.");
            var changes = stagedChanges ?? Changes;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var input = PrepareCollisionInput(chunk, World.Definition.Preset, changes, World.Definition.Fixture).GetAwaiter().GetResult();
            CollisionInputMilliseconds = watch.Elapsed.TotalMilliseconds;
            lock (NativeGate)
            {
                if (!TryRestoreTerrain(c, chunk, changes))
                    preparedTerrain.Add(c, Cook(input));
            }
        }
    }
    public bool PrepareTerrainBatch(IEnumerable<ChunkCoordinate> coordinates, int limit = 2)
    {
        var ordered = coordinates.Order().ToArray();
        var missing = ordered.Where(c => (!terrainBodies.ContainsKey(c) || dirtyTerrain.Contains(c)) && !preparedTerrain.ContainsKey(c)).ToArray();
        int cooked = 0;
        foreach (var c in missing)
        {
            if (terrainSource is null || !terrainSource.TryGetChunk(c, out var chunk))
                continue;
            var changes = stagedChanges ?? Changes;
            var input = PrepareCollisionInput(chunk, World.Definition.Preset, changes, World.Definition.Fixture);
            if (cooked >= limit || !input.IsCompleted)
                continue;
            var ready = input.GetAwaiter().GetResult();
            lock (NativeGate)
            {
                if (!TryRestoreTerrain(c, chunk, changes))
                    preparedTerrain.Add(c, Cook(ready));
            }
            cooked++;
        }
        bool buildings = PrepareBuildings(ordered, limit);
        return missing.All(preparedTerrain.ContainsKey) && buildings;
    }
    public void SetTerrain(IEnumerable<ChunkCoordinate> coordinates, bool preparedOnly = false)
    {
        var requested = coordinates.ToArray();
        if (!preparedOnly)
        {
            PrepareTerrain(requested);
            PrepareBuildings(requested, 256, true);
        }
        var commitWatch = System.Diagnostics.Stopwatch.StartNew();
        lock (NativeGate)
        {
            var wanted = requested.ToHashSet();
            var nextState = stagedChanges ?? Changes;
            if (wanted.Any(c => (!terrainBodies.ContainsKey(c) || dirtyTerrain.Contains(c)) && !preparedTerrain.ContainsKey(c)) ||
                nextState.Pieces.GroupBy(p => Owner(nextState, p)).Any(g => wanted.Contains(g.Key) &&
                    (!buildingBodies.ContainsKey(g.Key) || dirtyBuildings.Contains(g.Key)) && !preparedBuildings.ContainsKey(g.Key)))
                throw new InvalidOperationException("Collision commit attempted before replacements were ready; active geometry retained.");
            // Allocate every replacement before touching active bodies. If capacity
            // or native allocation fails, roll back only the new bodies.
            var nextTerrain = new Dictionary<ChunkCoordinate, BodyID>();
            var nextBuildings = new Dictionary<ChunkCoordinate, List<BodyID>>();
            try
            {
                foreach (var c in wanted.Where(c=>!terrainBodies.ContainsKey(c)||dirtyTerrain.Contains(c)).Order())
                    nextTerrain.Add(c,AddBody(preparedTerrain[c],Vector3.Zero,Quaternion.Identity,false));
                foreach (var c in preparedBuildings.Keys.Where(wanted.Contains).Order())
                {
                    nextBuildings[c] = [];
                    foreach (var part in preparedBuildings[c])
                        nextBuildings[c].Add(AddBody(part.Shape,Vector3.Zero,Quaternion.Identity,false,layer:part.Layer));
                }
            }
            catch
            {
                foreach (var body in nextTerrain.Values.Concat(nextBuildings.Values.SelectMany(ids=>ids)))
                {
                    system.BodyInterface.RemoveBody(body);
                    system.BodyInterface.DestroyBody(body);
                    ownedBodies.Remove(body);
                }
                throw;
            }
            // All native checkpoints belong to the previous body topology.
            foreach (var checkpoint in checkpoints.ToArray())
                checkpoint.Dispose();
            foreach (var c in terrainBodies.Keys.Where(c => !wanted.Contains(c) || dirtyTerrain.Contains(c)).ToArray())
            {
                var old = terrainBodies[c];
                system.BodyInterface.RemoveBody(old.Body);
                system.BodyInterface.DestroyBody(old.Body);
                ownedBodies.Remove(old.Body);
                RetainTerrain(c, old.Shape);
                terrainBodies.Remove(c);
                topology++;
            }
            foreach (var c in wanted.Order())
                if (!terrainBodies.ContainsKey(c))
                {
                    var shape = preparedTerrain[c];
                    preparedTerrain.Remove(c);
                    terrainBodies.Add(c, (nextTerrain[c], shape));
                }
            foreach (var c in preparedTerrain.Keys.Where(c => !wanted.Contains(c)).ToArray())
            {
                var shape = preparedTerrain[c];
                preparedTerrain.Remove(c);
                shapes.Remove(shape);
                shape.Dispose();
            }
            CommitBuildings(wanted, nextBuildings);
            foreach (var c in characters.Values)
                Refresh(c);
            system.OptimizeBroadPhase();
        }
        Console.WriteLine($"COLLISION commit revision={Changes.Revision} chunks={requested.Length} ms={commitWatch.Elapsed.TotalMilliseconds:F2}");
    }
    public bool HasTerrain(Vector3 p, float margin = 3)
    {
        if (terrainSource is null)
            return true;
        foreach (float x in new[] { p.X - margin, p.X + margin })
            foreach (float z in new[] { p.Z - margin, p.Z + margin })
            {
                var c = ChunkCoordinate.At(Math.Clamp(x, -World.Definition.HalfSize, World.Definition.HalfSize-.01f), Math.Clamp(z, -World.Definition.HalfSize, World.Definition.HalfSize-.01f));
                if (!terrainBodies.ContainsKey(c))
                    return false;
            }
        return true;
    }
    BodyID AddBody(Shape shape, Vector3 position, Quaternion rotation, bool dynamic, float mass = 10, uint? layer = null)
    {
        using var settings = Native.Own(new BodyCreationSettings(shape, position, rotation, dynamic ? MotionType.Dynamic : MotionType.Static, layer ?? (dynamic ? PropLayer : StaticLayer)));
        settings.Friction = .6f;
        settings.Restitution = .05f;
        if (dynamic)
        {
            settings.OverrideMassProperties = OverrideMassProperties.CalculateInertia;
            settings.MassPropertiesOverride = new MassProperties { Mass = mass };
            settings.MotionQuality = MotionQuality.LinearCast;
        }
        BodyID id = system.BodyInterface.CreateAndAddBody(settings, dynamic ? Activation.Activate : Activation.DontActivate);
        if (id.IsInvalid)
            throw new InvalidOperationException("Jolt body capacity exceeded.");
        ownedBodies.Add(id);
        topology++;
        return id;
    }
    public void AddStaticBox(Vector3 center, Vector3 halfExtent, Quaternion? rotation = null)
    {
        var q = rotation ?? Quaternion.Identity;
        AddBody(Own(new BoxShape(halfExtent, Math.Min(.02f, Math.Min(halfExtent.X, Math.Min(halfExtent.Y, halfExtent.Z)) / 2))), center, q, false);
        staticLines.AddRange(BoxLines(center, halfExtent, q, new(.4f, 1, .4f)));
    }
    public void AddProp(PropDefinition prop)
    {
        if (prop.Id <= 0 || props.ContainsKey(prop.Id))
            throw new ArgumentException("Duplicate prop ID.");
        Shape shape = Own(CreatePropShape(prop));
        originalPropShapes[prop.Id] = shape;
        props.Add(prop.Id, AddBody(shape, prop.Position, Quaternion.Identity, true, prop.Mass));
        propDefinitions.Add(prop.Id, prop);
    }
    readonly Dictionary<int, PropDefinition> propDefinitions = [];
    BodyID AddMesh((Vector3 A, Vector3 B, Vector3 C)[] triangles, Vector3 offset, bool terrain, float scale = 1)
    {
        if (scale != 1)
            triangles = triangles.Select(t => (t.A * scale, t.B * scale, t.C * scale)).ToArray();
        Triangle[] native = triangles.Select(t => new Triangle(t.A, t.B, t.C)).ToArray();
        using var settings = Native.Own(new MeshShapeSettings(native));
        settings.Sanitize();
        var meshBody = AddBody(Own(settings.Create()), offset, Quaternion.Identity, false);
        // Sample dense authored meshes for readable wireframes; collision always uses every triangle.
        foreach (var t in triangles.Where((_, i) => i % Math.Max(1, triangles.Length / 3000) == 0))
        {
            var color = terrain ? new Vector3(.2f, .55f, .2f) : new Vector3(.1f, .85f, 1);
            staticLines.Add(new(t.A + offset, t.B + offset, color));
            staticLines.Add(new(t.B + offset, t.C + offset, color));
            staticLines.Add(new(t.C + offset, t.A + offset, color));
        }
        return meshBody;
    }
    public void AddPlayer(PlayerState state)
    {
        lock (NativeGate)
        {
            if (characters.ContainsKey(state.Id))
                throw new ArgumentException("Duplicate player ID.");
            var character = new CharacterVirtual(new CharacterVirtualSettings
            {
                Shape = state.Crouched ? crouchShape : characterShape,
                InnerBodyShape = state.Crouched ? crouchShape : characterShape,
                InnerBodyLayer = CharacterLayer,
                Mass = 80,
                MaxStrength = Movement.PushStrength,
                MaxSlopeAngle = MathF.PI / 4,
                SupportingVolume = new Plane(Vector3.UnitY, -Movement.Radius),
                EnhancedInternalEdgeRemoval = true
            }, state.Position, Quaternion.Identity, (ulong)state.Id, system);
            Native.Own(character);
            characters.Add(state.Id, character);
            players.Add(state.Id, state);
            commands.Add(state.Id, new(state.Id, new(state.LastInput, 0, 0, state.Yaw, state.Pitch, false, false), 7));
            topology++;
            character.LinearVelocity = state.Velocity;
            Refresh(character);
            ReadPlayer(state.Id);
            if (state.TransitFrozen)
            {
                system.BodyInterface.RemoveBody(character.InnerBodyID);
                frozenCharacters.Add(state.Id);
            }
        }
    }
    public void RemovePlayer(int id)
    {
        lock (NativeGate)
        {
            DropCarried(id);
            if (characters.Remove(id, out var c))
            {
                c.Dispose();
                frozenCharacters.Remove(id);
                players.Remove(id);
                commands.Remove(id);
                topology++;
            }
        }
    }
    public void SynchronizePlayers(PhysicsSnapshot baseline)
    {
        lock (NativeGate)
        {
            Validate(baseline, World.Definition);
            foreach (var checkpoint in checkpoints.ToArray())
                checkpoint.Dispose();
            var wanted = baseline.Players.Select(p => p.Id).ToHashSet();
            foreach (var id in players.Keys.Where(id => !wanted.Contains(id)).ToArray())
                RemovePlayer(id);
            foreach (var player in baseline.Players.OrderBy(p => p.Id))
                if (!players.ContainsKey(player.Id))
                    AddPlayer(player);
            Apply(baseline);
        }
    }
    void Refresh(CharacterVirtual character) => Native.SG_Refresh(character.Handle, system.Handle, allocator, CharacterLayer);
    void ReadPlayer(int id)
    {
        var c = characters[id];
        int support = props.FirstOrDefault(p => p.Value == c.GroundBodyId).Key;
        players[id] = players[id] with
        {
            Position = c.Position,
            Velocity = c.LinearVelocity,
            Grounded = c.GroundState == GroundState.OnGround,
            SupportId = support,
            GroundNormal = c.GroundNormal,
            GroundVelocity = c.GroundVelocity,
            Surface = SupportSurface(c, support)
        };
    }
    GroundSurface SupportSurface(CharacterVirtual c, int support)
    {
        if (c.GroundState != GroundState.OnGround)
            return GroundSurface.Unknown;
        if (support != 0)
            return propDefinitions[support].Surface;
        // Streamed terrain, rocks and trunks share a body; preserve material on each triangle.
        var ground = terrainBodies.Values.FirstOrDefault(t => t.Body == c.GroundBodyId);
        if (ground.Shape is null && buildingBodies.Values.SelectMany(parts=>parts).FirstOrDefault(t => t.Body == c.GroundBodyId).Shape is MeshShape building)
            return (GroundSurface)building.GetTriangleUserData(c.GroundSubShapeId);
        if (ground.Shape is not MeshShape mesh)
            return GroundSurface.Stone;
        var surface = (GroundSurface)mesh.GetTriangleUserData(c.GroundSubShapeId);
        if (surface != GroundSurface.Unknown)
            return surface;
        var point = c.GroundPosition;
        var coord = ChunkCoordinate.At(point.X, point.Z);
        if (terrainSource?.TryGetChunk(coord, out var chunk) != true)
            return GroundSurface.Unknown;
        int x = Math.Clamp((int)MathF.Floor(point.X - coord.X * 64), 0, 63), z = Math.Clamp((int)MathF.Floor(point.Z - coord.Z * 64), 0, 63);
        if (float.IsFinite(chunk.WaterHeight(x, z)) && chunk.WaterHeight(x, z) > point.Y + .04f)
            return GroundSurface.Water;
        if (chunk.Road(x, z) > 70)
            return World.Definition.Settlements?.Outcome == "city" ? GroundSurface.Stone : GroundSurface.Gravel;
        var m = chunk.Material(x, z);
        if (m.Y > .65f)
            return GroundSurface.Stone;
        if (m.Y > .38f)
            return GroundSurface.Gravel;
        if (World.Definition.Preset == "icy")
            return GroundSurface.Snow;
        if (World.Definition.Preset == "desert")
            return GroundSurface.Sand;
        return m.Z > .65f ? GroundSurface.Mud : GroundSurface.Grass;
    }
    public void Step(IReadOnlyDictionary<int, PlayerInput>? inputs = null)
    {
        lock (NativeGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (var (id, c) in characters)
            {
                var last = commands[id];
                bool supplied = inputs is not null && inputs.ContainsKey(id);
                var input = supplied ? inputs![id] : last.MissingTicks < 6 ? last.Input.Held : last.Input.Neutral;
                if (supplied && !input.IsValid)
                    throw new ArgumentException("Invalid movement input.");
                if (supplied && input.Sequence > last.Input.Sequence)
                    CarryCommand(id, input);
                commands[id] = new(id, input, supplied ? 0 : Math.Min(7, last.MissingTicks + 1));
                if (players[id].TransitFrozen)
                {
                    players[id] = players[id] with
                    {
                        LastInput = Math.Max(players[id].LastInput, input.Sequence)
                    };
                    continue;
                }
                c.UpdateGroundVelocity();
                SetStance(id, input.Crouch && !players[id].Swimming, true);
                bool onGround = c.GroundState == GroundState.OnGround && Vector3.Dot(c.LinearVelocity - c.GroundVelocity, c.GroundNormal) < .1f;
                bool jump = onGround && input.Jump && input.Sequence > last.Input.Sequence;
                var current = c.LinearVelocity;
                if (players[id].ArrivalMomentumActive)
                {
                    current.X = players[id].ArrivalMomentum.X;
                    current.Z = players[id].ArrivalMomentum.Z;
                    players[id] = players[id] with
                    {
                        ArrivalMomentum = default,
                        ArrivalMomentumActive = false,
                        AirSpeedLimit = Math.Max(Movement.RunSpeed, new Vector2(current.X, current.Z).Length())
                    };
                }
                var support = onGround ? c.GroundVelocity : Vector3.Zero;
                Vector3 velocity = Movement.Accelerate(current - support,
                    Movement.DesiredVelocity(input, players[id].Crouched), onGround, players[id].AirSpeedLimit, c.GroundNormal) + support;
                if (onGround)
                {
                    if (jump) velocity.Y = support.Y + Movement.JumpSpeed;
                    players[id] = players[id] with { AirSpeedLimit = Math.Max(Movement.RunSpeed, new Vector2(velocity.X, velocity.Z).Length()) };
                }
                velocity.Y -= Movement.Gravity * Movement.Dt;
                if (SwimVelocity(id, c.Position, c.LinearVelocity, input, out var swim))
                    velocity = swim;
                if (!HasTerrain(c.Position + velocity * Movement.Dt))
                {
                    velocity.X = 0;
                    velocity.Z = 0;
                    if (onGround && !jump && !players[id].Swimming)
                        velocity.Y = Math.Min(velocity.Y, support.Y - Movement.Gravity * Movement.Dt);
                }
                // ExtendedUpdate preserves this desired velocity for stair detection
                // before applying its own steep-slope cancellation.
                c.LinearVelocity = velocity;
                Native.SG_CharacterStep(c.Handle, system.Handle, allocator, Movement.Dt, CharacterLayer, jump || !onGround && velocity.Y > 0 || players[id].Swimming);
                players[id] = players[id] with
                {
                    Yaw = input.Yaw,
                    Pitch = input.Pitch,
                    LastInput = Math.Max(players[id].LastInput, input.Sequence)
                };
            }
            PreparePropRegions();
            foreach (var b in props.Values)
            {
                var velocity = system.BodyInterface.GetLinearVelocity(b);
                if (!HasTerrain(system.BodyInterface.GetPosition(b) + velocity * Movement.Dt, 4))
                    system.BodyInterface.SetLinearVelocity(b, new Vector3(0, velocity.Y, 0));
            }
            UpdateTravelProxies();
            ApplyCarrying();
            ApplyWaterBuoyancy();
            ApplyEntrySupports();
            uint error = Native.SG_Step(system.Handle, Movement.Dt, allocator, jobs.Handle);
            if (error != 0)
                throw new InvalidOperationException($"Jolt update capacity error: {error}.");
            Tick++;
            foreach (int id in characters.Keys)
                if (!players[id].TransitFrozen)
                    ReadPlayer(id);
        }
    }
    public BodyState[] Bodies() => props.Select(p =>
    {
        var b = system.BodyInterface;
        if (completedProps.TryGetValue(p.Key, out var completed))
            return completed;
        if (dormantProps.TryGetValue(p.Key, out var dormant))
            return dormant;
        return new BodyState(p.Key, b.GetPosition(p.Value), b.GetRotation(p.Value), b.GetLinearVelocity(p.Value), b.GetAngularVelocity(p.Value), !b.IsActive(p.Value));
    }).ToArray();
    public PhysicsSnapshot Snapshot() => new(Tick, Epoch, players.Values.ToArray(), Bodies(), commands.Values.ToArray(), propDefinitions.Values.OrderBy(p => p.Id).ToArray(), CurrentClips(), TravelGate, Carries, EntrySupports);
    public void Acknowledge(int id, long sequence) => players[id] = players[id] with { LastInput = Math.Max(sequence, players[id].LastInput) };
    public void Apply(PhysicsSnapshot snapshot)
    {
        lock (NativeGate)
        {
            Validate(snapshot, World.Definition);
            ReleaseCompletedProps();
            SynchronizeProps(snapshot.Registry ?? World.Definition.Props);
            if (!snapshot.Players.Select(p => p.Id).Order().SequenceEqual(players.Keys))
                throw new InvalidDataException("Physics topology mismatch.");
            var b = system.BodyInterface;
            foreach (var state in snapshot.Bodies)
            {
                var id = props[state.Id];
                if (dormantProps.ContainsKey(state.Id))
                    dormantProps[state.Id] = state;
                bool changed = b.GetPosition(id) != state.Position || b.GetRotation(id) != state.Rotation ||
                    b.GetLinearVelocity(id) != state.LinearVelocity || b.GetAngularVelocity(id) != state.AngularVelocity;
                if (changed)
                {
                    b.SetPositionRotationAndVelocity(id, state.Position, state.Rotation, state.LinearVelocity, state.AngularVelocity);
                    b.InvalidateContactCache(id);
                }
                if (dormantProps.ContainsKey(state.Id))
                {
                    b.SetLinearVelocity(id, Vector3.Zero);
                    b.SetAngularVelocity(id, Vector3.Zero);
                }
                if (state.Sleeping)
                    b.DeactivateBody(id);
                else if (!b.IsActive(id))
                    b.ActivateBody(id);
            }
            foreach (var state in snapshot.Players)
            {
                var c = characters[state.Id];
                var old = players[state.Id];
                bool changed = c.Position != state.Position || c.LinearVelocity != state.Velocity || old.Crouched != state.Crouched || old.Grounded != state.Grounded || old.SupportId != state.SupportId || old.GroundNormal != state.GroundNormal || old.GroundVelocity != state.GroundVelocity;
                if (changed)
                {
                    c.Position = state.Position;
                    SetStance(state.Id, state.Crouched, false);
                    c.LinearVelocity = state.Velocity;
                    b.SetPositionRotationAndVelocity(c.InnerBodyID, state.Position, Quaternion.Identity, state.Velocity, Vector3.Zero);
                    b.InvalidateContactCache(c.InnerBodyID);
                    Refresh(c);
                }
                if (state.TransitFrozen != frozenCharacters.Contains(state.Id))
                    FreezePlayer(state.Id, state.TransitFrozen);
                players[state.Id] = state;
            }
            TravelGate = snapshot.TravelGate;
            SetPropClips(snapshot.Clips ?? []);
            SetCarries(snapshot.Carries ?? []);
            SetEntrySupports(snapshot.EntrySupports ?? []);
            commands.Clear();
            foreach (var cmd in snapshot.Commands)
                commands.Add(cmd.PlayerId, cmd);
            PreparePropRegions();
            Tick = snapshot.Tick;
            Epoch = snapshot.Epoch;
        }
    }
    public static void Validate(PhysicsSnapshot snapshot, PlanetDefinition definition)
    {
        static bool Vec(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && v.LengthSquared() < 1e8f;
        if (snapshot.Registry is { } supplied && (supplied.Length > 64 || supplied.Any(p => p is null)))
            throw new InvalidDataException("Invalid prop registry bounds.");
        static bool FrameValid(GateFrame f) => Vec(f.Centre) && float.IsFinite(f.Radius) && f.Radius is > 0 and <= 100 && float.IsFinite(f.Rotation.LengthSquared()) && Math.Abs(f.Rotation.LengthSquared() - 1) < .01f && Vector3.DistanceSquared(Vector3.Transform(Vector3.UnitY, f.Rotation), Vector3.UnitY) < .0001f;
        if (snapshot.TravelGate is { } gate && !FrameValid(gate) || snapshot.Clips is { } clips && (clips.Length > 64 || clips.Any(c => c is null || !FrameValid(c.Gate) || !Enum.IsDefined(c.Mode) || c.Mode == PropClipMode.Emergence && c.Connection == Guid.Empty || snapshot.Bodies is null || !snapshot.Bodies.Any(b => b.Id == c.Body)) || clips.Select(c => c.Body).Distinct().Count() != clips.Length))
            throw new InvalidDataException("Invalid gate clipping state.");
        if (snapshot.Tick < 0 || snapshot.Epoch <= 0 || snapshot.Players is null || snapshot.Bodies is null || snapshot.Commands is null ||
            snapshot.Players.Length > 8 || snapshot.Players.Select(p => p.Id).Distinct().Count() != snapshot.Players.Length ||
            snapshot.Bodies.Length > 64 || !snapshot.Bodies.Select(b => b.Id).Order().SequenceEqual((snapshot.Registry ?? definition.Props).Select(p => p.Id).Order()) ||
            !snapshot.Commands.Select(c => c.PlayerId).Order().SequenceEqual(snapshot.Players.Select(p => p.Id).Order()))
            throw new InvalidDataException("Invalid physics snapshot topology.");
        if (snapshot.Registry is { } registry && (registry.Count(p => p?.Kind == PropKind.Lantern) > LanternAsset.LocationLimit || registry.Any(p => p is null || p.Id is < 1 or > 64 || !p.Valid || !Vec(p.Position)) || registry.Select(p => p.Id).Distinct().Count() != registry.Length))
            throw new InvalidDataException("Invalid prop registry.");
        foreach (var p in snapshot.Players)
            if (!Enum.IsDefined(p.Surface) || p.Id <= 0 || p.Name is null || p.Name.Length > 24 || !Vec(p.Position) || !Vec(p.Velocity) || !Vec(p.GroundNormal) || !Vec(p.GroundVelocity) || !Vec(p.ArrivalMomentum) ||
                !float.IsFinite(p.AirSpeedLimit) || p.AirSpeedLimit is < 0 or > 10000 || !float.IsFinite(p.Submersion) || p.Submersion is < 0 or > 1 || !float.IsFinite(p.Yaw) || !float.IsFinite(p.Pitch) || Math.Abs(p.Pitch) > 1.55f || Math.Abs(p.Yaw) > MathF.Tau || p.LastInput < 0)
                throw new InvalidDataException("Invalid character state.");
        foreach (var b in snapshot.Bodies)
            if (!Vec(b.Position) || !Vec(b.LinearVelocity) || !Vec(b.AngularVelocity) || !float.IsFinite(b.Rotation.LengthSquared()) || Math.Abs(b.Rotation.LengthSquared() - 1) > .01f)
                throw new InvalidDataException($"Invalid body state: id={b.Id}, tick={snapshot.Tick}, position={b.Position}, linear={b.LinearVelocity}, angular={b.AngularVelocity}, rotationLengthSquared={b.Rotation.LengthSquared()}.");
        if (snapshot.Carries is { } holds && (holds.Length > 8 || holds.Any(c => c is null) || holds.Select(c => c.Player).Distinct().Count() != holds.Length || holds.Select(c => c.Body).Distinct().Count() != holds.Length || holds.Any(c => c is null || !snapshot.Players.Any(p => p.Id == c.Player) || !(snapshot.Registry ?? definition.Props).Any(p => p.Id == c.Body && p.PersistentId == c.PersistentId) || !Vec(c.GrabOffset) || c.GrabOffset.Length() > 2 || !float.IsFinite(c.RelativeRotation.LengthSquared()) || Math.Abs(c.RelativeRotation.LengthSquared() - 1) > .01f || c.BlockedTicks is < 0 or > 18 || !float.IsFinite(c.Distance) || c.Distance < Movement.PhysicsGunMinDistance || c.Distance > Movement.PhysicsGunRange)))
            throw new InvalidDataException("Invalid carrying state.");
        if (snapshot.EntrySupports is { } supports && (supports.Length > 64 || supports.Any(s => s is null || !float.IsFinite(s.Height) || Math.Abs(s.Height) > 10000 || !snapshot.Bodies.Any(b => b.Id == s.Body) || !(snapshot.Clips ?? []).Any(c => c.Body == s.Body)) || supports.Select(s => s.Body).Distinct().Count() != supports.Length))
            throw new InvalidDataException("Invalid entry support state.");
        foreach (var c in snapshot.Commands)
            if (!(c.Input with
            {
                Sequence = Math.Max(1, c.Input.Sequence)
            }).IsValid || c.MissingTicks is < 0 or > 7)
                throw new InvalidDataException("Invalid applied command.");
    }
    public Checkpoint Save()
    {
        lock (NativeGate)
        {
            var handles = characters.Values.Select(c => c.Handle).ToArray();
            var checkpoint = new Checkpoint(this, topology, Native.SG_Save(system.Handle, handles, (uint)handles.Length), Snapshot());
            checkpoints.Add(checkpoint);
            return checkpoint;
        }
    }
    public void Restore(Checkpoint checkpoint)
    {
        lock (NativeGate)
        {
            if (checkpoint.Owner != this || checkpoint.Topology != topology || checkpoint.Handle == 0)
                throw new InvalidOperationException("Checkpoint is expired or belongs to another topology.");
            ReleaseCompletedProps();
            dormantProps.Clear();
            foreach (var state in checkpoint.State.Bodies)
                if (!HasTerrain(state.Position, 4))
                    dormantProps[state.Id] = state;
            TravelGate = checkpoint.State.TravelGate;
            foreach (var b in checkpoint.State.Bodies)
                system.BodyInterface.SetPositionRotationAndVelocity(props[b.Id], b.Position, b.Rotation, b.LinearVelocity, b.AngularVelocity);
            SetPropClips(checkpoint.State.Clips ?? []);
            foreach (var p in checkpoint.State.Players)
            {
                // Jolt's recorder omits virtual/inner-body shape selection. Restore
                // both before contacts and physics state are read from the recorder.
                SetStance(p.Id, p.Crouched, false);
                if (p.TransitFrozen != frozenCharacters.Contains(p.Id))
                    FreezePlayer(p.Id, p.TransitFrozen);
            }
            var handles = characters.Values.Select(c => c.Handle).ToArray();
            if (!Native.SG_Restore(checkpoint.Handle, system.Handle, handles, (uint)handles.Length))
                throw new InvalidOperationException("Jolt checkpoint restore failed.");
            players.Clear();
            foreach (var p in checkpoint.State.Players)
                players.Add(p.Id, p);
            commands.Clear();
            foreach (var c in checkpoint.State.Commands)
                commands.Add(c.PlayerId, c);
            SetCarries(checkpoint.State.Carries ?? []);
            SetEntrySupports(checkpoint.State.EntrySupports ?? []);
            Tick = checkpoint.State.Tick;
            Epoch = checkpoint.State.Epoch;
        }
    }
    public sealed class Checkpoint : IDisposable
    {
        internal PhysicsWorld Owner
        {
            get;
        }
        internal long Topology
        {
            get;
        }
        internal nint Handle
        {
            get;
            private set;
        }
        public PhysicsSnapshot State
        {
            get;
        }
        public long ByteSize
        {
            get;
        }
        internal Checkpoint(PhysicsWorld owner, long topology, nint handle, PhysicsSnapshot state)
        {
            Owner = owner;
            Topology = topology;
            Handle = handle;
            State = state;
            ByteSize = (long)Native.SG_Size(handle);
        }
        public void Dispose()
        {
            if (Handle == 0)
                return;
            Native.SG_Free(Handle);
            Handle = 0;
            Owner.checkpoints.Remove(this);
        }
    }
    public IEnumerable<DebugLine> DebugLines(Vector3 eye, Matrix4x4? viewProjection = null)
    {
        foreach (var line in CachedDebug(eye, viewProjection))
            yield return line;
        foreach (var line in DynamicDebugLines(eye))
            yield return line;
    }
    public IEnumerable<DebugLine> DynamicDebugLines(Vector3 eye)
    {
        foreach (var body in Bodies().Where(b => Vector3.DistanceSquared(b.Position, eye) < 400))
        {
            var hull = PropGeometry.For(propDefinitions[body.Id]);
            foreach (var (a, b) in hull.Edges)
                yield return new(Vector3.Transform(hull.Vertices[a], body.Rotation) + body.Position, Vector3.Transform(hull.Vertices[b], body.Rotation) + body.Position, new(1, .7f, .1f));
        }
        foreach (var (id, c) in characters)
        {
            if (Vector3.DistanceSquared(c.Position, eye) > 400)
                continue;
            Vector3 color = players[id].Grounded ? new(0, 1, 0) : new(1, 0, 0);
            for (int ring = 0; ring < 2; ring++)
                for (int i = 0; i < 20; i++)
                {
                    float a = i * MathF.Tau / 20, b = (i + 1) * MathF.Tau / 20;
                    Vector3 origin = c.Position + Vector3.UnitY * (ring == 0 ? Movement.Radius : Movement.BodyHeight(players[id]) - Movement.Radius);
                    yield return new(origin + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * Movement.Radius, origin + new Vector3(MathF.Cos(b), 0, MathF.Sin(b)) * Movement.Radius, color);
                }
            yield return new(c.GroundPosition, c.GroundPosition + c.GroundNormal, color);
            for (int cap = 0; cap < 2; cap++)
                for (int axis = 0; axis < 2; axis++)
                    for (int i = 0; i < 12; i++)
                    {
                        float a = i * MathF.PI / 12, b = (i + 1) * MathF.PI / 12;
                        Vector3 origin = c.Position + Vector3.UnitY * (cap == 0 ? Movement.Radius : Movement.BodyHeight(players[id]) - Movement.Radius);
                        Vector3 horizontal = axis == 0 ? Vector3.UnitX : Vector3.UnitZ;
                        float sign = cap == 0 ? -1 : 1;
                        yield return new(origin + Movement.Radius * (horizontal * MathF.Cos(a) + Vector3.UnitY * (sign * MathF.Sin(a))),
                            origin + Movement.Radius * (horizontal * MathF.Cos(b) + Vector3.UnitY * (sign * MathF.Sin(b))), color);
                    }
            for (int side = 0; side < 4; side++)
            {
                Vector3 offset = new(MathF.Cos(side * MathF.PI / 2) * Movement.Radius, Movement.Radius, MathF.Sin(side * MathF.PI / 2) * Movement.Radius);
                yield return new(c.Position + offset, c.Position + offset + Vector3.UnitY * (Movement.BodyHeight(players[id]) - 2 * Movement.Radius), color);
            }
        }
    }
    static IEnumerable<DebugLine> BoxLines(Vector3 center, Vector3 half, Quaternion q, Vector3 color)
    {
        Vector3[] corners = Enumerable.Range(0, 8).Select(i => center + Vector3.Transform(new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z), q)).ToArray();
        for (int i = 0; i < 8; i++)
            for (int axis = 0; axis < 3; axis++)
                if ((i & (1 << axis)) == 0)
                    yield return new(corners[i], corners[i | (1 << axis)], color);
    }
    static readonly WorldChanges ChangesFallback = WorldChanges.Empty;
    static Stream CollisionStream(string name) => typeof(PhysicsWorld).Assembly.GetManifestResourceStream("StargateGame.Physics.Collision." + name + ".sgc") ?? throw new InvalidDataException("Missing collision asset: " + name);
    static readonly Lazy<IReadOnlyDictionary<string, (Vector3 A, Vector3 B, Vector3 C)[]>> rockMeshes = new(() => RockScenery.Models.ToDictionary(n => n, ReadCollision, StringComparer.Ordinal));
    static readonly Lazy<IReadOnlyDictionary<string, (Vector3 A, Vector3 B, Vector3 C)[]>> treeMeshes = new(() => TreeScenery.Models.ToDictionary(n => n, ReadCollision, StringComparer.Ordinal));
    static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> TreeTriangles(TerrainChunkData chunk, string preset, WorldChanges? changes = null, string? fixture = null)
    {
        foreach (var tree in TreeScenery.Resolve(chunk, preset, fixture).Where(t => !(changes ?? ChangesFallback).IsRemoved("tree", t.Id)))
            foreach (var t in treeMeshes.Value[tree.Model])
                yield return (Vector3.Transform(t.A, tree.Transform), Vector3.Transform(t.B, tree.Transform), Vector3.Transform(t.C, tree.Transform));
    }
    static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> RockTriangles(TerrainChunkData chunk, string preset, WorldChanges? changes = null)
    {
        foreach (var rock in RockScenery.Resolve(chunk, preset).Where(r => r.Collidable && !(changes ?? ChangesFallback).IsRemoved("rock", r.Id)))
            foreach (var t in rockMeshes.Value[rock.Model])
                yield return (Vector3.Transform(t.A, rock.Transform), Vector3.Transform(t.B, rock.Transform), Vector3.Transform(t.C, rock.Transform));
    }
    static (Vector3 A, Vector3 B, Vector3 C)[] ReadCollision(string name)
    {
        using var r = new BinaryReader(CollisionStream(name));
        if (r.ReadUInt32() != 0x31434753)
            throw new InvalidDataException("Collision header.");
        int nv = r.ReadInt32(), nt = r.ReadInt32();
        if (nv <= 0 || nv > 100000 || nt <= 0 || nt > 100000 || r.BaseStream.Length != 12L + 12L * (nv + nt))
            throw new InvalidDataException("Collision bounds.");
        var v = new Vector3[nv];
        for (int i = 0; i < nv; i++)
            v[i] = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        var t = new (Vector3, Vector3, Vector3)[nt];
        for (int i = 0; i < nt; i++)
            t[i] = (v[r.ReadInt32()], v[r.ReadInt32()], v[r.ReadInt32()]);
        return t;
    }
    static string ComputeContentHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(StargateGame.Building.BuildingPrefabs.Hash));
        hash.AppendData(Encoding.UTF8.GetBytes(WeaponCatalog.Identity));
        hash.AppendData(Encoding.UTF8.GetBytes(LanternAsset.Identity));
        using (var interaction = typeof(DhdInteraction).Assembly.GetManifestResourceStream("DhdInteraction")!)
        using (var memory = new MemoryStream())
        {
            interaction.CopyTo(memory);
            hash.AppendData(memory.ToArray());
        }
        foreach (string name in new[] { "stargate", "dhd" }.Concat(RockScenery.Models).Concat(TreeScenery.Models))
        {
            using var stream = CollisionStream(name);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            hash.AppendData(memory.ToArray());
        }
        foreach (var name in new[] { "TreeBounds", "TreePackBounds", "RealisticTreeBounds" })
            using (var bounds = typeof(TreeScenery).Assembly.GetManifestResourceStream(name)!)
            using (var reader = new StreamReader(bounds))
                hash.AppendData(Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n")));
        using (var bounds = typeof(RockScenery).Assembly.GetManifestResourceStream("RockBounds")!)
        using (var reader = new StreamReader(bounds))
            hash.AppendData(Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("\r\n", "\n")));
        hash.AppendData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(PlanetDefinition.Default, Protocol.Json)));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    static string ComputePhysicsHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var assembly = typeof(PhysicsWorld).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("SimulationSource.")).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            // Git's CRLF conversion must not change compatibility across Windows/Linux.
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\n" + reader.ReadToEnd().Replace("\r\n", "\n")));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public void Dispose()
    {
        lock (NativeGate)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var c in checkpoints.ToArray())
                c.Dispose();
            foreach (var c in characters.Values)
                c.Dispose();
            characters.Clear();
            foreach (var b in ownedBodies)
            {
                system.BodyInterface.RemoveBody(b);
                system.BodyInterface.DestroyBody(b);
            }
            interactionFilter?.Dispose();
            system.Dispose();
            jobs.Dispose();
            Native.SG_AllocatorDestroy(allocator);
            foreach (var filter in transferredFilters)
                filter.Dispose();
            for (int i = shapes.Count - 1; i >= 0; i--)
                shapes[i].Dispose();
        }
    }
}
