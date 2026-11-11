using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace StargateGame.Shared;

public sealed record Message
{
    public CableCommand? CableCommand { get; init; }
    public CableSnapshot? Cables { get; init; }
    public CableReply? CableReply { get; init; }
    public WeaponRequest? WeaponCommand { get; init; }
    public WeaponResult? Weapons { get; init; }
    public bool RegenerateHome { get; init; }
    public PlanetDebugRequest? PlanetDebug { get; init; }
    public PlanetDebugStatus? PlanetDebugState { get; init; }
    public PlanetEnvironmentDefinition? PlanetEnvironment { get; init; }
    public TransitNotice? Transit
    {
        get;
        init;
    }
    public byte[]? IdentityKey
    {
        get;
        init;
    }
    public byte[]? IdentitySignature
    {
        get;
        init;
    }
    public IdentityChallenge? Challenge
    {
        get;
        init;
    }
    public string? IdentityProfile
    {
        get;
        init;
    }
    public Guid PersistentPlayerId
    {
        get;
        init;
    }
    public WorldSoundEvent[]? Sounds
    {
        get;
        init;
    }
    public Guid LocationId
    {
        get;
        init;
    }
    public Guid PreloadId
    {
        get;
        init;
    }
    public Guid SourceLocationId
    {
        get;
        init;
    }
    public long ContentRevision
    {
        get;
        init;
    }
    public long GateTick
    {
        get;
        init;
    }
    public CharacterAction? CharacterAction
    {
        get;
        init;
    }
    public CharacterPresentation[]? Characters
    {
        get;
        init;
    }
    public DhdCommand? Dhd
    {
        get;
        init;
    }
    public DhdResult? DhdResult
    {
        get;
        init;
    }
    public GateState? Gate
    {
        get;
        init;
    }
    public ConnectionState? Connection
    {
        get;
        init;
    }
    public int[]? Address
    {
        get;
        init;
    }
    public DeveloperDestination[]? Destinations
    {
        get;
        init;
    }
    public string Kind
    {
        get;
        init;
    } = "";
    public int Version
    {
        get;
        init;
    } = Protocol.Version;
    public string? Name
    {
        get;
        init;
    }
    public string? Reason
    {
        get;
        init;
    }
    public int PlayerId
    {
        get;
        init;
    }
    public PlanetDefinition? Planet
    {
        get;
        init;
    }
    public PhysicsSnapshot? State
    {
        get;
        init;
    }
    public string? PhysicsHash
    {
        get;
        init;
    }
    public string? ContentHash
    {
        get;
        init;
    }
    public long Echo
    {
        get;
        init;
    }
    public PlayerInput? Input
    {
        get;
        init;
    }
    public long Tick
    {
        get;
        init;
    }
    public long EnvironmentUtcMilliseconds
    {
        get;
        init;
    }
    public TerrainMetrics? Metrics
    {
        get;
        init;
    }
    public long TransferId
    {
        get;
        init;
    }
    public string? SettingsHash
    {
        get;
        init;
    }
    public long Epoch
    {
        get;
        init;
    }
    public bool SwitchPlanet
    {
        get;
        init;
    }
    public bool Developer
    {
        get;
        init;
    }
    public ChunkManifestEntry[]? Manifest
    {
        get;
        init;
    }
    public ChunkCoordinate[]? CollisionChunks
    {
        get;
        init;
    }
    public ChunkPacket? Chunk
    {
        get;
        init;
    }
    public FarTileKey[]? FarRequests { get; init; }
    public int FarLevel { get; init; }
    public ChunkCoordinate[]? Requests
    {
        get;
        init;
    }
    public BuildingMetrics? BuildingMetrics
    {
        get;
        init;
    }
    public BuildCommand? Build
    {
        get;
        init;
    }
    public BuildResult? BuildResult
    {
        get;
        init;
    }
    public WorldStatePart? WorldPart
    {
        get;
        init;
    }
    public int KitVersion
    {
        get;
        init;
    } = 1;
    public int Seed
    {
        get;
        init;
    }
    public string? Preset
    {
        get;
        init;
    }
}

public static class Protocol
{
    public const int Version = 31, MaxFrameBytes = 65536;
    public static readonly JsonSerializerOptions Json = new() { IncludeFields = true, MaxDepth = 16 };
    public static async Task WriteAsync(Stream stream, Message message, CancellationToken token = default)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (data.Length > MaxFrameBytes)
            throw new InvalidDataException("Message exceeds frame limit.");
        byte[] prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, data.Length);
        await stream.WriteAsync(prefix, token);
        await stream.WriteAsync(data, token);
    }
    public static async Task<Message> ReadAsync(Stream stream, CancellationToken token = default)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size <= 0 || size > MaxFrameBytes)
            throw new InvalidDataException("Invalid frame length.");
        byte[] data = new byte[size];
        await stream.ReadExactlyAsync(data, token);
        try
        {
            return JsonSerializer.Deserialize<Message>(data, Json) ?? throw new InvalidDataException("Empty message.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed message.", ex);
        }
    }
}

// Each socket has one reader and one writer. Game threads never block on socket IO.
public sealed class Peer : IDisposable
{
    readonly TcpClient socket;
    readonly NetworkStream stream;
    readonly CancellationTokenSource stop = new();
    readonly Channel<Message> outgoing = Channel.CreateBounded<Message>(256);
    readonly Channel<Message> cosmetic = Channel.CreateBounded<Message>(32);
    readonly Channel<Message> bulk = Channel.CreateBounded<Message>(8);
    readonly SemaphoreSlim writable = new(0);
    readonly Channel<Message> incoming = Channel.CreateBounded<Message>(256);
    public ChannelReader<Message> Incoming => incoming.Reader;
    public string? Failure
    {
        get;
        private set;
    }
    public bool Closed => stop.IsCancellationRequested;
    public Task Completion
    {
        get;
    }
    public Peer(TcpClient socket)
    {
        this.socket = socket;
        stream = socket.GetStream();
        socket.NoDelay = true;
        Completion = Task.WhenAll(ReadLoop(), WriteLoop());
    }
    public bool Send(Message message)
    {
        if (!Closed && outgoing.Writer.TryWrite(message))
        {
            writable.Release();
            return true;
        }
        Close("Connection queue overflow.");
        return false;
    }
    public bool TrySendCosmetic(Message message)
    {
        if (Closed || !cosmetic.Writer.TryWrite(message))
            return false;
        writable.Release();
        return true;
    }
    public bool SendBulk(Message message)
    {
        if (Closed || !bulk.Writer.TryWrite(message))
            return false;
        writable.Release();
        return true;
    }
    async Task ReadLoop()
    {
        long heartbeatWindow = Environment.TickCount64;
        int heartbeats = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var message = await Protocol.ReadAsync(stream, timeout.Token);
                // Keep the transport alive during bounded resource preparation,
                // without injecting messages into prediction or gameplay queues.
                if (message.Kind == "keepalive")
                {
                    long now = Environment.TickCount64;
                    if (now - heartbeatWindow >= 1000)
                    {
                        heartbeatWindow = now;
                        heartbeats = 0;
                    }
                    if (message.Version != Protocol.Version || ++heartbeats > 4)
                        throw new InvalidDataException("Invalid transport keepalive.");
                    continue;
                }
                if (!incoming.Writer.TryWrite(message))
                    throw new InvalidDataException("Incoming queue overflow.");
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Close(ex is OperationCanceledException ? "Connection timed out." : "Connection closed: " + ex.Message);
        }
        finally
        {
            incoming.Writer.TryComplete();
        }
    }
    async Task WriteLoop()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                bool signalled = await writable.WaitAsync(TimeSpan.FromSeconds(2), stop.Token);
                Message? message;
                if (!signalled)
                    message = new()
                    {
                        Kind = "keepalive"
                    };
                else if (!outgoing.Reader.TryRead(out message) && !bulk.Reader.TryRead(out message) && !cosmetic.Reader.TryRead(out message))
                    continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await Protocol.WriteAsync(stream, message, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            Close("Connection closed: " + ex.Message);
        }
    }
    public void Close(string reason)
    {
        if (Closed)
            return;
        Failure = reason;
        stop.Cancel();
        socket.Dispose();
        outgoing.Writer.TryComplete();
        bulk.Writer.TryComplete();
        cosmetic.Writer.TryComplete();
    }
    public void Dispose() => Close("Disconnected.");
}

public sealed class GameConnection : IDisposable
{
    public Peer Peer
    {
        get;
    }
    public Message Welcome
    {
        get;
    }
    public WorldChanges InitialChanges
    {
        get;
    }
    public IReadOnlyDictionary<ChunkCoordinate, byte[]> InitialTerrain
    {
        get;
    }
    GameConnection(Peer peer, Message welcome, IReadOnlyDictionary<ChunkCoordinate, byte[]>? terrain = null, WorldChanges? changes = null)
    {
        InitialChanges = changes ?? WorldChanges.Empty;
        Peer = peer;
        Welcome = welcome;
        InitialTerrain = terrain ?? new Dictionary<ChunkCoordinate, byte[]>();
    }
    public static async Task<GameConnection> ConnectAsync(string host, int port, string name, string physicsHash, string contentHash, CancellationToken token = default, PlayerIdentity? identity = null)
    {
        using var temporaryIdentity = identity is null ? new PlayerIdentity() : null;
        identity ??= temporaryIdentity!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var socket = new TcpClient();
        Peer? peer = null;
        try
        {
            await socket.ConnectAsync(host, port, timeout.Token);
            peer = new Peer(socket);
            peer.Send(new()
            {
                Kind = "hello",
                Name = name,
                PhysicsHash = physicsHash,
                ContentHash = contentHash,
                IdentityKey = identity.PublicKey,
                IdentityProfile = identity.TestProfile
            });
            var welcome = await peer.Incoming.ReadAsync(timeout.Token);
            if (welcome.Kind == "identity-challenge" && welcome.Challenge is { } challenge)
            {
                peer.Send(new()
                {
                    Kind = "identity-proof",
                    IdentitySignature = identity.Sign(challenge)
                });
                welcome = await peer.Incoming.ReadAsync(timeout.Token);
            }
            while (welcome.Kind == "terrain-wait")
            {
                welcome = await peer.Incoming.ReadAsync(timeout.Token);
            }
            WorldStateReceiver changes = new();
            while (welcome.Kind == "world-state")
            {
                changes.Add(welcome.WorldPart ?? throw new InvalidDataException("Missing world state."));
                welcome = await peer.Incoming.ReadAsync(timeout.Token);
            }
            TerrainTransfer? transfer = null;
            if (welcome.Kind == "terrain-prepare")
            {
                transfer = new(welcome);
                peer.Send(new()
                {
                    Kind = "terrain-fetch",
                    TransferId = welcome.TransferId,
                    Requests = welcome.Manifest!.Select(m => m.Coordinate).ToArray()
                });
                long progressAt = Environment.TickCount64;
                while (!transfer.Complete)
                {
                    var fragment = await peer.Incoming.ReadAsync(timeout.Token);
                    if (fragment.Kind != "terrain-chunk" || fragment.TransferId != welcome.TransferId || fragment.Chunk is null)
                        throw new InvalidDataException(fragment.Reason ?? "Invalid initial terrain transfer.");
                    transfer.Add(fragment.Chunk);
                    if (Environment.TickCount64 - progressAt >= 1000)
                    {
                        peer.Send(new()
                        {
                            Kind = "terrain-progress",
                            TransferId = welcome.TransferId
                        });
                        progressAt = Environment.TickCount64;
                    }
                }
                peer.Send(new()
                {
                    Kind = "terrain-ready",
                    TransferId = welcome.TransferId
                });
                welcome = await peer.Incoming.ReadAsync(timeout.Token);
            }
            if (welcome.Kind != "welcome" || welcome.Version != Protocol.Version || welcome.Planet is null || welcome.State is null)
                throw new IOException(welcome.Reason ?? "Server sent an incompatible welcome.");
            if (welcome.PhysicsHash != physicsHash || welcome.ContentHash != contentHash)
                throw new IOException("Physics or world content does not match this client. Install the same game build as the server.");
            return new(peer, welcome, transfer?.Completed, changes.State);
        }
        catch
        {
            peer?.Dispose();
            socket.Dispose();
            throw;
        }
    }
    public void Dispose() => Peer.Dispose();
}
