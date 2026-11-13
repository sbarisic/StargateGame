using System.Security.Cryptography;
using System.Text.Json;

namespace StargateGame.Testing;

public sealed record OperationSnapshot(Guid Id, int Generation, string State, DateTimeOffset Submitted,
    DateTimeOffset? Started, DateTimeOffset? Finished, TestResult? Result, string? CleanupError = null);

/// <summary>Session-local, bounded, exactly-once admission. Readers never acquire the mutation gate.</summary>
public sealed class Operations : IAsyncDisposable
{
    sealed class Entry(TestRequest request, string fingerprint, CancellationToken stop)
    {
        public readonly TestRequest Request = request;
        public readonly string Fingerprint = fingerprint;
        public readonly CancellationTokenSource Cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        public OperationSnapshot Snapshot = new(request.Id, request.Generation, "queued", DateTimeOffset.UtcNow, null, null, null);
        public Task<TestResult> Work = null!;
        public bool ExplicitCancel;
    }
    bool disposed;
    readonly object sync = new();
    readonly Dictionary<Guid, Entry> entries = [];
    readonly SemaphoreSlim mutation = new(1);
    readonly CancellationTokenSource stop = new();
    public async Task<TestResult> Submit(TestRequest request, Func<TestRequest, CancellationToken, Task<TestResult>> execute,
        Func<TestRequest, Task> release, CancellationToken caller)
    {
        Entry entry;
        string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request with { Async = false }, TestJson.Options)));
        lock (sync)
        {
            if (entries.TryGetValue(request.Id, out entry!))
            {
                if (entry.Fingerprint != fingerprint) throw new InvalidOperationException("request-id-conflict: payload differs from the admitted request.");
            }
            else
            {
                if (entries.Count >= 4096) throw new InvalidOperationException("Operation history full; start a new session.");
                if (entries.Values.Count(e => e.Snapshot.Finished is null) >= 32) throw new InvalidOperationException("Operation queue full.");
                entry = new(request, fingerprint, stop.Token);
                entry.Cancel.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300)));
                entries.Add(request.Id, entry);
                entry.Work = Task.Run(() => Run(entry, execute, release));
            }
        }
        if (request.Async) return new(request.Id, request.Session, request.Generation, "accepted", TestJson.Element(Read(request.Id)), Client: request.Client);
        try { return await entry.Work.WaitAsync(caller); }
        catch (OperationCanceledException) { Cancel(request.Id); throw; }
    }
    async Task<TestResult> Run(Entry e, Func<TestRequest, CancellationToken, Task<TestResult>> execute, Func<TestRequest, Task> release)
    {
        bool acquired = false; TestResult result; string state; string? cleanupError = null;
        try
        {
            await mutation.WaitAsync(e.Cancel.Token); acquired = true;
            lock (sync) e.Snapshot = e.Snapshot with { State = "running", Started = DateTimeOffset.UtcNow };
            result = await execute(e.Request, e.Cancel.Token);
            state = result.Outcome == "passed" ? "completed" : "failed";
        }
        catch (Exception error)
        {
            state = error is OperationCanceledException ? e.ExplicitCancel || stop.IsCancellationRequested ? "cancelled" : "timed-out" : "failed";
            result = TestResult.Fail(e.Request, error) with { ErrorCode = state };
        }
        finally
        {
            if (acquired)
            {
                try { await release(e.Request); } catch (Exception error) { cleanupError = error.ToString(); }
                mutation.Release();
            }
        }
        lock (sync) e.Snapshot = e.Snapshot with { State = state, Finished = DateTimeOffset.UtcNow, Result = result, CleanupError = cleanupError };
        return result;
    }
    public TestRequest Request(Guid id) { lock (sync) return entries.TryGetValue(id, out var e) ? e.Request : throw new KeyNotFoundException("Unknown operation."); }
    public OperationSnapshot Read(Guid id) { lock (sync) return entries.TryGetValue(id, out var e) ? e.Snapshot : throw new KeyNotFoundException("Unknown operation."); }
    public OperationSnapshot[] Active() { lock (sync) return entries.Values.Where(e => e.Snapshot.Finished is null).Select(e => e.Snapshot).ToArray(); }
    public void Cancel(Guid id) { lock (sync) { if (!entries.TryGetValue(id, out var e)) throw new KeyNotFoundException("Unknown operation."); e.ExplicitCancel = true; e.Cancel.Cancel(); } }
    public void CancelGeneration(int generation, Guid except) { lock (sync) foreach (var e in entries.Values.Where(e => e.Request.Generation == generation && e.Request.Id != except && e.Snapshot.Finished is null)) { e.ExplicitCancel = true; e.Cancel.Cancel(); } }
    public void CancelController(string controller) { lock (sync) foreach (var e in entries.Values.Where(e => e.Request.Controller == controller && e.Snapshot.Finished is null)) { e.ExplicitCancel = true; e.Cancel.Cancel(); } }
    public async Task<OperationSnapshot> Wait(Guid id, int seconds, CancellationToken token)
    {
        Task<TestResult> task; lock (sync) task = entries[id].Work;
        try { await task.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 10)), token); } catch (TimeoutException) { }
        return Read(id);
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        stop.Cancel(); Task[] tasks; lock (sync) tasks = entries.Values.Select(e => (Task)e.Work).ToArray();
        await Task.WhenAll(tasks);
        foreach (var e in entries.Values) e.Cancel.Dispose();
        stop.Dispose(); mutation.Dispose();
    }
}
