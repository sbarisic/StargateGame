using StargateGame.Shared;

namespace StargateGame.Physics;

/// <summary>Shared prediction implementation used by the graphical client and headless network tests.</summary>
public sealed class PredictedWorld : IDisposable
{
    readonly PlanetDefinition definition;
    readonly int localId;
    readonly ITerrainSource? terrain;
    ChunkCoordinate[] collisionChunks;
    readonly SortedDictionary<long, PhysicsWorld.Checkpoint> history = [];
    readonly SortedDictionary<long, PlayerInput> pending = [];
    long historyBytes;
    public PhysicsWorld Simulation
    {
        get;
        private set;
    }
    public PhysicsSnapshot PreviousState
    {
        get;
        private set;
    } = null!;
    public PhysicsSnapshot CurrentState => history[Simulation.Tick].State;
    public int HistoryCount => history.Count;
    public long HistoryBytes => historyBytes;
    public int Resets
    {
        get;
        private set;
    }
    public int Reconciliations
    {
        get;
        private set;
    }
    public int MatchedSnapshots
    {
        get;
        private set;
    }
    public PredictedWorld(PlanetDefinition definition, int localId, PhysicsSnapshot baseline, ITerrainSource? terrain = null, ChunkCoordinate[]? collisionChunks = null, PhysicsWorld? prepared = null, WorldChanges? changes = null)
    {
        this.definition = definition;
        this.localId = localId;
        this.terrain = terrain;
        this.collisionChunks = collisionChunks ?? [];
        if (prepared is null)
            Simulation = Create(baseline, changes);
        else
        {
            Simulation = prepared;
            foreach (var p in baseline.Players.OrderBy(p => p.Id))
                Simulation.AddPlayer(p);
            Simulation.Apply(baseline);
        }
        PreviousState = baseline;
        Save();
    }
    PhysicsWorld Create(PhysicsSnapshot snapshot, WorldChanges? changes = null)
    {
        PhysicsWorld.Validate(snapshot, definition);
        var world = new PhysicsWorld(definition, true, terrain, collisionChunks, changes);
        try
        {
            foreach (var p in snapshot.Players.OrderBy(p => p.Id))
                world.AddPlayer(p);
            world.Apply(snapshot);
            return world;
        }
        catch
        {
            world.Dispose();
            throw;
        }
    }
    public void Advance(PlayerInput input)
    {
        if (!input.IsValid || input.TargetTick <= Simulation.Tick || input.TargetTick - Simulation.Tick > 180)
            throw new InvalidOperationException("Prediction input outside history window.");
        pending.Add(input.TargetTick, input);
        ReplayTo(input.TargetTick);
    }
    void ReplayTo(long target)
    {
        while (Simulation.Tick < target)
        {
            Dictionary<int, PlayerInput>? inputs = null;
            if (pending.TryGetValue(Simulation.Tick + 1, out var input))
                inputs = new() { { localId, input } };
            PreviousState = CurrentState;
            Simulation.Step(inputs);
            Save();
        }
    }
    public void Accept(PhysicsSnapshot snapshot)
    {
        if (snapshot.Epoch < Simulation.Epoch)
            return;
        PhysicsWorld.Validate(snapshot, definition);
        if (!snapshot.Players.Any(p => p.Id == localId))
            throw new InvalidDataException("Server removed the local player.");
        long ack = snapshot.Players.Single(p => p.Id == localId).LastInput;
        foreach (var tick in pending.Where(p => p.Key <= snapshot.Tick || p.Value.Sequence <= ack).Select(p => p.Key).ToArray())
            pending.Remove(tick);
        long target = Math.Max(Simulation.Tick, snapshot.Tick);
        // Exact confirmations do not need a rewind. In particular, don't
        // refresh a character's live pushing/standing contacts every 50 ms.
        if (snapshot.Epoch == Simulation.Epoch && history.TryGetValue(snapshot.Tick, out var match) &&
            (match.State.Npcs ?? []).SequenceEqual(snapshot.Npcs ?? []) && match.State.Players.SequenceEqual(snapshot.Players) && match.State.Bodies.SequenceEqual(snapshot.Bodies) && match.State.Commands.SequenceEqual(snapshot.Commands) && match.State.TravelGate == snapshot.TravelGate &&
            (match.State.EntrySupports ?? []).SequenceEqual(snapshot.EntrySupports ?? []) && (match.State.Carries ?? []).SequenceEqual(snapshot.Carries ?? []) && (match.State.Clips ?? []).SequenceEqual(snapshot.Clips ?? []) && (match.State.Registry ?? definition.Props).SequenceEqual(snapshot.Registry ?? definition.Props))
        {
            MatchedSnapshots++;
            return;
        }
        if (snapshot.Epoch != Simulation.Epoch || !(snapshot.Registry ?? definition.Props).SequenceEqual(Simulation.World.Definition.Props))
        {
            ClearHistory();
            Simulation.SynchronizePlayers(snapshot);
            Resets++;
            // A snapshot outside retained history starts a fresh baseline; only bounded future inputs survive.
            if (target - snapshot.Tick > 180)
            {
                target = snapshot.Tick;
                pending.Clear();
            }
        }
        else if (!history.TryGetValue(snapshot.Tick, out var checkpoint))
        {
            ClearHistory();
            Simulation.Apply(snapshot);
            Resets++;
            if (target - snapshot.Tick > 180)
            {
                target = snapshot.Tick;
                pending.Clear();
            }
        }
        else
        {
            Simulation.Restore(checkpoint);
            Simulation.Apply(snapshot);
            Reconciliations++;
            foreach (var tick in history.Keys.Where(t => t >= snapshot.Tick).ToArray())
                RemoveCheckpoint(tick);
        }
        PreviousState = snapshot;
        Save();
        ReplayTo(target);
    }
    public void CommitTerrain(ChunkCoordinate[] chunks, PhysicsSnapshot baseline, bool replayInputs = true)
    {
        long target = replayInputs ? Math.Max(Simulation.Tick, baseline.Tick) : baseline.Tick;
        if (!replayInputs) pending.Clear();
        long ack = baseline.Players.Single(p => p.Id == localId).LastInput;
        ClearHistory();
        foreach (long tick in pending.Where(p => p.Key <= baseline.Tick || p.Value.Sequence <= ack).Select(p => p.Key).ToArray())
            pending.Remove(tick);
        collisionChunks = chunks;
        Simulation.SetTerrain(chunks, preparedOnly: true);
        Simulation.Apply(baseline);
        PreviousState = baseline;
        Save();
        ReplayTo(target);
        Console.WriteLine($"COLLISION prediction commit baseline={baseline.Tick} replayed={target-baseline.Tick} pending={pending.Count}");
        Resets++;
    }
    void Save()
    {
        if (history.ContainsKey(Simulation.Tick))
            RemoveCheckpoint(Simulation.Tick);
        var checkpoint = Simulation.Save();
        history.Add(Simulation.Tick, checkpoint);
        historyBytes += checkpoint.ByteSize;
        while (history.Count > 180 || historyBytes > 32 * 1024 * 1024)
            RemoveCheckpoint(history.Keys.First());
        foreach (var tick in pending.Keys.Where(t => t < Simulation.Tick - 180).ToArray())
            pending.Remove(tick);
    }
    void RemoveCheckpoint(long tick)
    {
        var c = history[tick];
        historyBytes -= c.ByteSize;
        c.Dispose();
        history.Remove(tick);
    }
    void ClearHistory()
    {
        foreach (var c in history.Values)
            c.Dispose();
        history.Clear();
        historyBytes = 0;
    }
    public void Dispose()
    {
        ClearHistory();
        Simulation.Dispose();
    }
}
