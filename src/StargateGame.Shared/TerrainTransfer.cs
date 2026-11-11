using System.Security.Cryptography;

namespace StargateGame.Shared;

public sealed record ChunkManifestEntry(ChunkCoordinate Coordinate, string Hash, int Length);
public sealed record ChunkPacket(ChunkCoordinate Coordinate, string Hash, int Offset, byte[] Data);

/// <summary>Bounded, ordered fragment receiver. No native state is accepted here.</summary>
public sealed class TerrainTransfer
{
    readonly Dictionary<ChunkCoordinate, ChunkManifestEntry> manifest;
    readonly Dictionary<ChunkCoordinate, MemoryStream> partial = [];
    public Dictionary<ChunkCoordinate, byte[]> Completed
    {
        get;
    } = [];
    public Message Preparation
    {
        get;
    }
    public bool Complete => Completed.Count == manifest.Count;
    public TerrainTransfer(Message message, int maximumChunkBytes = 192 * 1024)
    {
        if (message.Version != Protocol.Version || message.Planet is null || message.Manifest is null || message.CollisionChunks is null || message.TransferId <= 0 || message.Manifest.Length is < 1 or > 256 || message.CollisionChunks.Length > 256)
            throw new InvalidDataException("Invalid terrain manifest.");
        PlanetSettings.Validate(message.Planet);
        Preparation = message;
        manifest = [];
        foreach (var m in message.Manifest)
            if (m is null || m.Hash is null || !m.Coordinate.Valid || (m.Length < 1 || m.Length > maximumChunkBytes) || m.Hash.Length != 64 || !m.Hash.All(Uri.IsHexDigit) || !manifest.TryAdd(m.Coordinate, m))
                throw new InvalidDataException("Invalid terrain manifest entry.");
        if (message.CollisionChunks.Distinct().Count() != message.CollisionChunks.Length || message.CollisionChunks.Any(c => !manifest.ContainsKey(c)))
            throw new InvalidDataException("Incomplete collision manifest.");
    }
    public bool UseCached(ChunkCoordinate coordinate, byte[] data)
    {
        if (!manifest.TryGetValue(coordinate, out var entry) || data.Length != entry.Length || Convert.ToHexString(SHA256.HashData(data)) != entry.Hash)
            return false;
        Completed.TryAdd(coordinate, data);
        return true;
    }
    public void Add(ChunkPacket packet)
    {
        if (!manifest.TryGetValue(packet.Coordinate, out var m) || packet.Hash != m.Hash || packet.Data is null || packet.Data.Length is < 1 or > 32768 || Completed.ContainsKey(packet.Coordinate))
            throw new InvalidDataException("Unexpected chunk fragment.");
        if (!partial.TryGetValue(packet.Coordinate, out var stream))
            partial[packet.Coordinate] = stream = new(m.Length);
        if (packet.Offset != stream.Length || stream.Length + packet.Data.Length > m.Length)
            throw new InvalidDataException("Invalid chunk fragment offset.");
        stream.Write(packet.Data);
        if (stream.Length == m.Length)
        {
            var data = stream.ToArray();
            if (Convert.ToHexString(SHA256.HashData(data)) != m.Hash)
                throw new InvalidDataException("Terrain hash mismatch.");
            Completed.Add(packet.Coordinate, data);
            stream.Dispose();
            partial.Remove(packet.Coordinate);
        }
    }
}
