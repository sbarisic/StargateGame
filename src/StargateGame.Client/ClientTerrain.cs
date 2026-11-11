using System.Diagnostics;
using System.Numerics;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed partial class ClientSession
{
    public TerrainStore? Terrain
    {
        get;
        private set;
    }
    public bool Developer
    {
        get;
        private set;
    }
    public TerrainMetrics? TerrainCounters
    {
        get;
        private set;
    }
    public string TerrainStatus
    {
        get;
        private set;
    } = "";
    public bool TransitionPending => transfer is not null;
    public long TerrainBytesReceived
    {
        get;
        private set;
    }
    public double TerrainDecodeMilliseconds
    {
        get;
        private set;
    }
    TerrainChunkData DecodeTerrain(byte[] bytes)
    {
        var watch = Stopwatch.StartNew();
        var data = ChunkCodec.Decode(bytes);
        TerrainDecodeMilliseconds += watch.Elapsed.TotalMilliseconds;
        return data;
    }
    TerrainTransfer? transfer;
    TerrainStore? targetStore;
    PhysicsWorld? preparedWorld;
    readonly Dictionary<ChunkCoordinate, TerrainTransfer> visualTransfers = [];
    readonly HashSet<ChunkCoordinate> requested = [];
    long activationTick, nextVisualRequest, lastCancelledTransfer;
    bool readySent, dataReady;
    public void CancelPlanetLoad()
    {
        if (Developer)
            connection.Peer.Send(new()
            {
                Kind = "planet-cancel"
            });
    }
    public void SwitchPlanet(int seed, string preset)
    {
        if (!Developer)
        {
            TerrainStatus = "Developer controls are unavailable.";
            return;
        }
        if (TransitionPending)
        {
            TerrainStatus = "A planet update is already in progress.";
            return;
        }
        connection.Peer.Send(new()
        {
            Kind = "planet-switch",
            Seed = seed,
            Preset = preset
        });
        TerrainStatus = "Preparing destination...";
    }
    bool HandleTerrain(Message message)
    {
        if (message.LocationId != Guid.Empty && message.LocationId != World.Definition.LocationId &&
            (message.Kind is "terrain-metrics" || message.Kind == "terrain-chunk" && message.TransferId == 0))
            return true;
        bool relocating = message.Kind == "location-commit";
        if (relocating)
        {
            activationTick = message.State?.Tick ?? 0;
            message = message with
            {
                Kind = "terrain-commit"
            };
        }
        switch (message.Kind)
        {
            case "terrain-metrics":
                if (message.Metrics is not { } metrics || metrics.Pending is < 0 or > WorldDomain.ChunkCount*WorldDomain.ChunkCount || metrics.Cached is < 0 or > WorldDomain.ChunkCount*WorldDomain.ChunkCount || metrics.CacheBytes < 0 || !double.IsFinite(metrics.GenerationMilliseconds))
                    throw new InvalidDataException("Invalid terrain counters.");
                TerrainCounters = metrics;
                return true;
            case "terrain-status":
                TerrainStatus = message.Reason ?? "";
                return true;
            case "terrain-prepare":
                if (message.TransferId <= lastCancelledTransfer)
                    return true;
                if (transfer is not null)
                    throw new InvalidDataException("Overlapping terrain preparations.");
                transfer = new(message);
                readySent = false;
                dataReady = false;
                if (incomingWorld?.State is not { } changes)
                    throw new InvalidDataException("Missing prepared world changes.");
                if (changes.LocationId != message.Planet!.LocationId || changes.UniverseId != message.Planet.UniverseId)
                    throw new InvalidDataException("World identity mismatch.");
                StargateGame.Building.BuildingRules.ValidateState(changes);
                preparedChanges = changes;
                incomingWorld = null;
                if (!message.SwitchPlanet)
                    prediction.Simulation.StageChanges(changes);
                targetStore = message.SwitchPlanet ? PromotePreloadStore(message.Planet!, changes.Revision) : Terrain ?? throw new InvalidDataException("Missing terrain source.");
                TerrainStatus = message.SwitchPlanet ? "Loading destination terrain..." : "Preparing nearby collision...";
                foreach (var entry in message.Manifest!)
                    if (targetStore.TryLoad(entry.Coordinate, out var cached))
                        transfer.UseCached(entry.Coordinate, ChunkCodec.Cached(cached).Bytes);
                var missing = message.Manifest.Where(m => !transfer.Completed.ContainsKey(m.Coordinate)).Select(m => m.Coordinate).ToArray();
                if (missing.Length > 0)
                    connection.Peer.Send(new()
                    {
                        Kind = "terrain-fetch",
                        TransferId = message.TransferId,
                        Requests = missing
                    });
                FinishTerrainData();
                return true;
            case "terrain-chunk":
                if (message.Chunk is not { } packet)
                    throw new InvalidDataException("Missing terrain fragment.");
                if (packet.Data is null)
                    throw new InvalidDataException("Missing chunk data.");
                TerrainBytesReceived += packet.Data.Length;
                if (message.TransferId == 0)
                {
                    if (Terrain is null || message.SettingsHash != Terrain.SettingsHash)
                        return true;
                    if (!visualTransfers.TryGetValue(packet.Coordinate, out var receiver))
                    {
                        if (visualTransfers.Count >= 16 || message.Manifest is null || message.Manifest.Length != 1)
                            throw new InvalidDataException("Invalid visual terrain transfer.");
                        receiver = new(new()
                        {
                            TransferId = 1,
                            Planet = World.Definition,
                            Manifest = message.Manifest,
                            CollisionChunks = [packet.Coordinate]
                        });
                        visualTransfers.Add(packet.Coordinate, receiver);
                    }
                    receiver.Add(packet);
                    if (receiver.Complete)
                    {
                        Terrain.PutDeferred(DecodeTerrain(receiver.Completed[packet.Coordinate]));
                        visualTransfers.Remove(packet.Coordinate);
                    }
                    return true;
                }
                if (message.TransferId <= lastCancelledTransfer)
                    return true;
                if (transfer is null || message.TransferId != transfer.Preparation.TransferId)
                    throw new InvalidDataException("Unexpected collision transfer.");
                transfer.Add(packet);
                FinishTerrainData();
                return true;
            case "terrain-cancel":
                incomingWorld = null;
                lastCancelledTransfer = Math.Max(lastCancelledTransfer, message.TransferId);
                if (transfer?.Preparation.TransferId == message.TransferId)
                {
                    prediction.Simulation.CancelChanges();
                    preparedWorld?.Dispose();
                    preparedWorld = null;
                    transfer = null;
                    targetStore = null;
                    activationTick = 0;
                    dataReady = readySent = false;
                    TerrainStatus = message.Reason ?? "";
                }
                return true;
            case "terrain-activate":
                if (transfer is null || message.TransferId != transfer.Preparation.TransferId || !readySent || message.Tick <= ServerTick)
                    throw new InvalidDataException("Invalid terrain activation.");
                activationTick = message.Tick;
                return true;
            case "terrain-commit":
                if (transfer is null || message.TransferId != transfer.Preparation.TransferId || message.State is null || !readySent || (!relocating && activationTick == 0) || message.State.Tick != activationTick || message.State.Epoch <= prediction.Simulation.Epoch)
                    throw new InvalidDataException("Invalid terrain commit.");
                if (transfer.Preparation.RegenerateHome) { PlanetVisualPending = true; BuildPending = false; BuildStatus = ""; LastBuildResult = null; pendingCarry = CarryAction.None; pendingGrab = default; }
                var prep = transfer.Preparation;
                var baseline = message.State;
                if (prep.SwitchPlanet)
                {
                    ClearDial();
                    if (Destination is { } oldCache && oldCache.Begin.LocationId != prep.Planet!.LocationId)
                    {
                        oldCache.Cancel();
                        Destination = null;
                        PromotedDestination = null;
                    }
                    prediction.Dispose();
                    Terrain = targetStore;
                    prediction = new(prep.Planet!, localId, baseline, Terrain, prep.CollisionChunks, preparedWorld);
                    preparedWorld = null;
                    requested.Clear();
                    visualTransfers.Clear();
                    ResetFarScene();
                    StreamingFocus = null;
                }
                else
                    prediction.CommitTerrain(prep.CollisionChunks!, baseline);
                latest = baseline.Players;
                LastSnapshot = baseline;
                ServerTick = baseline.Tick;
                clock.Anchor(baseline.Tick, Now, true);
                presentation.Clear();
                remotePresentation.Clear();
                remotePresentation.Add(baseline);
                lastTarget = relocating ? baseline.Tick : Math.Max(lastTarget, baseline.Tick);
                pendingJump = false;
                if (relocating)
                    sequence = baseline.Players.Single(p => p.Id == localId).LastInput;
                activationTick = 0;
                transfer = null;
                targetStore = null;
                TerrainStatus = "";
                Arrived(message);
                return true;
        }
        return false;
    }
    void FinishTerrainData()
    {
        if (transfer is null || !transfer.Complete || dataReady)
            return;
        foreach (var pair in transfer.Completed)
            if (!targetStore!.TryGetChunk(pair.Key, out var existing) || ChunkCodec.Cached(existing).Hash != ChunkCodec.Hash(pair.Value))
                targetStore.PutDeferred(DecodeTerrain(pair.Value));
        if (transfer.Preparation.SwitchPlanet)
        {
            if (PromotedDestination is { PhysicsReady: true, Physics: not null } cache && cache.Begin.ContentRevision == preparedChanges!.Revision)
            {
                preparedWorld = cache.Physics;
                cache.Physics = null;
                cache.PhysicsReady = false;
                PreloadPhysicsPromotions++;
                preparedWorld.StageChanges(preparedChanges);
            }
            else
                preparedWorld = new(transfer.Preparation.Planet!, true, targetStore, [], preparedChanges);
        }
        dataReady = true;
        TerrainStatus = "Preparing terrain collision...";
    }
    void PrepareCollisionBatch()
    {
        if (transfer is null || !dataReady || readySent)
            return;
        if (!(preparedWorld ?? prediction.Simulation).PrepareTerrainBatch(transfer.Preparation.CollisionChunks!))
            return;
        if (preparedWorld is not null)
            preparedWorld.SetTerrain(transfer.Preparation.CollisionChunks!);
        if (Transit is { } transit)
        {
            if (!TransitReady)
                return;
            connection.Peer.Send(new()
            {
                Kind = "transit-ready",
                Transit = transit,
                TransferId = transfer.Preparation.TransferId
            });
        }
        else
            connection.Peer.Send(new()
            {
                Kind = "terrain-ready",
                TransferId = transfer.Preparation.TransferId
            });
        readySent = true;
        TerrainStatus = "Terrain ready; synchronizing simulation...";
    }
    void RequestVisualTerrain()
    {
        if (Terrain is null || Stopwatch.GetTimestamp() < nextVisualRequest || transfer is not null || requested.Count(c => !Terrain.TryGetChunk(c, out _)) >= 8)
            return;
        nextVisualRequest = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
        var position = StreamingFocus ?? Local.Position;
        PollSupplemental();
        var keep = ChunkCoordinate.All.Where(c => StreamingSettings.Distance(c, position) <= Streaming.Radius + 64).Concat(Physics.TerrainCoordinates).ToHashSet();
        if (transfer?.Preparation.CollisionChunks is { } protectedChunks)
            keep.UnionWith(protectedChunks);
        Terrain.Retain(keep);
        foreach (var c in requested.Where(c => !keep.Contains(c)).ToArray())
            requested.Remove(c);
        var needed = ChunkCoordinate.All.Where(c => !GraphicsFixture.IsFixture(World.Definition) || GraphicsFixture.Contains(c)).Where(c => StreamingSettings.Distance(c, position) <= Streaming.Radius + 64 && !requested.Contains(c) && !Terrain.TryGetChunk(c, out _))
            .OrderBy(c => StreamingSettings.Distance(c, position)).Take(8).ToArray();
        if (needed.Length > 0)
        {
            foreach (var c in needed)
                requested.Add(c);
            connection.Peer.Send(new()
            {
                Kind = "terrain-request",
                LocationId = World.Definition.LocationId,
                Requests = needed
            });
        }
        if (!GraphicsFixture.IsFixture(World.Definition))
            RequestFarScene(position);
    }
}
