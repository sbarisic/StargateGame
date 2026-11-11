using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace StargateGame.Client;

/// <summary>Index-only mounts. Snapshots keep readers on one immutable generation.</summary>
sealed class MountedAssetStore : IDisposable
{
    internal sealed record Block(SafeFileHandle? Handle, string? File, long Offset, int Stored, int Size, string Hash, bool Brotli);
    internal sealed record Asset(string Category, string[] Dependencies, string Reload, bool Deleted = false,
        float[]? BoundsMin = null, float[]? BoundsMax = null);
    internal sealed record Snapshot(string Generation, IReadOnlyDictionary<string, Block> Blocks, IReadOnlyDictionary<string, Asset> Assets);
    internal sealed record Refresh(Snapshot Next, string[] Changed, bool RequiresReload);
    sealed record Payload(string Hash, int Size);
    sealed record Pack(string Name, string File, string IndexHash,
        Dictionary<string, Asset>? Assets = null, string[]? Tombstones = null);
    sealed record Catalog(int Version, string Cooker, string Id, Dictionary<string, Payload> Entries,
        Dictionary<string, Asset> Assets, Pack[] Packs, Pack[] Patches);
    sealed record Overlay(int Version, string Cooker, string BaseId, string Generation,
        Dictionary<string, Payload> Entries, string[] Tombstones, Dictionary<string, Asset> Assets);
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly List<SafeFileHandle> handles = [];
    readonly string? development;
    readonly Catalog? catalog;
    readonly Snapshot baseline;
    Snapshot current;
    public Snapshot Current => Volatile.Read(ref current);
    public bool HasCatalog => catalog is not null;
    public bool HasDevelopment => development is not null;

    public MountedAssetStore(string root, string? development = null)
    {
        this.development = development is null ? null : Path.GetFullPath(development);
        try
        {
            var blocks = new Dictionary<string, Block>(StringComparer.Ordinal);
            var assets = new Dictionary<string, Asset>(StringComparer.Ordinal);
            string path = Path.Combine(root, "packs", "catalog.json");
            if (File.Exists(path))
            {
                catalog = ReadJson<Catalog>(path);
                if (catalog.Version != 1 || !HashValid(catalog.Id) || !HashValid(catalog.Cooker))
                    throw new InvalidDataException("Unsupported asset catalog. Rebuild runtime packs.");
                string directory = Path.GetDirectoryName(path)!;
                foreach (var pack in catalog.Packs)
                    Mount(directory, pack.File, pack.IndexHash, blocks, replace: false);
                foreach (var (key, payload) in catalog.Entries)
                {
                    ValidateKey(key);
                    if (!blocks.TryGetValue(key, out var block) || block.Hash != payload.Hash || block.Size != payload.Size)
                        throw new InvalidDataException("Catalog/package mismatch: " + key);
                }
                if (blocks.Count != catalog.Entries.Count)
                    throw new InvalidDataException("Catalog contains unowned package entries.");
                foreach (var (key, asset) in catalog.Assets) assets.Add(key, asset);
                foreach (var pack in catalog.Patches)
                {
                    Mount(directory, pack.File, pack.IndexHash, blocks, replace: true);
                    foreach (string key in pack.Tombstones ?? [])
                    {
                        ValidateKey(key);
                        blocks.Remove(key);
                    }
                    foreach (var (key, asset) in pack.Assets ?? [])
                    {
                        ValidateKey(key);
                        if (asset.Deleted) assets.Remove(key);
                        else assets[key] = asset;
                    }
                }
                ValidateAssets(assets, blocks);
            }
            else
            {
                if (development is not null)
                    throw new InvalidDataException("Development overrides require category packs. Rebuild runtime packs first.");
                Mount(root, "models.sga", null, blocks, replace: false);
            }
            baseline = new("base", blocks, assets);
            current = this.development is null ? baseline : LoadOverlay()!;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException("Empty asset index: " + path);
    static bool HashValid(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\\') || key.Contains(':') || key.Split('/').Any(p => p is "" or "." or ".."))
            throw new InvalidDataException("Invalid asset path: " + key);
    }
    static void ValidateAssets(IReadOnlyDictionary<string, Asset> assets, IReadOnlyDictionary<string, Block> blocks)
    {
        foreach (var (key, asset) in assets)
        {
            ValidateKey(key);
            if (asset.Deleted) continue;
            if (!blocks.ContainsKey(key) || asset.Dependencies is null || string.IsNullOrWhiteSpace(asset.Category)
                || asset.Reload is not ("visual" or "session"))
                throw new InvalidDataException("Invalid asset descriptor: " + key);
            if ((asset.BoundsMin is null) != (asset.BoundsMax is null)
                || asset.BoundsMin is not null && (asset.BoundsMin.Length != 3 || asset.BoundsMax!.Length != 3
                    || asset.BoundsMin.Concat(asset.BoundsMax).Any(v => !float.IsFinite(v))
                    || Enumerable.Range(0, 3).Any(i => asset.BoundsMin[i] > asset.BoundsMax[i])))
                throw new InvalidDataException("Invalid asset bounds: " + key);
            foreach (string dependency in asset.Dependencies)
                if (!blocks.ContainsKey(dependency))
                    throw new InvalidDataException($"Asset {key} has missing dependency {dependency}.");
        }
    }
    void Mount(string root, string file, string? expectedIndex, Dictionary<string, Block> blocks, bool replace)
    {
        ValidateKey(file);
        var handle = File.OpenHandle(Path.Combine(root, file), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
        handles.Add(handle);
        byte[] header = new byte[48];
        ReadExactly(handle, header, 0);
        long offset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(4));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x31414753 || length is < 2 or > 32 * 1024 * 1024
            || offset < 48 || offset > RandomAccess.GetLength(handle) - length)
            throw new InvalidDataException("Invalid SGA index bounds: " + file);
        byte[] data = new byte[length];
        ReadExactly(handle, data, offset);
        byte[] hash = SHA256.HashData(data);
        if (!hash.AsSpan().SequenceEqual(header.AsSpan(16)) || expectedIndex is not null && Convert.ToHexStringLower(hash) != expectedIndex)
            throw new InvalidDataException("Corrupt SGA index: " + file);
        var index = JsonSerializer.Deserialize<Dictionary<string, CookedAssets.Entry>>(data)
            ?? throw new InvalidDataException("Empty SGA index.");
        foreach (var (key, e) in index)
        {
            ValidateKey(key);
            if (e.Size is < 0 or > 256 * 1024 * 1024 || e.Stored is < 0 or > 256 * 1024 * 1024 || e.Offset < 48
                || e.Offset > offset - e.Stored || !e.Brotli && e.Stored != e.Size || !HashValid(e.Hash))
                throw new InvalidDataException("Invalid SGA block: " + key);
            if (!replace && blocks.ContainsKey(key))
                throw new InvalidDataException("Multiple owning packs for " + key);
            blocks[key] = new(handle, null, e.Offset, e.Stored, e.Size, e.Hash.ToLowerInvariant(), e.Brotli);
        }
    }
    Snapshot? LoadOverlay(string? knownGeneration = null)
    {
        var overlay = ReadJson<Overlay>(Path.Combine(development!, "manifest.json"));
        if (overlay.Version != 1 || overlay.Cooker != catalog!.Cooker || overlay.BaseId != catalog.Id)
            throw new InvalidDataException("Development override is incompatible with the mounted base/cooker. Rebuild base packs and the development override.");
        if (!HashValid(overlay.Generation)) throw new InvalidDataException("Invalid override generation.");
        if (overlay.Generation == knownGeneration) return null;
        var blocks = new Dictionary<string, Block>(baseline.Blocks, StringComparer.Ordinal);
        foreach (string key in overlay.Tombstones)
        {
            ValidateKey(key);
            if (overlay.Entries.ContainsKey(key)) throw new InvalidDataException("Override both deletes and replaces " + key);
            blocks.Remove(key);
        }
        foreach (var (key, payload) in overlay.Entries)
        {
            ValidateKey(key);
            if (!HashValid(payload.Hash) || payload.Size is < 0 or > 256 * 1024 * 1024)
                throw new InvalidDataException("Invalid override payload: " + key);
            string path = Path.Combine(development!, "blobs", payload.Hash + ".bin");
            if (!File.Exists(path) || new FileInfo(path).Length != payload.Size)
                throw new InvalidDataException("Missing override payload: " + key);
            blocks[key] = new(null, path, 0, payload.Size, payload.Size, payload.Hash, false);
        }
        var assets = new Dictionary<string, Asset>(baseline.Assets, StringComparer.Ordinal);
        foreach (var (key, asset) in overlay.Assets)
        {
            ValidateKey(key);
            if (asset.Deleted) assets.Remove(key);
            else assets[key] = asset; // Complete descriptors only; never merge fields.
        }
        ValidateAssets(assets, blocks);
        return new(overlay.Generation, blocks, assets);
    }
    // Call on a preparation worker; publishing the returned snapshot is a frame-boundary action.
    public Refresh? CheckRefresh()
    {
        if (development is null) return null;
        Snapshot before = Current;
        Snapshot? next = LoadOverlay(before.Generation);
        if (next is null) return null;
        var changed = before.Blocks.Keys.Union(next.Blocks.Keys).Where(k =>
            !before.Blocks.TryGetValue(k, out var a) || !next.Blocks.TryGetValue(k, out var b) || a.Hash != b.Hash).ToHashSet();
        foreach (string key in before.Assets.Keys.Union(next.Assets.Keys))
            if (!before.Assets.TryGetValue(key, out var a) || !next.Assets.TryGetValue(key, out var b)
                || a.Category != b.Category || a.Reload != b.Reload || a.Deleted != b.Deleted
                || !a.Dependencies.SequenceEqual(b.Dependencies)
                || !(a.BoundsMin ?? []).SequenceEqual(b.BoundsMin ?? [])
                || !(a.BoundsMax ?? []).SequenceEqual(b.BoundsMax ?? [])) changed.Add(key);
        var affected = before.Assets.Concat(next.Assets)
            .Where(a => changed.Contains(a.Key) || a.Value.Dependencies.Any(changed.Contains)).ToArray();
        bool reload = affected.Any(a => a.Value.Reload != "visual");
        return new(next, changed.ToArray(), reload);
    }
    public void Publish(Refresh refresh) => Volatile.Write(ref current, refresh.Next);

    internal static void ReadExactly(SafeFileHandle handle, Span<byte> bytes, long offset)
    {
        while (!bytes.IsEmpty)
        {
            int count = RandomAccess.Read(handle, bytes, offset);
            if (count == 0) throw new EndOfStreamException("Truncated asset payload.");
            bytes = bytes[count..]; offset += count;
        }
    }
    internal static byte[] Read(Block block, out long readTicks, out long decodeTicks)
    {
        long begin = Stopwatch.GetTimestamp();
        byte[] stored = new byte[block.Stored];
        if (block.Handle is not null) ReadExactly(block.Handle, stored, block.Offset);
        else
        {
            using var file = File.OpenHandle(block.File!, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);
            ReadExactly(file, stored, 0);
        }
        readTicks = Stopwatch.GetTimestamp() - begin;
        begin = Stopwatch.GetTimestamp();
        byte[] result = stored;
        if (block.Brotli)
        {
            try
            {
                result = new byte[block.Size];
                using var source = new MemoryStream(stored, false);
                using var decoder = new BrotliStream(source, CompressionMode.Decompress);
                decoder.ReadExactly(result);
                if (decoder.ReadByte() != -1) throw new InvalidDataException("Oversized asset payload.");
            }
            catch (Exception error) when (error is InvalidOperationException or EndOfStreamException)
            {
                throw new InvalidDataException("Corrupt compressed asset: " + block.Hash, error);
            }
        }
        if (Convert.ToHexStringLower(SHA256.HashData(result)) != block.Hash)
            throw new InvalidDataException("Corrupt asset payload: " + block.Hash);
        decodeTicks = Stopwatch.GetTimestamp() - begin;
        return result;
    }
    public void Dispose()
    {
        foreach (var handle in handles) handle.Dispose();
        handles.Clear();
    }
}
