using System.Diagnostics;
using System.Numerics;
using StargateGame.Building;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.WorldGen;

namespace StargateGame.Client;

sealed record StreamingSettings(int Radius = 384)
{
    public const long PreloadCpuLimit = 128L * 1024 * 1024, PreloadGpuLimit = 64L * 1024 * 1024;
    public static float Distance(ChunkCoordinate c, Vector3 p) => Vector2.Distance(new(p.X, p.Z), new(c.X * 64 + 32, c.Z * 64 + 32));
}
enum PreloadStage
{
    Requested, Receiving, Preparing, Ready, Stale
}
sealed class DestinationCache(Message begin)
{
    public Message Begin
    {
        get;
    } = begin;
    public PreloadStage Stage = PreloadStage.Requested;
    public TerrainTransfer Transfer = new(begin);
    public WorldStateReceiver Buildings = new();
    public TerrainStore Store = new(begin.Planet!.SettingsHash);
    public Dictionary<ChunkCoordinate, TerrainMeshData> Meshes = [];
    public WorldChanges? Changes;
    public Dictionary<ChunkCoordinate, PhysicsWorld.CollisionInput> CollisionInputs = [];
    public Task? Work;
    public long Bytes, Received;
    public double Milliseconds, Expires = double.PositiveInfinity;
    public bool Capped;
    public PhysicsWorld? Physics;
    public bool PhysicsReady;
    public readonly CancellationTokenSource Cancellation = new();
    public void Cancel()
    {
        Cancellation.Cancel();
        Physics?.Dispose();
        Physics = null;
        PhysicsReady = false;
        if (Work is { } task)
            _ = task.ContinueWith(t =>
            {
                _ = t.Exception;
            }, TaskContinuationOptions.OnlyOnFaulted);
    }

}

sealed partial class ClientSession
{
    public Vector3? StreamingFocus
    {
        get;
        set;
    }
    public StreamingSettings Streaming
    {
        get;
        set;
    } = new();
    public DestinationCache? Destination
    {
        get;
        private set;
    }
    public DestinationCache? PromotedDestination
    {
        get;
        private set;
    }
    public long PreloadBytesReceived
    {
        get;
        private set;
    }
    public int PreloadPromotions
    {
        get;
        private set;
    }
    public int PreloadPhysicsPromotions
    {
        get;
        private set;
    }
    public long FarBytesReceived
    {
        get;
        private set;
    }
    long farDecodeTicks;
    public double FarDecodeMilliseconds => Interlocked.Read(ref farDecodeTicks) * 1000d / Stopwatch.Frequency;
    public readonly Dictionary<FarTileKey, FarTile> FarScene = [];
    readonly Dictionary<FarTileKey, TerrainTransfer> farReceivers = [];
    readonly Dictionary<FarTileKey, Task<FarTile>> farDecoding = [];
    readonly HashSet<FarTileKey> farRequested = [];
    readonly HashSet<Guid> obsoletePreloads = [];
    public int FarDecodeFailures
    {
        get;
        private set;
    }
    bool HandleSupplemental(Message m)
    {
        if (m.Kind == "preload-begin")
        {
            if (m.PreloadId == Guid.Empty || m.SourceLocationId != World.Definition.LocationId || m.Planet?.LocationId != m.LocationId || m.LocationId == World.Definition.LocationId)
                return true;
            if (obsoletePreloads.Contains(m.PreloadId))
                return true;
            if (m.Manifest is null || m.Manifest.Sum(e => (long)e.Length) > StreamingSettings.PreloadCpuLimit)
                throw new InvalidDataException("Preload manifest exceeds budget.");
            Destination?.Cancel();
            Destination = new(m)
            {
                Stage = PreloadStage.Receiving
            };
            return true;
        }
        if (m.Kind == "preload-cancel")
        {
            obsoletePreloads.Add(m.PreloadId);
            if (obsoletePreloads.Count > 128)
                obsoletePreloads.Remove(obsoletePreloads.First());
            if (Destination?.Begin.PreloadId != m.PreloadId)
                return true;
            if (m.Reason == "closed" && Destination.Stage == PreloadStage.Ready)
                Destination.Expires = Now + 30;
            else
            {
                Destination.Cancel();
                Destination.Stage = PreloadStage.Stale;
                Destination = null;
            }
            return true;
        }
        if (m.Kind is "preload-world" or "preload-chunk")
        {
            var cache = Destination;
            if (cache is null || cache.Begin.PreloadId != m.PreloadId || cache.Begin.LocationId != m.LocationId)
                return true;
            if (m.Kind == "preload-world")
                cache.Buildings.Add(m.WorldPart ?? throw new InvalidDataException("Missing preload buildings."));
            else
            {
                var packet = m.Chunk ?? throw new InvalidDataException("Missing preload chunk.");
                cache.Transfer.Add(packet);
                cache.Received += packet.Data.Length;
                PreloadBytesReceived += packet.Data.Length;
            }
            if (cache.Transfer.Complete && cache.Buildings.State is { } changes && cache.Work is null)
            {
                if (changes.LocationId != cache.Begin.LocationId || changes.Revision != cache.Begin.ContentRevision)
                    throw new InvalidDataException("Preload changes mismatch.");
                BuildingRules.ValidateState(changes);
                cache.Changes = changes;
                cache.Stage = PreloadStage.Preparing;
                cache.Work = Task.Run(() =>
                {
                    var watch = Stopwatch.StartNew();
                    // Include compressed transfer buffers until preparation completes.
                    cache.Bytes = cache.Transfer.Completed.Values.Sum(b => (long)b.Length) + 4L * 1024 * 1024;
                    foreach (var pair in cache.Transfer.Completed)
                    {
                        cache.Cancellation.Token.ThrowIfCancellationRequested();
                        // Worst-case canonical mesh + collision input admission avoids overshooting the cap.
                        if (cache.Bytes + 12L * 1024 * 1024 > StreamingSettings.PreloadCpuLimit)
                        {
                            cache.Capped = true;
                            break;
                        }
                        var chunk = ChunkCodec.Decode(pair.Value);
                        cache.Store.PutDeferred(chunk);
                        cache.Bytes += TerrainChunkData.Count * 27L + chunk.Placements.Count * 64L + pair.Value.Length;
                        var mesh = TerrainMesher.Build(chunk, 1);
                        long bytes = (long)mesh.Positions.Length * 40 + (long)mesh.WaterPositions.Length * 20;
                        if (cache.Bytes + bytes > StreamingSettings.PreloadCpuLimit)
                        {
                            cache.Capped = true;
                            break;
                        }
                        var collision = PhysicsWorld.PrepareCollisionInput(chunk, cache.Begin.Planet!.Preset, changes, cache.Begin.Planet.Fixture).GetAwaiter().GetResult();
                        if (cache.Bytes + bytes + collision.Bytes > StreamingSettings.PreloadCpuLimit)
                        {
                            cache.Capped = true;
                            break;
                        }
                        cache.Meshes.Add(pair.Key, mesh);
                        cache.CollisionInputs.Add(pair.Key, collision);
                        cache.Bytes += bytes + collision.Bytes;
                    }
                    cache.Bytes -= cache.Transfer.Completed.Values.Sum(b => (long)b.Length);
                    cache.Transfer.Completed.Clear();
                    cache.Milliseconds = watch.Elapsed.TotalMilliseconds;
                });
            }
            return true;
        }
        if (m.Kind != "far-chunk")
            return false;
        if (m.LocationId != World.Definition.LocationId || m.SettingsHash != World.Definition.SettingsHash)
            return true;
        var p = m.Chunk ?? throw new InvalidDataException("Missing far tile.");
        var key = new FarTileKey(p.Coordinate, m.FarLevel);
        if (!key.Valid || !farRequested.Contains(key))
            throw new InvalidDataException("Unrequested far tile.");
        if (!farReceivers.TryGetValue(key, out var receiver))
        {
            if (farReceivers.Count >= 8 || m.Manifest?.Length != 1)
                throw new InvalidDataException("Invalid far tile manifest.");
            receiver = new(m with
            {
                TransferId = 1,
                Planet = World.Definition,
                CollisionChunks = []
            }, FarTile.MaxBytes);
            farReceivers.Add(key, receiver);
        }
        receiver.Add(p);
        FarBytesReceived += p.Data.Length;
        if (receiver.Complete)
        {
            var data = receiver.Completed[p.Coordinate];
            farReceivers.Remove(key);
            farDecoding[key] = Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                var tile = FarTile.Decode(data, p.Coordinate);
                if(tile.Key != key) throw new InvalidDataException("Far tile level mismatch.");
                Interlocked.Add(ref farDecodeTicks, watch.ElapsedTicks);
                return tile;
            });
        }
        return true;
    }
    void PollSupplemental()
    {
        if (Destination is { } cache)
        {
            if (cache.Expires < Now)
            {
                cache.Cancel();
                Destination = null;
            }
            else if (cache.Work?.IsCompleted == true && cache.Stage == PreloadStage.Preparing)
            {
                if (cache.Work.IsCompletedSuccessfully && !cache.Capped)
                    cache.Stage = PreloadStage.Ready;
                else
                {
                    _ = cache.Work.Exception;
                    cache.Stage = PreloadStage.Stale;
                }
            }
            if (ReferenceEquals(cache, Destination) && !cache.Cancellation.IsCancellationRequested && cache.Stage == PreloadStage.Ready && !cache.PhysicsReady && cache.Begin.LocationId != World.Definition.LocationId && transfer is null)
            {
                cache.Physics ??= new(cache.Begin.Planet!, true, cache.Store, [], cache.Changes);
                if (cache.Physics.PrepareTerrainBatch(cache.Begin.CollisionChunks!, 1))
                {
                    cache.Physics.SetTerrain(cache.Begin.CollisionChunks!);
                    cache.PhysicsReady = true;
                }
            }
        }
        foreach (var (c, task) in farDecoding.ToArray())
            if (task.IsCompleted)
            {
                farDecoding.Remove(c);
                if (task.IsCompletedSuccessfully)
                    FarScene[c] = task.Result;
                else
                {
                    FarDecodeFailures++;
                    if (FarDecodeFailures <= 3)
                        Console.Error.WriteLine("Far decode failed: " + task.Exception?.GetBaseException().Message);
                    farRequested.Remove(c);
                }
            }
    }
    void RequestFarScene(Vector3 position)
    {
        if (farRequested.Count - FarScene.Count >= 8)
            return;
        var cover = FarTileKey.Cover(position, Streaming.Radius).ToHashSet();
        foreach(var old in FarScene.Keys.Where(k => !cover.Contains(k)).OrderByDescending(k=>k.Distance(position)).Take(Math.Max(0,FarScene.Count-512)).ToArray()) { FarScene.Remove(old); farRequested.Remove(old); }
        var needed = cover.Where(c => !farRequested.Contains(c))
            .OrderBy(c => c.Distance(position)).Take(Math.Min(4, 8 - (farRequested.Count - FarScene.Count))).ToArray();
        if (needed.Length == 0)
            return;
        foreach (var c in needed)
            farRequested.Add(c);
        connection.Peer.Send(new()
        {
            Kind = "far-request",
            LocationId = World.Definition.LocationId,
            FarRequests = needed
        });
    }
    public void ReleasePromotedDestination()
    {
        PromotedDestination = null;
    }
    TerrainStore PromotePreloadStore(PlanetDefinition planet, long revision)
    {
        PollSupplemental();
        if (Destination is { Stage: PreloadStage.Ready } cache && cache.Begin.LocationId == planet.LocationId && cache.Store.SettingsHash == planet.SettingsHash)
        {
            PreloadPromotions++;
            PromotedDestination = cache;
            if (cache.Begin.ContentRevision != revision)
                cache.Stage = PreloadStage.Stale;
            return cache.Store; // Canonical chunks are immutable; building collision is always revalidated.
        }
        PromotedDestination = null;
        return new(planet.SettingsHash);
    }
    void ResetFarScene()
    {
        FarScene.Clear();
        farReceivers.Clear();
        farDecoding.Clear();
        farRequested.Clear();
    }
    void ClearSupplemental()
    {
        Destination?.Cancel();
        Destination = null;
        PromotedDestination = null;
        ResetFarScene();
        obsoletePreloads.Clear();
    }
}
