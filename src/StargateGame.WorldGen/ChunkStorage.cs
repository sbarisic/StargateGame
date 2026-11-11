using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Threading.Channels;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

public static class ChunkCodec
{
    public const int MaxBytes = 192 * 1024;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TerrainChunkData, EncodedChunk> encoded = new();
    public sealed record EncodedChunk(byte[] Bytes, string Hash);
    public static EncodedChunk Cached(TerrainChunkData c) => encoded.GetValue(c, chunk =>
    {
        var bytes = EncodeRaw(chunk);
        return new(bytes, Hash(bytes));
    });
    public static byte[] Encode(TerrainChunkData c) => Cached(c).Bytes;
    static byte[] EncodeRaw(TerrainChunkData c)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(0x35434753);
        w.Write(c.Coordinate.X);
        w.Write(c.Coordinate.Z);
        foreach (int h in c.Heights.Span)
            w.Write(h);
        foreach (int v in c.Water.Span)
            w.Write(v);
        foreach (int id in c.WaterBodies.Span)
            w.Write(id);
        w.Write(c.Materials.Span);
        w.Write(c.Roads.Span);
        w.Write(c.Environment.Span);
        w.Write(c.WaterKinds.Span);
        w.Write(c.Placements.Count);
        foreach (var p in c.Placements)
        {
            w.Write(p.Id);
            w.Write((byte)(p.Category == "tree" ? 0 : 1));
            w.Write(p.Position.X);
            w.Write(p.Position.Y);
            w.Write(p.Position.Z);
            w.Write(p.Yaw);
            w.Write(p.Scale);
        }
        return stream.ToArray();
    }
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data));
    public static TerrainChunkData Decode(byte[] data)
    {
        if (data.Length > MaxBytes || data.Length < 16 + TerrainChunkData.Count * 27)
            throw new InvalidDataException("Invalid chunk payload size.");
        using var s = new MemoryStream(data, false);
        using var r = new BinaryReader(s);
        if (r.ReadInt32() != 0x35434753)
            throw new InvalidDataException("Unknown chunk format.");
        var c = new ChunkCoordinate(r.ReadInt32(), r.ReadInt32());
        int[] h = new int[TerrainChunkData.Count], water = new int[h.Length];
        for (int i = 0; i < h.Length; i++)
            h[i] = r.ReadInt32();
        for (int i = 0; i < h.Length; i++)
            water[i] = r.ReadInt32();
        int[] regions = new int[h.Length];
        for (int i = 0; i < regions.Length; i++)
            regions[i] = r.ReadInt32();
        byte[] m = r.ReadBytes(h.Length * 4), roads = r.ReadBytes(h.Length);
        byte[] environment = r.ReadBytes(h.Length * 9), waterKinds = r.ReadBytes(h.Length);
        int count = r.ReadInt32();
        if (count < 0 || count > 512 || s.Length - s.Position != count * 29)
            throw new InvalidDataException("Invalid placement payload.");
        PlacementCandidate[] p = new PlacementCandidate[count];
        HashSet<ulong> ids = [];
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            byte category = r.ReadByte();
            Vector3 pos = new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            float yaw = r.ReadSingle(), scale = r.ReadSingle();
            if (category > 1 || !ids.Add(id) || !float.IsFinite(pos.Y) || Math.Abs(pos.Y) > 2048 || !float.IsFinite(pos.X) || !float.IsFinite(pos.Z) ||
               ChunkCoordinate.At(pos.X, pos.Z) != c || !float.IsFinite(yaw) || yaw < 0 || yaw > MathF.Tau || !float.IsFinite(scale) || scale is < .1f or > 10)
                throw new InvalidDataException("Invalid placement candidate.");
            p[i] = new(id, category == 0 ? "tree" : "rock", pos, yaw, scale);
        }
        return new(c, h, water, m, p, regions, roads, environment, waterKinds);
    }
}

/// <summary>Bounded canonical data cache; does not hold native or GPU resources.</summary>
public sealed class TerrainStore(string settingsHash, string? cacheRoot = null) : ITerrainSource
{
    readonly ConcurrentDictionary<ChunkCoordinate, TerrainChunkData> chunks = new();
    readonly string root = cacheRoot ?? Environment.GetEnvironmentVariable("STARGATEGAME_TERRAIN_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame", "terrain-cache");
    static readonly object DiskGate = new();
    static readonly Channel<(TerrainStore Store, TerrainChunkData Chunk)> writes = Channel.CreateBounded<(TerrainStore, TerrainChunkData)>(64);
    static readonly Task writer = WriteLoop();
    static async Task WriteLoop()
    {
        await foreach (var entry in writes.Reader.ReadAllAsync())
            entry.Store.Persist(entry.Chunk);
    }
    long revision;
    public long Revision => Interlocked.Read(ref revision);
    public void PutDeferred(TerrainChunkData chunk)
    {
        chunks[chunk.Coordinate] = chunk;
        Interlocked.Increment(ref revision);
        writes.Writer.TryWrite((this, chunk));
    }
    public string SettingsHash
    {
        get;
    } = settingsHash;
    public int Count => chunks.Count;
    public void Retain(IReadOnlySet<ChunkCoordinate> keep)
    {
        foreach (var c in chunks.Keys)
            if (!keep.Contains(c) && chunks.TryRemove(c, out _))
                Interlocked.Increment(ref revision);
    }
    public long Bytes => chunks.Count * (long)TerrainChunkData.Count * 27;
    public IEnumerable<TerrainChunkData> Chunks => chunks.Values;
    public void Put(TerrainChunkData chunk, bool persist = true)
    {
        chunks[chunk.Coordinate] = chunk;
        Interlocked.Increment(ref revision);
        if (persist)
            Persist(chunk);
    }
    void Persist(TerrainChunkData chunk)
    {
        lock (DiskGate)
        {
            string? temp = null;
            try
            {
                Directory.CreateDirectory(root);
                byte[] data = ChunkCodec.Encode(chunk);
                string path = CachePath(chunk.Coordinate);
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, SHA256.HashData(data).Concat(data).ToArray());
                File.Move(temp, path, true);
                var files = new DirectoryInfo(root).GetFiles("*.sgchunk").OrderBy(f => f.LastWriteTimeUtc).ToArray();
                long size = files.Sum(f => f.Length);
                foreach (var file in files)
                {
                    if (size <= 1024L * 1024 * 1024)
                        break;
                    if (file.FullName == path)
                        continue;
                    size -= file.Length;
                    file.Delete();
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                if (temp is not null)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
    }
    string CachePath(ChunkCoordinate c) => Path.Combine(root, $"{SettingsHash}_{c.X}_{c.Z}.sgchunk");
    public bool TryGetChunk(ChunkCoordinate c, out TerrainChunkData chunk) => chunks.TryGetValue(c, out chunk!);
    public bool TryLoad(ChunkCoordinate c, out TerrainChunkData chunk)
    {
        if (TryGetChunk(c, out chunk))
            return true;
        lock (DiskGate)
        {
            try
            {
                string path = CachePath(c);
                if (!File.Exists(path))
                    return false;
                if (new FileInfo(path).Length > ChunkCodec.MaxBytes + 32)
                    return false;
                var stored = File.ReadAllBytes(path);
                if (stored.Length < 32 || !SHA256.HashData(stored.AsSpan(32)).AsSpan().SequenceEqual(stored.AsSpan(0, 32)))
                    return false;
                chunk = ChunkCodec.Decode(stored[32..]);
                if (chunk.Coordinate != c)
                    return false;
                chunks[c] = chunk;
                Interlocked.Increment(ref revision);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
    public float SampleHeight(float x, float z)
    {
        x = Math.Clamp(x, -WorldDomain.HalfSize, WorldDomain.HalfSize);
        z = Math.Clamp(z, -WorldDomain.HalfSize, WorldDomain.HalfSize);
        var c = ChunkCoordinate.At(Math.Clamp(x, -WorldDomain.HalfSize, WorldDomain.HalfSize-.001f), Math.Clamp(z, -WorldDomain.HalfSize, WorldDomain.HalfSize-.001f));
        if (!TryGetChunk(c, out var data))
            throw new InvalidOperationException($"Terrain chunk {c} is not ready.");
        return data.Sample(x, z);
    }
}
