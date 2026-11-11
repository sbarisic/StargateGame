using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    sealed class PlanetRuntime(GenerationService generation, PlanetDefinition definition, ChunkCoordinate[] initial) : IDisposable
    {
        public GenerationService Generation
        {
            get;
        } = generation;
        public PlanetDefinition Definition
        {
            get;
            set;
        } = definition;
        public WorldChanges Changes = WorldChanges.Empty;
        public BodyState[] SavedBodies = [];
        public bool BodiesDeferred;
        public Task? ArrivalData;
        public ChunkCoordinate[]? ArrivalChunks;
        public ChunkCoordinate[] InitialCollision
        {
            get;
            set;
        } = initial;
        public static PlanetRuntime CreateFixture(string scene, string preset)
        {
            var definition = GraphicsFixture.Create(scene, preset);
            var store = new TerrainStore(definition.SettingsHash);
            foreach (var c in GraphicsFixture.Chunks)
                store.Put(GraphicsFixture.BuildChunk(definition, c), false);
            return new(new GenerationService(definition.Seed, preset, canonical: store), definition, GraphicsFixture.Chunks)
            {
                Changes = scene == "gate-pair" ? StargateGame.Building.SandboxStations.Create(definition) : WorldChanges.Empty with
                {
                    UniverseId = definition.UniverseId,
                    LocationId = definition.LocationId
                }
            };
        }
        public static async Task<PlanetRuntime> CreateAsync(int seed, string preset, CancellationToken cancellation = default, StargateGame.Persistence.WorldRepository? repository = null, Func<PlanetDefinition, Task<StargateGame.Persistence.SavedLocation>>? materialize = null, int gateCollisionRadius = 3, SettlementSettings? settlements = null, PlanetGenerationSettings? generationSettings = null, int version = PlanetSettings.Version, Action<string>? progress = null)
        {
            var generation = new GenerationService(seed, preset, version: version, settlements: settlements, generation: generationSettings, progress: progress);
            try
            {
                var plan = await generation.Plan.WaitAsync(cancellation);
                var saved = materialize is not null ? await materialize(plan.Definition) : repository is null ? null : await repository.ResolveAsync(plan.Definition);
                if (saved is not null && (saved.Definition.Seed != seed || saved.Definition.Preset != preset || saved.Definition.GeneratorVersion != generation.Version || saved.Definition.SettlementGeneration != (settlements ?? new()) || saved.Definition.Generation != (generationSettings ?? new())))
                {
                    generation.Dispose();
                    generation = new GenerationService(saved.Definition.Seed, saved.Definition.Preset, version: saved.Definition.GeneratorVersion, settlements: saved.Definition.SettlementGeneration, generation: saved.Definition.Generation);
                    plan = await generation.Plan.WaitAsync(cancellation);
                }
                var chunks = ChunkCoordinate.At(plan.Gate.X, plan.Gate.Z).Around(gateCollisionRadius).Concat((saved?.Definition ?? plan.Definition).Spawns.SelectMany(p => ChunkCoordinate.At(p.X, p.Z).Around(1))).Distinct().Order().ToArray();
                var runtime = new PlanetRuntime(generation, saved?.Definition ?? plan.Definition, chunks);
                if (saved is not null)
                {
                    runtime.Changes = saved.Changes;
                    runtime.SavedBodies = saved.Bodies;
                    runtime.BodiesDeferred = gateCollisionRadius == 1 && saved.Bodies.Length > 0;
                    runtime.InitialCollision = PhysicsWorld.IncludeBuildingOwners(saved.Changes, runtime.BodiesDeferred ? chunks : chunks.Concat(saved.Bodies.SelectMany(b => ChunkCoordinate.At(b.Position.X, b.Position.Z).Around(1))));
                }
                progress?.Invoke("Arrival terrain");
                await Task.WhenAll(runtime.InitialCollision.Select(c => generation.RequestAsync(c, cancellation)));
                return runtime;
            }
            catch
            {
                generation.Dispose();
                throw;
            }
        }
        public void Dispose() => Generation.Dispose();
    }
    sealed record Prepared(PlanetRuntime Runtime, ChunkCoordinate[] Chunks, bool Switch, bool Regenerate = false);
    sealed class Transition(long id, Prepared data)
    {
        public long Id = id, ActivateTick;
        public Prepared Data = data;
        public HashSet<int> Ready = [];
        public PhysicsWorld? World;
        public bool ServerReady;
        public DateTime Started = DateTime.UtcNow;
    }
    PlanetRuntime? runtime
    {
        get => context.Runtime;
        set => context.Runtime = value;
    }
    readonly bool devPlanets;
    int developerId;
    long nextTransfer;
    long lastCancelledTransfer
    {
        get => context.LastCancelledTransfer;
        set => context.LastCancelledTransfer = value;
    }
    Task<Prepared>? preparing
    {
        get => context.Preparing;
        set => context.Preparing = value;
    }
    CancellationTokenSource? destinationStop
    {
        get => context.DestinationStop;
        set => context.DestinationStop = value;
    }
    Transition? transition
    {
        get => context.Transition;
        set => context.Transition = value;
    }
    bool commitThisTick
    {
        get => context.CommitThisTick;
        set => context.CommitThisTick = value;
    }
    bool commitWasSwitch
    {
        get => context.CommitWasSwitch;
        set => context.CommitWasSwitch = value;
    }
    Dictionary<string, BodyState[]> savedBodies => context.SavedBodies;
    public PlanetDefinition CurrentPlanet => physics.World.Definition;
    public long CurrentTick => physics.Tick;
    public int CollisionCount => physics.TerrainCoordinates.Count;
    void Join(Session session, string name)
    {
        if (AllSessions.Count(s => s.PlayerId != 0) >= maxPlayers)
        {
            Reject(session, "Server is full.");
            return;
        }
        if (transition is not null)
        {
            Reject(session, "Planet update in progress; reconnect shortly.");
            return;
        }
        session.PreparedRuntime = null;
        session.PreparedCoordinates.Clear();
        int id = ++nextId;
        var occupied = physics.Players.Select(p => p.Position).ToArray();
        var spawn = Enumerable.Range(0, physics.World.Definition.Spawns.Length).Select(physics.World.Spawn)
            .OrderByDescending(p => occupied.Length == 0 ? 0 : occupied.Min(q => Vector3.DistanceSquared(p, q))).First();
        var restored = session.SavedPlayer?.State;
        bool restoredCrouch = false;
        if (restored is { } saved && float.IsFinite(saved.Position.X) && physics.HasTerrain(saved.Position))
        {
            if (physics.CanPlacePlayer(saved.Position, crouched: saved.Crouched))
            {
                spawn = saved.Position;
                restoredCrouch = saved.Crouched;
            }
            else if (physics.CanPlacePlayer(saved.Position, crouched: true))
            {
                spawn = saved.Position;
                restoredCrouch = true;
            }
            else if (!TryPlayerRecovery(context, saved.Position, 0, out spawn))
            {
                Reject(session, "No safe saved-player position is available. Retry after the area is clear.");
                return;
            }
        }
        physics.AddPlayer(new(id, name, spawn, default, restored?.Yaw ?? 0, restored?.Pitch ?? 0, true, 0, Crouched: restoredCrouch));
        if (session.SavedPlayer?.Appearance is { } appearance)
            characterStates[id] = appearance with
            {
                PlayerId = id,
                Sequence = 0,
                Aiming = false,
                Action = "idle",
                StartTick = serverTick
            };
        physics.Epoch++;
        session.PlayerId = id;
        if (devPlanets && session.Loopback && (developerId == 0 || !AllSessions.Any(s => s.PlayerId == developerId)))
            developerId = id;
        session.Peer.Send(new()
        {
            Kind = "welcome",
            EnvironmentUtcMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PlayerId = id,
            PersistentPlayerId = session.SavedPlayer?.Id ?? Guid.Empty,
            Planet = physics.World.Definition,
            State = physics.Snapshot(),
            Tick = physics.Tick,
            PhysicsHash = PhysicsWorld.PhysicsHash,
            ContentHash = PhysicsWorld.ContentHash,
            Developer = catalogue is null ? id == developerId : devPlanets && session.Loopback,
            CollisionChunks = physics.TerrainCoordinates.ToArray()
        });
        Console.WriteLine($"Joined: {name} #{id}");
        SendGateBaseline(session);
    }
    Message Preparation(long id, PlanetRuntime target, ChunkCoordinate[] chunks, bool switching)
    {
        var manifest = chunks.Order().Select(c =>
        {
            if (!target.Generation.Store.TryGetChunk(c, out var data))
                throw new InvalidOperationException("Missing prepared terrain.");
            var encoded = ChunkCodec.Cached(data);
            return new ChunkManifestEntry(c, encoded.Hash, encoded.Bytes.Length);
        }).ToArray();
        return new()
        {
            Kind = "terrain-prepare",
            LocationId = target.Definition.LocationId,
            TransferId = id,
            Planet = target.Definition,
            CollisionChunks = chunks,
            Manifest = manifest,
            SwitchPlanet = switching,
            RegenerateHome = transition is { Data.Regenerate: true } && ReferenceEquals(transition.Data.Runtime, target)
        };
    }
    void SendPreparation(Session s, long id, PlanetRuntime target, ChunkCoordinate[] chunks, bool switching)
    {
        foreach (var part in WorldStateWire.Split(target == runtime ? edit?.Edit.State ?? target.Changes : target.Changes))
            s.WorldQueue.Enqueue(new()
            {
                Kind = "world-state",
                LocationId = target.Definition.LocationId,
                TransferId = id,
                WorldPart = part
            });
        s.WorldQueue.Enqueue(Preparation(id, target, chunks, switching));
        s.PreparedTransfer = id;
        s.PreparedRuntime = target;
        s.PreparedCoordinates = chunks.ToHashSet();
    }
    bool QueueTerrainFetch(Session s, Message m)
    {
        if (s.PreparedRuntime is null || m.TransferId != s.PreparedTransfer || m.Requests is null || m.Requests.Length > 256 ||
            m.Requests.Distinct().Count() != m.Requests.Length || m.Requests.Any(c => !s.PreparedCoordinates.Contains(c)))
            return Reject(s, "Invalid prepared terrain request.");
        foreach (var c in m.Requests.Order())
        {
            s.PreparedCoordinates.Remove(c);
            s.TerrainQueue.Enqueue((m.TransferId, c, s.PreparedRuntime));
        }
        return true;
    }
    bool HandleTerrainMessage(Session s, Message m, out bool result)
    {
        result = true;
        if (m.Kind == "far-request")
        {
            result = HandleFarRequest(s, m);
            return true;
        }
        if (catalogue is not null && m.Kind == "planet-switch")
        {
            s.Peer.Send(new()
            {
                Kind = "terrain-status",
                Reason = "Use a galaxy address in F4 to move only your own session."
            });
            return true;
        }
        if (m.Kind == "terrain-fetch")
        {
            if (m.TransferId > lastCancelledTransfer)
                result = QueueTerrainFetch(s, m);
            return true;
        }
        if (m.Kind == "planet-cancel")
        {
            if (!devPlanets || s.PlayerId != developerId)
            {
                result = Reject(s, "Developer planet control is unavailable.");
                return true;
            }
            if (transition is { Data.Switch: true } cancelled)
            {
                lastCancelledTransfer = cancelled.Id;
                Broadcast(new()
                {
                    Kind = "terrain-cancel",
                    TransferId = cancelled.Id,
                    Reason = "Planet loading cancelled."
                });
                foreach (var peer in sessions)
                {
                    var retained = peer.TerrainQueue.Where(q => q.Transfer != cancelled.Id).ToArray();
                    peer.TerrainQueue.Clear();
                    foreach (var item in retained)
                        peer.TerrainQueue.Enqueue(item);
                    if (peer.PayloadTransfer == cancelled.Id)
                        peer.Payload = null;
                }
                foreach (var peer in sessions)
                {
                    peer.PreparedRuntime = null;
                    peer.PreparedCoordinates.Clear();
                }
                cancelled.World?.Dispose();
                cancelled.Data.Runtime.Dispose();
                transition = null;
            }
            else if (destinationStop is not null)
                destinationStop.Cancel();
            return true;
        }
        if (m.Kind == "planet-switch")
        {
            if (!devPlanets || s.PlayerId != developerId)
            {
                result = Reject(s, "Developer planet control is unavailable.");
                return true;
            }
            if (!PlanetSettings.Presets.Contains(m.Preset))
            {
                result = Reject(s, "Unknown planet preset.");
                return true;
            }
            if (edit is not null || preparing is not null || transition is not null || sessions.Any(peer => peer.JoinName is not null))
            {
                s.Peer.Send(new()
                {
                    Kind = "terrain-status",
                    Reason = "A planet update is already in progress."
                });
                return true;
            }
            destinationStop?.Dispose();
            destinationStop = new();
            context.CollisionPreparation = false;
            preparing = PreparePlanet(m.Seed, m.Preset!, destinationStop.Token);
            Broadcast(new()
            {
                Kind = "terrain-status",
                Reason = "Preparing destination planet..."
            });
            return true;
        }
        if (m.Kind == "terrain-ready")
        {
            if (m.TransferId <= lastCancelledTransfer)
                return true;
            if (transition is null || m.TransferId != transition.Id)
            {
                result = Reject(s, "Unexpected terrain readiness.");
                return true;
            }
            transition.Ready.Add(s.PlayerId);
            return true;
        }
        if (m.Kind == "terrain-request")
        {
            if (runtime is null || m.Requests is null || m.Requests.Length > 16 || m.Requests.Any(c => !c.Valid) || s.VisualPending.Count + m.Requests.Length > 256)
            {
                result = Reject(s, "Invalid terrain request.");
                return true;
            }
            if (transition is { Data.Switch: true })
                return true;
            foreach (var c in m.Requests.Where(c => !GraphicsFixture.IsFixture(runtime.Definition) || GraphicsFixture.Contains(c)))
                if (s.VisualPending.Add(c))
                    s.TerrainQueue.Enqueue((0, c, runtime));
            return true;
        }
        return false;
    }
    async Task<Prepared> PreparePlanet(int seed, string preset, CancellationToken cancellation)
    {
        var retainedBodies = savedBodies.ToDictionary(p => p.Key, p => p.Value);
        var target = await PlanetRuntime.CreateAsync(seed, preset, cancellation, repository, settlements: settlementSettings);
        try
        {
            if (retainedBodies.TryGetValue(target.Definition.Id, out var bodies))
            {
                var chunks = target.InitialCollision.Concat(bodies.SelectMany(b => ChunkCoordinate.At(b.Position.X, b.Position.Z).Around(1))).Distinct().Order().ToArray();
                await Task.WhenAll(chunks.Select(c => target.Generation.RequestAsync(c, cancellation)));
                return new(target, chunks, true);
            }
            cancellation.ThrowIfCancellationRequested();
            return new(target, target.InitialCollision, true);
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }
    async Task<Prepared> PrepareCollision(ChunkCoordinate[] chunks)
    {
        var target = runtime!;
        await Task.WhenAll(chunks.Select(c => target.Generation.RequestAsync(c)));
        return new(target, chunks, false);
    }
    void UpdateTerrain()
    {
        commitThisTick = false;
        if (runtime is null)
            return;
        if (HasLocationTransfer && transition is null && preparing is null)
            return;
        foreach (var joining in sessions.Where(s => s.JoinPending && s.RejectAt == 0))
        {
            if (transition is null && preparing is null)
            {
                joining.JoinPending = false;
                joining.JoinTransfer = ++nextTransfer;
                SendPreparation(joining, joining.JoinTransfer, runtime, physics.TerrainCoordinates.ToArray(), false);
            }
            else if (physics.Tick % 60 == 0)
                joining.Peer.Send(new()
                {
                    Kind = "terrain-wait",
                    Reason = "Waiting for the active planet update..."
                });
        }
        if (physics.Tick % 60 == 0)
            Broadcast(new()
            {
                Kind = "building-metrics",
                BuildingMetrics = new(runtime.Changes.Pieces.Length, runtime.Changes.Revision, edit is null ? 0 : 1, physics.RebuiltBatches, repository?.Pending ?? 0, repository?.LastWriteMilliseconds ?? 0)
            });
        if (physics.Tick % 60 == 0)
            Broadcast(new()
            {
                Kind = "terrain-metrics",
                Metrics = new(runtime.Generation.Pending, runtime.Generation.Store.Count, runtime.Generation.Store.Bytes, runtime.Generation.GenerationMilliseconds, physics.TerrainCoordinates.Count, physics.Epoch, physics.CollisionCacheHits, physics.LongestCollisionCookMilliseconds, runtime.Generation.ChunkGenerationMilliseconds)
            });
        if (preparing?.IsCompleted == true)
        {
            var task = preparing;
            preparing = null;
            context.CollisionPreparation = false;
            bool cancelled = destinationStop?.IsCancellationRequested == true;
            destinationStop?.Dispose();
            destinationStop = null;
            if (cancelled && task.IsCompletedSuccessfully)
                task.Result.Runtime.Dispose();
            if (task.IsCompletedSuccessfully && !cancelled)
            {
                transition = new(++nextTransfer, task.Result);
                if (task.Result.Regenerate) SendHomeProgress("Preparing Collision");
                if (task.Result.Switch)
                    transition.World = new(task.Result.Runtime.Definition, true, task.Result.Runtime.Generation.Store, [], task.Result.Runtime.Changes);
                if (edit is not null)
                    physics.StageChanges(edit.Edit.State);
                foreach (var s in sessions.Where(s => s.PlayerId != 0 && s.RejectAt == 0))
                    SendPreparation(s, transition.Id, task.Result.Runtime, task.Result.Chunks, task.Result.Switch);
            }
            else
            {
                var reason = task.IsCanceled || cancelled ? "Planet loading cancelled." : "Terrain preparation failed: " + task.Exception?.GetBaseException().Message;
                if (homeRegeneration is { Started: true } && homeRegeneration.Source == context) { CancelHomeRegeneration("Failed", reason); Console.Error.WriteLine(task.Exception); }
                if (edit is not null)
                    CancelEdit(reason);
                else
                    Broadcast(new()
                    {
                        Kind = "terrain-status",
                        Reason = reason
                    });
            }
        }
        if (transition is { } change)
        {
            if (!change.ServerReady)
                change.ServerReady = (change.World ?? physics).PrepareTerrainBatch(change.Data.Chunks);
            if (!change.Data.Switch && !physics.Players.Select(p => p.Position).Concat(physics.Bodies().Select(b => b.Position))
                .SelectMany(p => ChunkCoordinate.At(p.X, p.Z).Around(1)).All(change.Data.Chunks.Contains))
            {
                if (edit is not null)
                {
                    CancelEdit("Collision coverage changed; try again.");
                    return;
                }
                lastCancelledTransfer = change.Id;
                Broadcast(new()
                {
                    Kind = "terrain-cancel",
                    TransferId = change.Id,
                    Reason = "Refreshing collision coverage..."
                });
                transition = null;
                return;
            }
            var active = sessions.Where(s => s.PlayerId != 0 && s.RejectAt == 0).ToArray();
            if (change.Data.Regenerate && change.ServerReady && !active.All(s => change.Ready.Contains(s.PlayerId)) && homeRegeneration?.Phase != "Preparing Client") SendHomeProgress("Preparing Client");
            if (change.ActivateTick == 0 && change.ServerReady && active.All(s => change.Ready.Contains(s.PlayerId)))
            {
                if (!SaveHomeReady(change)) return;
                change.ActivateTick = NextTick + 12;
                Broadcast(new()
                {
                    Kind = "terrain-activate",
                    TransferId = change.Id,
                    Tick = change.ActivateTick
                });
            }
            if (change.ActivateTick == 0 && (DateTime.UtcNow - change.Started).TotalSeconds > 45)
                foreach (var s in active.Where(s => !change.Ready.Contains(s.PlayerId)))
                    Reject(s, "Terrain loading timed out.");
            if (change.ActivateTick != NextTick)
                return;
            if (!CommitEditReady())
                return;
            if (change.Data.Switch && !change.Data.Regenerate && repository is not null)
            {
                switchSave ??= repository.SaveCurrentBodiesAsync(runtime.Definition.LocationId, physics.Bodies(), physics.World.Definition.Props);
                if (!switchSave.IsCompleted)
                {
                    freezeTick = true;
                    return;
                }
                if (!switchSave.IsCompletedSuccessfully)
                {
                    Broadcast(new()
                    {
                        Kind = "terrain-cancel",
                        TransferId = change.Id,
                        Reason = "Saving old planet failed."
                    });
                    change.World?.Dispose();
                    change.Data.Runtime.Dispose();
                    transition = null;
                    switchSave = null;
                    return;
                }
                switchSave = null;
            }
            commitThisTick = true;
            commitWasSwitch = change.Data.Switch;
            if (change.Data.Switch)
            {
                if (!change.Data.Regenerate) savedBodies[runtime.Definition.Id] = physics.Bodies();
                var old = physics.Snapshot();
                var target = change.Data.Runtime;
                var next = change.World!;
                change.World = null;
                next.SetTerrain(change.Data.Chunks);
                var players = old.Players.Select((p, i) => p with { Position = next.World.Spawn(i), Velocity = Vector3.Zero, Grounded = true, SupportId = 0, GroundVelocity = Vector3.Zero, GroundNormal = Vector3.UnitY }).ToArray();
                foreach (var p in players.OrderBy(p => p.Id))
                    next.AddPlayer(p);
                var bodies = change.Data.Regenerate ? next.Bodies() : target.Definition.LocationId == runtime.Definition.LocationId ? physics.Bodies() : target.SavedBodies.Length > 0 ? target.SavedBodies : savedBodies.GetValueOrDefault(target.Definition.Id) ?? next.Bodies();
                next.Apply(new(old.Tick, Math.Max(old.Epoch, nextLocationEpoch) + 1, players, bodies, players.Select(p => new AppliedCommand(p.Id, new PlayerInput(p.LastInput, 0, 0, p.Yaw, p.Pitch, false, false), 7)).ToArray()));
                if (change.Data.Regenerate && context != homeContext)
                {
                    var source = context;
                    foreach (var player in players) source.Physics.RemovePlayer(player.Id);
                    foreach (var peer in source.Sessions.ToArray()) { source.Sessions.Remove(peer); homeContext.Sessions.Add(peer); }
                    source.Transition = null; source.CommitThisTick = source.CommitWasSwitch = false;
                    homeContext.Physics.Dispose(); homeContext.Runtime?.Dispose();
                    context = homeContext; transition = change; commitThisTick = commitWasSwitch = true;
                }
                else { physics.Dispose(); runtime.Dispose(); }
                physics = next;
                runtime = target;
                if (change.Data.Regenerate) savedBodies.Clear();
                foreach (var s in sessions)
                {
                    s.Inputs.Clear();
                    s.TerrainQueue.Clear();
                    s.Payload = null;
                    s.VisualPending.Clear();
                    if (change.Data.Regenerate)
                    {
                        s.WorldQueue.Clear(); s.FarPending.Clear();
                        s.BuildResults.Clear(); s.LookupCommand = null; s.LookupReceipt = null; s.CheckedRequest = Guid.Empty;
                        s.DhdResults.Clear(); s.GalaxyLookup = null; s.GalaxyLookupCommand = null; s.ResolvedTarget = null; s.ResolvedRequest = Guid.Empty;
                    }
                }
            }
            else
            {
                physics.SetTerrain(change.Data.Chunks);
                physics.Epoch++;
            }
        }
        if (!HomeDebugBusy && !GraphicsFixture.IsFixture(runtime.Definition) && transition is null && preparing is null && (physics.Players.Any() || physics.NeedsPropSimulation) && physics.Tick % 30 == 0 && !sessions.Any(s => s.JoinName is not null))
        {
            var desired = PhysicsWorld.IncludeBuildingOwners(runtime.Changes, physics.Players.SelectMany(p => ChunkCoordinate.At(p.Position.X, p.Position.Z).Around(3))
                .Concat(physics.Bodies().SelectMany(b => ChunkCoordinate.At(b.Position.X, b.Position.Z).Around(1)))
                // Unattended simulation extends collision for moving items. Do
                // not shrink a prepared arrival/join region without any player.
                .Concat(physics.Players.Any() ? [] : physics.TerrainCoordinates)
                .Concat(ChunkCoordinate.At(runtime.Definition.Entities[0].Position.X, runtime.Definition.Entities[0].Position.Z).Around(1)).Distinct()).Order().ToArray();
            if (desired.Length > 0 && !desired.SequenceEqual(physics.TerrainCoordinates.Order()))
            { context.CollisionPreparation = true; preparing = PrepareCollision(desired); }
            foreach (var c in physics.Players.SelectMany(p => ChunkCoordinate.At(p.Position.X, p.Position.Z).Around(4)).Distinct())
                _ = runtime.Generation.RequestAsync(c).ContinueWith(t =>
                {
                    _ = t.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);
            runtime.Generation.ReleaseExcept(desired.ToHashSet());
        }
    }
    void FinishTerrainTick()
    {
        if (!commitThisTick || transition is null)
            return;
        Broadcast(new()
        {
            Kind = "terrain-commit",
            EnvironmentUtcMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TransferId = transition.Id,
            SwitchPlanet = commitWasSwitch,
            Planet = runtime!.Definition,
            CollisionChunks = physics.TerrainCoordinates.ToArray(),
            State = physics.Snapshot(),
            Tick = physics.Tick
        });
        Console.WriteLine($"Terrain commit: {runtime.Definition.Id}, epoch={physics.Epoch}, colliders={physics.TerrainCoordinates.Count}");
        foreach (var s in sessions)
        {
            s.PreparedRuntime = null;
            s.PreparedCoordinates.Clear();
        }
        if (edit is { } completed)
        {
            if (completed.Session is { } builder)
            {
                PublishBuildSound(completed);
                BuildReply(builder, new(completed.Command.RequestId, true, "World change saved.", runtime.Changes.Revision));
            }
            else if (completed.Command.Action == "hazard")
                FinishKawooshEdit(completed);
            edit = null;
            savingEdit = null;
        }
        bool regenerated = transition.Data.Regenerate;
        transition = null;
        if (regenerated) FinishHomeRegeneration();
    }
    void Broadcast(Message m)
    {
        foreach (var s in sessions.Where(s => s.PlayerId != 0 && s.RejectAt == 0))
            s.Peer.Send(m with
            {
                LocationId = physics.World.Definition.LocationId
            });
    }
    void PumpTerrain(Session s)
    {
        while (s.WorldQueue.TryPeek(out var worldMessage))
        {
            if (!s.Peer.SendBulk(worldMessage))
                return;
            s.WorldQueue.Dequeue();
        }
        for (int packet = 0; packet < 2; packet++)
        {
            if (s.Payload is null)
            {
                if (!s.TerrainQueue.TryPeek(out var request))
                    return;
                if (!request.Runtime.Generation.Store.TryGetChunk(request.Coordinate, out var data))
                {
                    _ = request.Runtime.Generation.RequestAsync(request.Coordinate).ContinueWith(t =>
                    {
                        _ = t.Exception;
                    }, TaskContinuationOptions.OnlyOnFaulted);
                    return;
                }
                s.TerrainQueue.Dequeue();
                var encoded = ChunkCodec.Cached(data);
                s.Payload = encoded.Bytes;
                s.PayloadHash = encoded.Hash;
                s.Offset = 0;
                s.PayloadCoordinate = request.Coordinate;
                s.PayloadTransfer = request.Transfer;
                s.PayloadSettings = request.Runtime.Definition.SettingsHash;
                s.PayloadLocation = request.Runtime.Definition.LocationId;
            }
            int count = Math.Min(32768, s.Payload.Length - s.Offset);
            var m = new Message
            {
                Kind = "terrain-chunk",
                LocationId = s.PayloadLocation,
                TransferId = s.PayloadTransfer,
                SettingsHash = s.PayloadSettings,
                Manifest = [new(s.PayloadCoordinate, s.PayloadHash, s.Payload.Length)],
                Chunk = new(s.PayloadCoordinate, s.PayloadHash, s.Offset, s.Payload.AsSpan(s.Offset, count).ToArray())
            };
            if (!s.Peer.SendBulk(m))
                return;
            s.Offset += count;
            if (s.Offset == s.Payload.Length)
            {
                s.Payload = null;
                if (s.PayloadTransfer == 0)
                    s.VisualPending.Remove(s.PayloadCoordinate);
            }
        }
    }
}
