using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StargateGame.Physics;
using StargateGame.Shared;

namespace StargateGame.Server;

public sealed partial class GameServer : IAsyncDisposable
{
    readonly TcpListener listener;
    readonly int maxPlayers;
    readonly ConcurrentQueue<TcpClient> accepted = new();
    List<Session> sessions => context.Sessions;
    IEnumerable<Session> AllSessions => contexts.SelectMany(c => c.Sessions);
    readonly CancellationTokenSource stop = new();
    PhysicsWorld physics
    {
        get => context.Physics;
        set => context.Physics = value;
    }
    readonly List<LocationContext> contexts = [];
    LocationContext context = new();
    LocationContext homeContext = null!;
    long serverTick, nextLocationEpoch;
    readonly SettlementSettings settlementSettings;
    public GameServer(IPAddress bind, int port, int maxPlayers, int? seed = null, string preset = "temperate", bool devPlanets = false, string? saveDirectory = null, string universe = "default", string? cataloguePath = null, string? fixture = null, SettlementSettings? settlements = null, bool allowTestIdentities = false, PlanetGenerationSettings? generationSettings = null, long? testSeed = null)
    {
        if (testSeed.HasValue && (!allowTestIdentities || !devPlanets || !IPAddress.IsLoopback(bind) || saveDirectory is null)) throw new ArgumentException("Deterministic test persistence requires an explicit private test server.");
        this.allowTestIdentities = allowTestIdentities;
        settlementSettings = settlements ?? new();
        settlementSettings.Validate();
        listener = new(bind, port);
        this.maxPlayers = maxPlayers;
        this.devPlanets = devPlanets;
        try
        {
            if (fixture is not null)
            {
                runtime = PlanetRuntime.CreateFixture(fixture, preset);
                physics = new(runtime.Definition, true, runtime.Generation.Store, runtime.InitialCollision, runtime.Changes);
            }
            else if (seed is { } value)
            {
                repository = new StargateGame.Persistence.WorldRepository(saveDirectory ?? StargateGame.Persistence.WorldRepository.DefaultDirectory, universe, testSeed);
                var retained = cataloguePath is null ? null : repository.ReadGalaxyAsync().GetAwaiter().GetResult();
                if (retained is not null)
                {
                    var saved = repository.LoadAsync(retained.HomeLocation).GetAwaiter().GetResult();
                    runtime = PlanetRuntime.CreateAsync(saved.Definition.Seed, saved.Definition.Preset, materialize: _ => Task.FromResult(saved), settlements: saved.Definition.SettlementGeneration, generationSettings: saved.Definition.Generation, version: saved.Definition.GeneratorVersion).GetAwaiter().GetResult();
                }
                else
                    runtime = PlanetRuntime.CreateAsync(value, preset, repository: repository, settlements: settlementSettings, generationSettings: generationSettings, progress: Console.WriteLine).GetAwaiter().GetResult();
                if (cataloguePath is not null)
                    runtime.Definition = repository.SetHomeTimeAsync(runtime.Definition.LocationId).GetAwaiter().GetResult();
                physics = new(runtime.Definition, true, runtime.Generation.Store, runtime.InitialCollision, runtime.Changes);
                if (runtime.SavedBodies.Length > 0)
                    physics.Apply(physics.Snapshot() with
                    {
                        Bodies = runtime.SavedBodies
                    });
            }
            else
                physics = new(PlanetDefinition.LegacyRuntime);
            homeContext = context;
            contexts.Add(context);
            InitializeFixtureGates();
            nextLocationEpoch = physics.Epoch;
            if (cataloguePath is not null && fixture is null)
                InitializeGalaxy(cataloguePath);
        }
        catch
        {
            context.Physics?.Dispose();
            runtime?.Dispose();
            catalogue?.Dispose();
            if (repository is not null)
            {
                try
                {
                    repository.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }
            throw;
        }
    }
    Task? acceptTask, simulation;
    public Task Completion => simulation ?? Task.CompletedTask;
    readonly double[] tickTimes = new double[4096];
    long measuredTicks;
    double worstTick;
    double lastTickMilliseconds, lastTravelMilliseconds;
    int nextId;
    bool disposed;
    long NextTick => physics.Tick + 1;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public void Start()
    {
        if (maxPlayers is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(maxPlayers));
        listener.Start(16);
        acceptTask = AcceptLoop();
        simulation = RunLoop();
        Console.WriteLine($"Listening on {listener.LocalEndpoint}; planet={physics.World.Definition.Id}; maxPlayers={maxPlayers}; Jolt 60 Hz");
    }
    async Task AcceptLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(stop.Token);
                if (accepted.Count < 32)
                    accepted.Enqueue(socket);
                else
                    socket.Dispose();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }
    async Task RunLoop()
    {
        var clock = Stopwatch.StartNew();
        double next = clock.Elapsed.TotalSeconds;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                double now = clock.Elapsed.TotalSeconds;
                int catchup = 0;
                while (now >= next && catchup++ < 6)
                {
                    Tick();
                    next += Movement.Dt;
                }
                if (now - next > .5)
                {
                    Console.Error.WriteLine($"Simulation overloaded; resynchronizing wall clock. tick={lastTickMilliseconds:F2}ms travel={lastTravelMilliseconds:F2}ms exitSearch={exitSearchMilliseconds:F2}ms pendingTravel={travels.Count}");
                    next = now;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(.001, next - clock.Elapsed.TotalSeconds)), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (var state in cableLocations.Values)
                if (state.Saving is not null)
                    try { await state.Saving; } catch (Exception error) { Console.Error.WriteLine("Cable shutdown save: " + error.Message); }
            foreach (var session in AllSessions)
                session.Peer.Dispose();
        }
    }
    void Tick()
    {
        long started = Stopwatch.GetTimestamp();
        serverTick++;
        context = homeContext;
        while (!HomeDebugBusy && accepted.TryDequeue(out var socket))
        {
            if (AllSessions.Count() >= maxPlayers + 8)
            {
                socket.Dispose();
                continue;
            }
            // Peer starts its reader immediately and can close a failed connection.
            // Capture the endpoint before starting it, including aborted handshakes.
            try
            {
                if (socket.Client.RemoteEndPoint is not IPEndPoint endpoint)
                {
                    socket.Dispose();
                    continue;
                }
                bool loopback = IPAddress.IsLoopback(endpoint.Address);
                sessions.Add(new(new Peer(socket), loopback));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                socket.Dispose();
            }
        }
        TickAdministration();
        TickPlanetDebug();
        TickIdentity();
        TickGalaxy();
        TickFixtureGates();
        long travelStarted = Stopwatch.GetTimestamp();
        TickTravel();
        lastTravelMilliseconds = Stopwatch.GetElapsedTime(travelStarted).TotalMilliseconds;
        foreach (var location in contexts.ToArray())
        {
            context = location;
            TickLocation();
        }
        foreach (var location in contexts)
            nextLocationEpoch = Math.Max(nextLocationEpoch, location.Physics.Epoch);
        context = homeContext;
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        lastTickMilliseconds = elapsed;
        tickTimes[measuredTicks++ % tickTimes.Length] = elapsed;
        worstTick = Math.Max(worstTick, elapsed);
    }
    string TickTiming()
    {
        var samples = tickTimes.Take((int)Math.Min(measuredTicks, tickTimes.Length)).Order().ToArray();
        return samples.Length == 0 ? "no samples" : $"server tick p95={samples[Math.Min(samples.Length - 1, (int)(samples.Length * .95))]:F3} ms, peak={worstTick:F3} ms";
    }
    void TickLocation()
    {
        if (!context.CollisionReady)
            return;
        // Keep both endpoints active after ownership commits. Without this the
        // last arriving item froze at the exit before its first physics step.
        // After closing, moving props continue until they naturally sleep.
        if (sessions.Count == 0 && transition is null && preparing is null && !TravelBusy(context) && !UnattendedPhysicsActive(context))
            return;
        TickCables();
        TickWeapons();
        PollBuildLookups();
        PollGalaxyLookups();
        Dictionary<int, PlayerInput> inputs = [];
        foreach (var session in sessions.ToArray())
        {
            if (session.RejectAt != 0)
            {
                if (NextTick >= session.RejectAt)
                    session.Peer.Close("Rejected.");
            }
            else
            {
                int messages = 0;
                while (session.Peer.Incoming.TryRead(out var message))
                {
                    if (++messages > 128)
                    {
                        Reject(session, "Too many messages per tick.");
                        break;
                    }
                    if (!Handle(session, message))
                        break;
                }
                if (session.PlayerId == 0 && Stopwatch.GetElapsedTime(session.Created).TotalSeconds > 60)
                    Reject(session, "Join timed out.");
                if (session.PlayerId != 0 && session.RejectAt == 0)
                {
                    foreach (var late in session.Inputs.Keys.TakeWhile(t => t < NextTick).ToArray())
                    {
                        physics.Acknowledge(session.PlayerId, session.Inputs[late].Sequence);
                        session.Inputs.Remove(late);
                    }
                    if (session.Inputs.Remove(NextTick, out var input))
                        inputs.Add(session.PlayerId, input);
                }
            }
            if ((session.Peer.Closed || session.RejectAt != 0 && session.PlayerId != 0) && !travels.Values.Any(t => t.Session == session))
            {
                if (session.PlayerId != 0)
                {
                    SavePlayer(session);
                    Console.WriteLine($"Left: {physics.Player(session.PlayerId).Name}; {session.Peer.Failure}");
                    physics.RemovePlayer(session.PlayerId);
                    inputs.Remove(session.PlayerId);
                    session.PlayerId = 0;
                    physics.Epoch++;
                }
                if (session.Peer.Closed)
                {
                    StopClientPreload(session, "disconnected");
                    sessions.Remove(session);
                }
            }
        }
        freezeTick = false;
        UpdateTerrain();
        if (freezeTick)
        {
            // Persistence/travel may defer this exact tick. Do not lose inputs
            // already dequeued before the boundary was deferred.
            foreach (var peer in sessions)
                if (inputs.TryGetValue(peer.PlayerId,out var held)) peer.Inputs.TryAdd(NextTick,held);
            foreach (var peer in sessions.Where(s => !s.Peer.Closed))
                PumpTerrain(peer);
            return;
        }
        if (commitThisTick && commitWasSwitch)
            inputs.Clear();
        physics.EnableSoundObservations();
        physics.Step(inputs);
        if (fixtureGates.Count > 0)
            ObserveFixtureTravel();
        else
        {
            ObserveKawoosh();
            ObserveTravel();
        }
        PublishSoundContacts();
        FinishTerrainTick();
        CheckpointCrates();
        if (serverTick % 300 == 0)
            foreach (var s in sessions.Where(s => s.PlayerId != 0))
                SavePlayer(s);
        foreach (var s in sessions.Where(s => !s.Peer.Closed))
        {
            PumpTerrain(s);
            PumpSupplemental(s);
        }
        if (physics.Tick % 3 != 0)
            return;
        var snapshot = new Message { Kind = "snapshot", Characters = CharacterBaseline(), LocationId = physics.World.Definition.LocationId, GateTick = serverTick, EnvironmentUtcMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Tick = physics.Tick, State = physics.Snapshot() };
        foreach (var session in sessions.Where(s => s.PlayerId != 0 && s.RejectAt == 0))
            session.Peer.Send(snapshot);
    }
    bool Handle(Session session, Message message)
    {
        if (message.Version != Protocol.Version)
            return Reject(session, "Protocol version mismatch.");
        if (session.PlayerId == 0)
        {
            if (session.JoinName is not null && message.Kind == "terrain-fetch")
                return QueueTerrainFetch(session, message);
            if (session.JoinName is not null && message.Kind == "terrain-progress" && message.TransferId == session.JoinTransfer)
                return true;
            if (session.SavedPlayer is not null && session.JoinTransfer > 0 && session.JoinName is not null && message.Kind == "terrain-ready" && message.TransferId == session.JoinTransfer)
            {
                Join(session, session.JoinName);
                session.JoinName = null;
                return true;
            }

            return HandleIdentity(session, message);
        }
        if (message.Kind is "terrain-fetch" or "terrain-ready" && message.TransferId != 0 && message.TransferId <= session.CancelledRelocation)
            return true;
        if (HandlePlanetDebug(session, message)) return true;
        if (HomeDebugBusy && message.Kind is "dhd" or "build" or "character-action" or "planet-switch" or "galaxy-move" or "planet-cancel")
        {
            // A request can already be in flight when regeneration is accepted.
            // Acknowledge rejection so cancellation cannot leave client queues stuck.
            if (message.Build is { } build)
                BuildReply(session, new(build.RequestId, false, "Planet update in progress.", CurrentChanges.Revision));
            if (message.Dhd is { } dhd)
                session.Peer.Send(new() { Kind = "dhd-result", DhdResult = new(dhd.RequestId, false, "Planet update in progress.", dhd.Revision) });
            return true;
        }
        if (HandleTravel(session, message))
            return true;
        if (physics.Player(session.PlayerId).TransitFrozen && message.Kind is "dhd" or "build" or "character-action" or "planet-switch" or "galaxy-move")
            return true;
        if (message.LocationId != Guid.Empty && message.LocationId != physics.World.Definition.LocationId)
        {
            return true;
        }
        if (HandleGateMessage(session, message))
            return true;
        if (HandleWeapons(session, message)) return true;
        if (HandleCharacter(session, message))
            return true;
        if (HandleCables(session, message)) return true;
        if (message.Kind == "build")
            return HandleBuild(session, message);
        if (HandleTerrainMessage(session, message, out bool result))
            return result;
        if (message.Kind == "ping")
        {
            // Loading can pause simulation ticks and bunch legitimate ping/pong delivery.
            // Bound requests by monotonic wall time, allowing a small catch-up burst.
            long now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(session.PingWindowStarted, now).TotalSeconds >= 1)
            {
                session.PingWindowStarted = now;
                session.PingsInWindow = 0;
            }
            if (++session.PingsInWindow > 4)
                return Reject(session, "Ping rate exceeded.");
            session.Peer.Send(new()
            {
                Kind = "pong",
                EnvironmentUtcMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Tick = physics.Tick,
                Echo = message.Echo
            });
            return true;
        }
        if (message.Kind != "input" || message.Input is not { } input || !input.IsValid || input.Sequence != session.LastSequence + 1 ||
            input.TargetTick <= session.LastTarget || input.TargetTick > NextTick + 30 || input.TargetTick < 0)
            return Reject(session, "Invalid or out-of-sequence movement input.");
        if (session.Inputs.Count >= 120)
            return Reject(session, "Too many pending movement inputs.");
        session.LastSequence = input.Sequence;
        session.LastTarget = input.TargetTick;
        bool previousGeometry = message.Epoch == session.PreviousGeometryEpoch &&
            session.GeometryLocation == physics.World.Definition.LocationId &&
            physics.Epoch == session.PreviousGeometryEpoch + 1 && NextTick <= session.GeometryInputDeadline;
        if (message.Epoch == physics.Epoch) session.PreviousGeometryEpoch = -1;
        if (message.Epoch != physics.Epoch && !previousGeometry && runtime is not null || input.TargetTick < NextTick)
            physics.Acknowledge(session.PlayerId, input.Sequence);
        else
            session.Inputs.Add(input.TargetTick, input);
        return true;
    }
    bool Reject(Session session, string reason)
    {
        Console.WriteLine($"Rejected #{session.PlayerId}: {reason} (tick {physics.Tick}, seq {session.LastSequence}, target {session.LastTarget})");
        session.Peer.Send(new()
        {
            Kind = "reject",
            Reason = reason
        });
        session.RejectAt = NextTick + 15;
        return false;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        stop.Cancel();
        destinationStop?.Cancel();
        listener.Stop();
        try
        {
            if (acceptTask is not null)
                await acceptTask;
            if (simulation is not null)
                await simulation;
        }
        finally
        {
            while (!HomeDebugBusy && accepted.TryDequeue(out var socket))
                socket.Dispose();
            foreach (var state in cableLocations.Values)
                if (state.Saving is not null)
                    try { await state.Saving; } catch (Exception error) { Console.Error.WriteLine("Cable shutdown save: " + error.Message); }
            foreach (var session in AllSessions)
                session.Peer.Dispose();
            foreach (var session in AllSessions)
                await session.Peer.Completion;
            foreach (var c in contexts)
            {
                context = c;
                foreach (var s in c.Sessions.Where(s => s.PlayerId != 0))
                    SavePlayer(s);
                c.DestinationStop?.Cancel();
                if (repository is not null && c.Runtime is not null && !TravelBusy(c))
                    await repository.SaveCurrentBodiesAsync(c.Runtime.Definition.LocationId, c.Runtime.BodiesDeferred ? c.Runtime.SavedBodies : c.Physics.Bodies(), c.Physics.World.Definition.Props);
                c.Physics.Dispose();
                c.Runtime?.Dispose();
                c.Transition?.World?.Dispose();
                if (c.Transition is { Data.Switch: true })
                    c.Transition.Data.Runtime.Dispose();
                if (c.Preparing is not null)
                {
                    try
                    {
                        var p = await c.Preparing;
                        if (p.Switch)
                            p.Runtime.Dispose();
                    }
                    catch
                    {
                    }
                }
                c.DestinationStop?.Dispose();
            }
            foreach (var load in locationLoads.Values)
            {
                try
                {
                    (await load).Dispose();
                }
                catch
                {
                }
            }
            foreach (var cancellation in loadCancellation.Values)
                cancellation.Dispose();
            if (repository is not null)
                await repository.DisposeAsync();
            catalogue?.Dispose();
            stop.Dispose();
        }
    }
    sealed class Session(Peer peer, bool loopback)
    {
        public long PreviousGeometryEpoch = -1, GeometryInputDeadline;
        public Guid GeometryLocation;
        public Peer Peer
        {
            get;
        } = peer;
        public bool Loopback
        {
            get;
        } = loopback;
        public string? JoinName;
        public byte[]? IdentityKey;
        public StargateGame.Shared.IdentityChallenge? Challenge;
        public long ChallengeStarted;
        public string? DeviceKey;
        public Task<StargateGame.Persistence.SavedPlayer>? IdentityWork;
        public StargateGame.Persistence.SavedPlayer? SavedPlayer;
        public Task? PlayerWrite;
        public ChunkCoordinate[]? ResumeChunks;
        public Task? ResumeTerrain;
        public Task<StargateGame.Persistence.GateAssociation?>? ResumeAssociation;
        public bool JoinPending;
        public long JoinTransfer, PreparedTransfer;
        public PlanetRuntime? PreparedRuntime;
        public HashSet<ChunkCoordinate> PreparedCoordinates = [];
        public Queue<(long Transfer, ChunkCoordinate Coordinate, PlanetRuntime Runtime)> TerrainQueue
        {
            get;
        } = [];
        public byte[]? Payload;
        public int Offset;
        public string PayloadHash = "", PayloadSettings = "";
        public long PayloadTransfer;
        public Guid PayloadLocation;
        public long CancelledRelocation;
        public ChunkCoordinate PayloadCoordinate;
        public HashSet<ChunkCoordinate> VisualPending
        {
            get;
        } = [];

        public Queue<Message> WorldQueue
        {
            get;
        } = [];
        public Queue<Message> Supplemental = [];
        public Guid PreloadId, PreloadLocation;
        public long PreloadRevision = -1, PreloadAway;
        public Task<Message[]>? PreloadWork;
        public CancellationTokenSource? PreloadStop;
        public Queue<FarTileKey> FarRequests = [];
        public HashSet<FarTileKey> FarPending = [];
        public Task<StargateGame.WorldGen.ChunkCodec.EncodedChunk>? FarWork;
        public FarTileKey FarCoordinate;
        public Guid FarLocation;
        public byte[]? FarPayload;
        public string FarHash = "";
        public int FarOffset;
        public Dictionary<Guid, BuildResult> BuildResults
        {
            get;
        } = [];
        public long LastBuild;
        public BuildCommand? LookupCommand;
        public Task<BuildResult?>? LookupReceipt;
        public Guid CheckedRequest;
        public Dictionary<Guid, DhdResult> DhdResults = [];
        public Task<StargateGame.Persistence.GalaxyTarget>? GalaxyLookup, MoveLookup;
        public DhdCommand? GalaxyLookupCommand;
        public StargateGame.Persistence.GalaxyTarget? ResolvedTarget;
        public Guid ResolvedRequest;
        public long Created
        {
            get;
        } = Stopwatch.GetTimestamp();
        public int PlayerId;
        public SortedDictionary<long, PlayerInput> Inputs
        {
            get;
        } = [];
        public long LastSequence, LastTarget, RejectAt, PingWindowStarted;
        public int PingsInWindow;
    }
}
