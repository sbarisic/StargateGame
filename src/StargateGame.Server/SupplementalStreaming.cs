using System.Numerics;
using StargateGame.Building;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Server;

public sealed partial class GameServer
{
    readonly Dictionary<(Guid, FarTileKey), Task<ChunkCodec.EncodedChunk>> farTiles = [];
    static readonly SemaphoreSlim farWorkers = new(2);
    Task<ChunkCodec.EncodedChunk> FarBytes(PlanetRuntime world, FarTileKey c)
    {
        var key = (world.Definition.LocationId, c);
        if (farTiles.TryGetValue(key, out var cached))
        {
            if (!cached.IsFaulted && !cached.IsCanceled)
                return cached;
            _ = cached.Exception;
            farTiles.Remove(key);
        }
        // Encoded tiles are immutable. Bound the reusable cache independently of client interest.
        long bytes = farTiles.Values.Where(t => t.IsCompletedSuccessfully).Sum(t => (long)t.Result.Bytes.Length);
        foreach (var old in farTiles.Where(p => p.Value.IsCompleted).Select(p => p.Key).ToArray())
        {
            if (bytes < 64 * 1024 * 1024 && farTiles.Count < 256)
                break;
            if (farTiles.Remove(old, out var task) && task.IsCompletedSuccessfully)
                bytes -= task.Result.Bytes.Length;
        }
        return farTiles[key] = Task.Run(async () =>
        {
            await farWorkers.WaitAsync(stop.Token);
            try
            {
                var plan = await world.Generation.Plan.WaitAsync(world.Generation.Cancellation);
                var bytes = new FarTile(2, c.Origin, RegionalMesher.Build(plan, c, world.Generation.Cancellation), [], []) { Level = c.Level }.Encode();
                return new ChunkCodec.EncodedChunk(bytes, ChunkCodec.Hash(bytes));
            }
            finally
            {
                farWorkers.Release();
            }
        }, stop.Token);
    }
    bool HandleFarRequest(Session s, Message m)
    {
        if (m.LocationId != runtime?.Definition.LocationId || GraphicsFixture.IsFixture(runtime!.Definition))
            return true;
        if (s.FarLocation != m.LocationId)
        {
            s.FarRequests.Clear();
            s.FarPending.Clear();
            s.FarWork = null;
            s.FarPayload = null;
            s.FarLocation = m.LocationId;
        }
        if (m.FarRequests is null || m.FarRequests.Length > 8 || m.FarRequests.Any(c => !c.Valid) || s.FarPending.Count + m.FarRequests.Length > 16)
            return Reject(s, "Invalid far scene request.");
        foreach (var c in m.FarRequests)
            if (s.FarPending.Add(c))
                s.FarRequests.Enqueue(c);
        return true;
    }
    void StopClientPreload(Session s, string reason)
    {
        if (s.PreloadId == Guid.Empty)
            return;
        s.Peer.Send(new()
        {
            Kind = "preload-cancel",
            PreloadId = s.PreloadId,
            Reason = reason
        });
        s.PreloadStop?.Cancel();
        s.PreloadStop?.Dispose();
        s.PreloadStop = null;
        if (s.PreloadWork is { } abandoned)
            _ = abandoned.ContinueWith(t =>
            {
                _ = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
        s.PreloadId = Guid.Empty;
        s.PreloadLocation = Guid.Empty;
        s.PreloadRevision = -1;
        s.PreloadWork = null;
        s.Supplemental.Clear();
        s.PreloadAway = 0;
    }
    void TickClientPreloads()
    {
        foreach (var source in contexts)
            foreach (var s in source.Sessions.Where(s => s.PlayerId != 0 && !s.Peer.Closed))
            {
                LocationContext? destination = null;
                var gate = source.Gate;
                if (gate is { Incoming: false })
                {
                    Guid id = Guid.Empty;
                    if (gate.Phase == GatePhase.Dialing && gate.Owner == s.PlayerId)
                        id = source.Prefetch?.Target?.LocationId ?? Guid.Empty;
                    else if (gate.Phase is GatePhase.Preparing or GatePhase.Opening or GatePhase.Open && links.TryGetValue(gate.ConnectionId, out var link) && link.Source == source)
                    {
                        float distance = Vector3.Distance(source.Physics.Player(s.PlayerId).Position, source.Runtime!.Definition.Entities.Single(e => e.Model == "stargate").Position);
                        if (distance <= 64)
                        {
                            id = link.Target.LocationId;
                            s.PreloadAway = 0;
                        }
                        else if (s.PreloadLocation == link.Target.LocationId)
                        {
                            if (distance <= 80)
                                s.PreloadAway = 0;
                            else if (s.PreloadAway == 0)
                                s.PreloadAway = serverTick;
                            if (s.PreloadAway == 0 || serverTick - s.PreloadAway < 600)
                                id = link.Target.LocationId;
                        }
                    }
                    destination = contexts.FirstOrDefault(c => id != Guid.Empty && c.Association?.LocationId == id);
                }
                if (destination?.Runtime is not { } target)
                {
                    StopClientPreload(s, gate?.Phase == GatePhase.Closing || gate?.Phase == GatePhase.Idle && string.IsNullOrEmpty(gate.Status) ? "closed" : "cancelled");
                    continue;
                }
                if (s.PreloadLocation == target.Definition.LocationId && s.PreloadRevision == target.Changes.Revision)
                    continue;
                StopClientPreload(s, "replaced");
                var idNew = Guid.NewGuid();
                s.PreloadId = idNew;
                s.PreloadLocation = target.Definition.LocationId;
                s.PreloadRevision = target.Changes.Revision;
                var changes = target.Changes;
                var planet = target.Definition;
                Guid sourceId = source.Runtime!.Definition.LocationId;
                s.PreloadStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var cancel = s.PreloadStop.Token;
                s.PreloadWork = Task.Run(async () =>
                {
                    var gatePosition = planet.Entities.Single(e => e.Model == "stargate").Position;
                    var region = ChunkCoordinate.At(gatePosition.X, gatePosition.Z).Around(1).ToHashSet();
                    var chunks = PhysicsWorld.IncludeBuildingOwners(changes, region);
                    var data = await Task.WhenAll(chunks.Select(c => target.Generation.RequestAsync(c, cancel)));
                    cancel.ThrowIfCancellationRequested();
                    var encoded = data.Select(d => (d.Coordinate, Data: ChunkCodec.Cached(d))).ToArray();
                    var selected = changes.Pieces.Where(p => BuildingGeometry.Chunks(BuildingGeometry.Bounds(BuildingGeometry.Mesh(changes, p))).Any(region.Contains)).Select(p => p.Id).ToHashSet();
                    // Retain the complete parent chain for safe validation of imported attachments.
                    bool added;
                    do
                    {
                        added = false;
                        foreach (var p in changes.Pieces.Where(p => selected.Contains(p.Id) && p.Parent != Guid.Empty))
                            added |= selected.Add(p.Parent);
                    }
                    while (added);
                    var pieces = changes.Pieces.Where(p => selected.Contains(p.Id)).ToArray();
                    var local = changes with
                    {
                        Pieces = pieces,
                        Structures = changes.Structures.Where(b => pieces.Any(p => p.StructureId == b.Id)).ToArray(),
                        Removed = changes.Removed.Where(r => chunks.Contains(r.Chunk)).ToArray()
                    };
                    List<Message> messages = [new() { Kind = "preload-begin", PreloadId = idNew, SourceLocationId = sourceId, LocationId = planet.LocationId, Planet = planet, ContentRevision = changes.Revision, TransferId = 1, CollisionChunks = chunks, Manifest = encoded.Select(e => new ChunkManifestEntry(e.Coordinate, e.Data.Hash, e.Data.Bytes.Length)).ToArray() }];
                    foreach (var part in WorldStateWire.Split(local))
                        messages.Add(new()
                        {
                            Kind = "preload-world",
                            PreloadId = idNew,
                            LocationId = planet.LocationId,
                            WorldPart = part
                        });
                    foreach (var e in encoded)
                        for (int offset = 0; offset < e.Data.Bytes.Length; offset += 32768)
                            messages.Add(new()
                            {
                                Kind = "preload-chunk",
                                PreloadId = idNew,
                                LocationId = planet.LocationId,
                                Chunk = new(e.Coordinate, e.Data.Hash, offset, e.Data.Bytes.AsSpan(offset, Math.Min(32768, e.Data.Bytes.Length - offset)).ToArray())
                            });
                    return messages.ToArray();
                }, cancel);
            }
    }
    void PumpSupplemental(Session s)
    {
        // Collision/join traffic wins, but ordinary source scenery must not
        // starve the imminent destination while the player stands at the DHD.
        if (s.WorldQueue.Count != 0 || s.JoinPending || s.Payload is not null && s.PayloadTransfer != 0 || s.TerrainQueue.Any(q => q.Transfer != 0))
            return;
        if (s.PreloadWork?.IsCompleted == true)
        {
            if (s.PreloadWork.IsCompletedSuccessfully)
                foreach (var m in s.PreloadWork.Result)
                    s.Supplemental.Enqueue(m);
            else
            {
                _ = s.PreloadWork.Exception;
                StopClientPreload(s, "failed");
            }
            s.PreloadWork = null;
        }
        if (s.Supplemental.Count != 0)
        {
            for (int packet = 0; packet < 4 && s.Supplemental.TryPeek(out var next); packet++)
            {
                if (!s.Peer.SendBulk(next))
                    break;
                s.Supplemental.Dequeue();
            }
            return;
        }
        if (s.TerrainQueue.Count != 0 || s.Payload is not null)
            return;
        if (runtime is null)
            return;
        if (s.FarLocation != runtime.Definition.LocationId)
        {
            s.FarRequests.Clear();
            s.FarPending.Clear();
            s.FarWork = null;
            s.FarPayload = null;
            s.FarLocation = runtime.Definition.LocationId;
        }
        if (s.FarPayload is null)
        {
            if (s.FarWork is null)
            {
                if (!s.FarRequests.TryDequeue(out var c))
                    return;
                s.FarCoordinate = c;
                s.FarWork = FarBytes(runtime, c);
            }
            if (!s.FarWork.IsCompleted)
                return;
            if (!s.FarWork.IsCompletedSuccessfully)
            {
                _ = s.FarWork.Exception;
                s.FarPending.Remove(s.FarCoordinate);
                s.FarWork = null;
                return;
            }
            s.FarPayload = s.FarWork.Result.Bytes;
            s.FarHash = s.FarWork.Result.Hash;
            s.FarWork = null;
            s.FarOffset = 0;
        }
        var payload = s.FarPayload;
        int count = Math.Min(32768, payload.Length - s.FarOffset);
        if (s.Peer.SendBulk(new()
        {
            Kind = "far-chunk",
            LocationId = s.FarLocation,
            SettingsHash = runtime.Definition.SettingsHash,
            FarLevel = s.FarCoordinate.Level,
            Manifest = [new(s.FarCoordinate.Origin, s.FarHash, payload.Length)],
            Chunk = new(s.FarCoordinate.Origin, s.FarHash, s.FarOffset, payload.AsSpan(s.FarOffset, count).ToArray())
        }))
        {
            s.FarOffset += count;
            if (s.FarOffset == payload.Length)
            {
                s.FarPayload = null;
                s.FarPending.Remove(s.FarCoordinate);
            }
        }
    }
}
