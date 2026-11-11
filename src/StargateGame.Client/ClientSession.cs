using System.Diagnostics;
using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class ClientSession : IDisposable
{
    readonly GameConnection connection;
    PredictedWorld prediction;
    readonly int localId;
    PlayerState[] latest = [];
    long sequence, lastTarget;
    readonly RemotePresentation remotePresentation = new();
    long nextPing, pendingPing;
    double rttSeconds;
    readonly PredictionClock clock = new();
    readonly PredictionPresentation presentation = new();
    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    double environmentSeconds;
    long environmentAt = Stopwatch.GetTimestamp();
    public double EnvironmentSeconds => environmentSeconds + Stopwatch.GetElapsedTime(environmentAt).TotalSeconds;
    void AnchorEnvironment(long milliseconds)
    {
        if (milliseconds <= 0)
            return;
        double incoming = milliseconds * .001 + rttSeconds * .5, current = EnvironmentSeconds;
        environmentSeconds = environmentSeconds == 0 || Math.Abs(incoming - current) > 2 ? incoming : current + (incoming - current) * .1;
        environmentAt = Stopwatch.GetTimestamp();
    }
    bool pendingJump;
    public PlanetWorld World => prediction.Simulation.World;
    public PlayerState Local => prediction.Simulation.Player(localId);
    public Vector3 RenderPosition => RenderPrediction().Player.Position;
    public PresentedPrediction RenderPrediction()
    {
        double now = Now;
        return presentation.Sample(prediction.PreviousState, prediction.CurrentState, localId, clock.PredictionTime(now) - 1, now);
    }
    public int PredictionCorrections => prediction.Reconciliations;
    public int PredictionConfirmations => prediction.MatchedSnapshots;
    public double PresentationTick => clock.PredictionTime(Now) - 1;
    public string? Failure
    {
        get;
        private set;
    }
    public int PlayerCount => latest.Length;
    public long ServerTick
    {
        get;
        private set;
    }
    public PhysicsWorld Physics => prediction.Simulation;
    public PhysicsSnapshot LastSnapshot
    {
        get;
        private set;
    }
    public ClientSession(GameConnection connection)
    {
        this.connection = connection;
        localId = connection.Welcome.PlayerId;
        try
        {
            AnchorEnvironment(connection.Welcome.EnvironmentUtcMilliseconds);
            var definition = connection.Welcome.Planet!;
            if (definition.GeneratorVersion != 0)
            {
                PlanetSettings.Validate(definition);
                Terrain = new(definition.SettingsHash);
                foreach (var (c, bytes) in connection.InitialTerrain)
                {
                    var data = ChunkCodec.Decode(bytes);
                    if (c != data.Coordinate)
                        throw new InvalidDataException("Chunk coordinate mismatch.");
                    Terrain.PutDeferred(data);
                }
            }
            else if (System.Text.Json.JsonSerializer.Serialize(definition, Protocol.Json) != System.Text.Json.JsonSerializer.Serialize(PlanetDefinition.LegacyRuntime, Protocol.Json))
                throw new InvalidDataException("Unsupported legacy planet.");
            Developer = connection.Welcome.Developer;
            var state = connection.Welcome.State!;
            LastSnapshot = state;
            ServerTick = state.Tick;
            clock.Anchor(state.Tick, Now, true);
            latest = state.Players;
            remotePresentation.Add(state);
            prediction = new(definition, localId, state, Terrain, connection.Welcome.CollisionChunks, changes: connection.InitialChanges);
            lastTarget = state.Tick;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
    public void Poll()
    {
        SendNextDial();
        try
        {
            PhysicsSnapshot? newest = null;
            int polled = 0;
            while (polled++ < 256 && connection.Peer.Incoming.TryRead(out var message))
            {
                AnchorEnvironment(message.EnvironmentUtcMilliseconds);
                if (message.Version != Protocol.Version)
                    throw new InvalidDataException("Server protocol version changed.");
                if (message.Kind == "fixture-arrival")
                {
                    if (World.Definition.Fixture != "gate-pair" || message.LocationId != World.Definition.LocationId || message.State is not { } baseline)
                        throw new InvalidDataException("Invalid fixture arrival.");
                    newest = null;
                    prediction.CommitTerrain(Physics.TerrainCoordinates.ToArray(), baseline, replayInputs: false);
                    latest = baseline.Players;
                    LastSnapshot = baseline;
                    ServerTick = baseline.Tick;
                    clock.Anchor(baseline.Tick, Now, true);
                    presentation.Clear();
                    remotePresentation.Clear();
                    remotePresentation.Add(baseline);
                    lastTarget = Math.Max(lastTarget, baseline.Tick);
                    pendingCarry = CarryAction.None; pendingPropRotation = default; pendingCarryScroll = 0;
                    pendingGrab = default;
                    long sentSequence = sequence;
                    Arrived(message);
                    sequence = Math.Max(sentSequence, Local.LastInput);
                    continue;
                }
                if (message.Kind == "item-transit")
                {
                    if (message.LocationId == World.Definition.LocationId)
                    {
                        ItemNotice = message.Reason ?? "";
                        ItemNoticeUntil = Now + 6;
                    }
                    continue;
                }
                if (HandleTransit(message))
                    continue;
                if (HandlePlanetDebug(message)) continue;
                if (HandleWeapons(message)) continue;
                if (HandleCharacters(message))
                    continue;
                if (HandleSupplemental(message))
                    continue;
                if (HandleAudio(message))
                    continue;
                if (HandleDialing(message))
                    continue;
                if (message.Kind == "snapshot" && message.LocationId != Guid.Empty && message.LocationId != World.Definition.LocationId)
                    continue;
                if (HandleCables(message)) continue;
                if (HandleBuilding(message))
                    continue;
                if (HandleTerrain(message))
                {
                    if (message.Kind is "terrain-commit" or "location-commit")
                        newest = null;
                    continue;
                }
                if (message.Kind == "reject")
                {
                    Failure = message.Reason;
                    continue;
                }
                if (message.Kind == "pong")
                {
                    if (message.Echo != pendingPing || pendingPing == 0)
                        throw new InvalidDataException("Unexpected time synchronization reply.");
                    double measured = Stopwatch.GetElapsedTime(pendingPing).TotalSeconds;
                    bool firstRoundTrip = rttSeconds == 0;
                    rttSeconds = rttSeconds == 0 ? measured : rttSeconds * .8 + measured * .2;
                    clock.Anchor(message.Tick + measured * 30, Now, firstRoundTrip);
                    clock.SetRoundTrip(rttSeconds, Now, firstRoundTrip);
                    pendingPing = 0;
                    continue;
                }
                if (message.Kind != "snapshot" || message.State is not { } received)
                    throw new InvalidDataException("Unexpected server message.");
                if (received.Epoch >= prediction.Simulation.Epoch && received.Tick > ServerTick && (newest is null || received.Tick > newest.Tick))
                    newest = received;
            }
            // Reconcile once per frame, so a backlog cannot monopolize rendering/input.
            if (newest is { } state)
            {
                double now = Now, renderTick = clock.PredictionTime(now) - 1;
                var before = presentation.Sample(prediction.PreviousState, prediction.CurrentState, localId, renderTick, now);
                long epoch = prediction.Simulation.Epoch;
                int corrections = prediction.Reconciliations + prediction.Resets;
                latest = state.Players;
                remotePresentation.Add(state);
                ServerTick = state.Tick;
                prediction.Accept(state);
                LastSnapshot = state;
                if (epoch != state.Epoch)
                    presentation.Clear();
                else if (corrections != prediction.Reconciliations + prediction.Resets)
                    presentation.Correct(before, PredictionPresentation.Interpolate(prediction.PreviousState, prediction.CurrentState, localId, renderTick), now);
                // Snapshots keep the clock usable until the first ping round trip completes.
                if (rttSeconds == 0)
                    clock.Anchor(state.Tick, now);
            }
            PollSupplemental();
            PrepareCollisionBatch();
            RequestVisualTerrain();
            long pingNow = Stopwatch.GetTimestamp();
            if (pingNow >= nextPing && pendingPing == 0)
            {
                pendingPing = pingNow;
                nextPing = pingNow + Stopwatch.Frequency;
                connection.Peer.Send(new()
                {
                    Kind = "ping",
                    Echo = pingNow
                });
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Failure = "Invalid server simulation: " + ex.Message;
            connection.Peer.Close(Failure);
        }
        if (connection.Peer.Closed)
            Failure ??= connection.Peer.Failure ?? "Server disconnected.";
    }
    public string ItemNotice
    {
        get;
        private set;
    } = "";
    public double ItemNoticeUntil
    {
        get;
        private set;
    }
    public bool ShowItemNotice => Now < ItemNoticeUntil;
    CarryAction pendingCarry;
    Guid pendingGrab;
    Vector2 pendingPropRotation;
    float pendingCarryScroll;
    public void ScrollCarriedProp(float amount)
    {
        if (float.IsFinite(amount)) pendingCarryScroll = Math.Clamp(pendingCarryScroll + amount, -32, 32);
    }
    public void RotateProp(Vector2 delta) => pendingPropRotation = Vector2.Clamp(pendingPropRotation + delta, new(-1), new(1));
    public bool Carrying => Physics.IsCarrying(localId) || pendingCarry is CarryAction.Grab or CarryAction.PhysicsGrab;
    public void Carry(CarryAction action, Guid item = default)
    {
        if (action is CarryAction.Drop or CarryAction.Throw) { pendingPropRotation = default; pendingCarryScroll = 0; }
        pendingCarry = action;
        pendingGrab = item;
    }
    public void Step(float strafe, float forward, float yaw, float pitch, bool walk, bool jump, bool rise = false, bool dive = false, bool crouch = false)
    {
        if (TransitLocked)
        {
            strafe = forward = 0;
            walk = jump = rise = dive = crouch = false;
            yaw = Local.Yaw;
            pitch = Local.Pitch;
        }
        PollCables();
        pendingJump |= jump;
        long target = Math.Min((long)Math.Floor(clock.PredictionTime(Now)), ServerTick + 24);
        if (activationTick > 0 && transfer?.Preparation.SwitchPlanet == true)
            target = Math.Min(target, activationTick - 1);
        if (target <= lastTarget || target <= prediction.Simulation.Tick)
            return;
        if (target - prediction.Simulation.Tick > 180)
        {
            Failure = "Server stopped acknowledging movement.";
            return;
        }
        // Submit every simulated tick, including catch-up ticks after a slow
        // render frame. Previously only the final tick received fresh input.
        for (long tick = Math.Max(lastTarget, prediction.Simulation.Tick) + 1; tick <= target; tick++)
        {
            if (pendingCarry == CarryAction.None && !TransitLocked && Physics.IsCarrying(localId) && Math.Abs(pendingCarryScroll) >= 1)
            {
                pendingCarry = pendingCarryScroll > 0 ? CarryAction.PushFarther : CarryAction.PullCloser;
                pendingCarryScroll -= Math.Sign(pendingCarryScroll);
            }
            if (TransitLocked || !Carrying) pendingCarryScroll = 0;
            var input = new PlayerInput(++sequence, strafe, forward, yaw, pitch, walk, pendingJump, tick, rise, dive, pendingCarry, pendingGrab, crouch, TransitLocked ? default : pendingPropRotation);
            pendingPropRotation = default;
            pendingCarry = CarryAction.None;
            pendingGrab = default;
            pendingJump = false;
            lastTarget = tick;
            var before = Local;
            prediction.Advance(input);
            PredictTransit(before, Local);
            connection.Peer.Send(new()
            {
                Kind = "input",
                Input = input,
                Epoch = prediction.Simulation.Epoch,
                LocationId = World.Definition.LocationId
            });
        }
    }
    public IEnumerable<BodyState> RenderBodies()
    {
        return RenderPrediction().Bodies;
    }
    public IEnumerable<PlayerState> RemotePlayers()
    {
        return remotePresentation.Sample(clock.Estimate(Now) - 6, localId);
    }
    public void Dispose()
    {
        ClearSupplemental();
        preparedWorld?.Dispose();
        prediction.Dispose();
        connection.Dispose();
    }
}
