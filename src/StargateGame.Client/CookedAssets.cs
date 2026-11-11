using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace StargateGame.Client;

/// <summary>Independent checked blocks; no archive-wide inflate or mixer/render-thread IO callbacks.</summary>
sealed class CookedAssets : IDisposable
{
    public sealed record Entry(long Offset, int Stored, int Size, string Hash, bool Brotli);
    readonly string root;
    readonly MountedAssetStore? store;
    public MountedAssetStore? Store => store;
    readonly Dictionary<string, byte[]> prepared = [];
    readonly Dictionary<string, Task<byte[]>> pending = [];
    readonly HashSet<Task> inFlight = [];
    readonly SemaphoreSlim workers = new(2);
    readonly CancellationTokenSource stop = new();
    const long QueueLimit = 64L * 1024 * 1024;
    long reserved, bytesRead, readTicks, decodeTicks, parseTicks, hits;
    int generation;
    bool disposed;
    public long PreparedBytes
    {
        get
        {
            lock (prepared)
                return reserved;
        }
    }
    public long ReadBytes => Interlocked.Read(ref bytesRead);
    public double ReadMilliseconds => Interlocked.Read(ref readTicks) * 1000d / Stopwatch.Frequency;
    public double DecodeMilliseconds => Interlocked.Read(ref decodeTicks) * 1000d / Stopwatch.Frequency;
    public long CacheHits => Interlocked.Read(ref hits);
    public double ParseMilliseconds => Interlocked.Read(ref parseTicks) * 1000d / Stopwatch.Frequency;
    // CPU preparation shares the reader's two-worker pool. Only completed GPU
    // upload inputs are retained by their owning job, not an unbounded preload.
    public Task<T> PrepareCpuAsync<T>(Func<T> prepare, CancellationToken cancellation = default)
    {
        var task = Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellation);
            await workers.WaitAsync(linked.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                long begin = Stopwatch.GetTimestamp();
                try
                {
                    return prepare();
                }
                finally
                {
                    Interlocked.Add(ref parseTicks, Stopwatch.GetTimestamp() - begin);
                }
            }
            finally
            {
                workers.Release();
            }
        });
        lock (prepared)
            inFlight.Add(task);
        _ = task.ContinueWith(t =>
        {
            _ = t.Exception;
            lock (prepared)
                inFlight.Remove(t);
        }, TaskContinuationOptions.ExecuteSynchronously);
        return task;
    }
    public bool IsCooked => store is not null;
    public int VerifyAll()
    {
        if (!IsCooked)
            throw new InvalidOperationException("No cooked package found.");
        int count = 0;
        foreach (var key in store!.Current.Blocks.Keys)
        {
            ReadCore(key);
            count++;
        }
        return count;
    }
    public CookedAssets(string root, bool loose = false, string? development = null)
    {
        this.root = Path.GetFullPath(root);
        if (loose && development is not null)
            throw new ArgumentException("--loose-assets cannot be combined with --dev-assets.");
        if (loose) return;
        bool exists = File.Exists(Path.Combine(root, "packs", "catalog.json")) || File.Exists(Path.Combine(root, "models.sga"));
        if (!exists)
        {
            if (development is not null || File.Exists(Path.Combine(root, "cooked.required")))
                throw new InvalidDataException("Required runtime packs are missing. Build asset packs before launching.");
            return;
        }
        store = new MountedAssetStore(root, development);
    }
    static void ValidatePath(string key) => MountedAssetStore.ValidateKey(key);
    string LoosePath(string key) => key.StartsWith("models/veg-", StringComparison.Ordinal) ? Path.Combine(root, "vegetation", "models", key[7..]) : Path.Combine(root, key);
    public bool Exists(string key)
    {
        ValidatePath(key);
        return store is not null ? store.Current.Blocks.ContainsKey(key) : File.Exists(LoosePath(key));
    }
    public long Size(string key)
    {
        ValidatePath(key);
        return store is not null ? store.Current.Blocks[key].Size : new FileInfo(LoosePath(key)).Length;
    }
    public bool FitsStaging(string key)
    {
        long bytes = Size(key);
        if (store?.Current.Blocks.TryGetValue(key, out var block) == true && block.Brotli) bytes += block.Stored;
        return bytes <= QueueLimit;
    }
    byte[] ReadCore(string key, MountedAssetStore.Snapshot? snapshot = null)
    {
        ValidatePath(key);
        long start = Stopwatch.GetTimestamp();
        byte[] bytes;
        if (store is null) bytes = File.ReadAllBytes(LoosePath(key));
        else
        {
            snapshot ??= store.Current;
            if (!snapshot.Blocks.TryGetValue(key, out var block))
                throw new InvalidDataException("Cooked asset missing: " + key);
            bytes = MountedAssetStore.Read(block, out long io, out long decode);
            Interlocked.Add(ref readTicks, io);
            Interlocked.Add(ref decodeTicks, decode);
            Interlocked.Add(ref bytesRead, block.Stored);
            return bytes;
        }
        Interlocked.Add(ref readTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Add(ref bytesRead, bytes.Length);
        return bytes;
    }
    public void Publish(MountedAssetStore.Refresh refresh)
    {
        lock (prepared)
        {
            CancelPending();
            store!.Publish(refresh);
        }
    }
    public byte[] Read(string key)
    {
        Task<byte[]>? task;
        lock (prepared)
        {
            if (prepared.Remove(key, out var bytes))
            {
                reserved -= bytes.Length;
                pending.Remove(key);
                Interlocked.Increment(ref hits);
                return bytes;
            }
            pending.TryGetValue(key, out task);
        }
        if (task is null)
            return ReadCore(key);
        task.GetAwaiter().GetResult();
        lock (prepared)
        {
            if (prepared.Remove(key, out var bytes))
            {
                reserved -= bytes.Length;
                pending.Remove(key);
                Interlocked.Increment(ref hits);
                return bytes;
            }
        }
        return ReadCore(key);
    }
    public bool Prefetch(string key)
    {
        lock (prepared)
        {
            if (prepared.ContainsKey(key))
                return true;
            if (pending.TryGetValue(key, out var old))
            {
                if (old.IsFaulted)
                    old.GetAwaiter().GetResult();
                return false;
            }
            long size = Size(key);
            long working = size;
            if (store?.Current.Blocks.TryGetValue(key, out var block) == true && block.Brotli) working += block.Stored;
            if (working > QueueLimit)
                throw new InvalidDataException("Asset block exceeds the 64 MiB preparation limit: " + key);
            if (working > QueueLimit - reserved)
                return false;
            reserved += working;
            int owner = generation;
            var snapshot = store?.Current;
            pending[key] = Task.Run(async () =>
            {
                bool entered = false;
                try
                {
                    await workers.WaitAsync(stop.Token);
                    entered = true;
                    stop.Token.ThrowIfCancellationRequested();
                    lock (prepared)
                    {
                        if (owner != generation)
                        {
                            reserved -= working;
                            return [];
                        }
                    }
                    var data = ReadCore(key, snapshot);
                    lock (prepared)
                    {
                        if (owner == generation)
                        {
                            prepared[key] = data;
                            reserved -= working - size;
                        }
                        else reserved -= working;
                    }
                    return data;
                }
                catch
                {
                    lock (prepared)
                        reserved -= working;
                    throw;
                }
                finally
                {
                    if (entered)
                        workers.Release();
                }
            });
            var task = pending[key];
            inFlight.Add(task);
            _ = task.ContinueWith(t =>
            {
                lock (prepared)
                    inFlight.Remove(t);
            }, TaskContinuationOptions.ExecuteSynchronously);
            return false;
        }
    }
    public void CancelPending()
    {
        lock (prepared)
        {
            generation++;
            foreach (var data in prepared.Values)
                reserved -= data.Length;
            prepared.Clear();
            foreach (var task in pending.Values)
                _ = task.ContinueWith(t =>
                {
                    _ = t.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);
            pending.Clear();
        }
    }
    public sealed class StagingLease : IDisposable
    {
        CookedAssets? owner;
        public byte[] Bytes { get; }
        internal StagingLease(CookedAssets owner, byte[] bytes) { this.owner = owner; Bytes = bytes; }
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref owner, null);
            if (previous is not null) lock (previous.prepared) if (!previous.disposed) previous.reserved -= Bytes.Length;
        }
    }
    public bool TryTakeLease(string key, out StagingLease lease)
    {
        lock (prepared)
        {
            if (prepared.Remove(key, out var bytes))
            {
                pending.Remove(key);
                Interlocked.Increment(ref hits);
                lease = new(this, bytes);
                return true;
            }
            if (pending.TryGetValue(key, out var t) && t.IsFaulted) t.GetAwaiter().GetResult();
        }
        lease = null!;
        Prefetch(key);
        return false;
    }
    public bool TryTake(string key, out byte[] bytes)
    {
        if (TryTakeLease(key, out var lease))
        {
            using (lease) bytes = lease.Bytes;
            return true;
        }
        bytes = [];
        return false;
    }
    public void Dispose()
    {
        stop.Cancel();
        Task[] tasks;
        lock (prepared)
            tasks = inFlight.ToArray();
        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException)
        {
        }
        lock (prepared)
        {
            prepared.Clear();
            pending.Clear();
            reserved = 0;
            disposed = true;
        }
        store?.Dispose();
        workers.Dispose();
        stop.Dispose();
    }
}
