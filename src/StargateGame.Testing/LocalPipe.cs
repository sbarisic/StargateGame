using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace StargateGame.Testing;

/// <summary>One bounded JSON request per connection; current-user-only, local IPC.</summary>
public sealed class LocalPipe : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly Task loop;
    public LocalPipe(string name, Func<TestRequest, CancellationToken, Task<TestResult>> handle)
        => loop = Task.Run(() => Listen(name, handle));
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> connections = new();
    readonly SemaphoreSlim slots = new(8);
    int connectionId;
    async Task Listen(string name, Func<TestRequest, CancellationToken, Task<TestResult>> handle)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await slots.WaitAsync(stop.Token);
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stop.Token); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                int id = Interlocked.Increment(ref connectionId);
                var task = Serve(pipe, handle);
                connections[id] = task;
                _ = task.ContinueWith(_ => { connections.TryRemove(id, out var ignored); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await Task.WhenAll(connections.Values); }
    }
    async Task Serve(NamedPipeServerStream pipe, Func<TestRequest, CancellationToken, Task<TestResult>> handle)
    {
        using (pipe)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        try
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var request = await Read<TestRequest>(pipe, deadline.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300)));
            TestResult result;
            try { result = await handle(request, deadline.Token); }
            catch (Exception e) { result = TestResult.Fail(request, e); }
            using var replyDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            replyDeadline.CancelAfter(TimeSpan.FromSeconds(3));
            await Write(pipe, result, replyDeadline.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
        finally { slots.Release(); }
    }
    public static async Task<TestResult> Send(string name, TestRequest request, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300) + 5));
        // Wait locally using short connections. No waiter occupies a server connection slot.
        if (request.Action == TestAction.Operation && request.Int("waitSeconds") > 0)
        {
            var end = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(request.Int("waitSeconds"), 0, 10));
            var poll = request with { Data = TestJson.Element(new { id = request.Value<Guid>("id"), waitSeconds = 0 }) };
            while (true)
            {
                var result = await SendOnce(name, poll, timeout.Token);
                if (result.Outcome != "passed" || result.Data.GetProperty("state").GetString() is not ("queued" or "running") || DateTimeOffset.UtcNow >= end) return result;
                await Task.Delay(50, timeout.Token);
            }
        }
        var accepted = await SendOnce(name, request, timeout.Token);
        if (accepted.Outcome != "accepted" || request.Async) return accepted;
        while (true)
        {
            var result = await SendOnce(name, request with { Id = Guid.NewGuid(), Action = TestAction.Operation, Data = TestJson.Element(new { id = request.Id, waitSeconds = 0 }) }, timeout.Token);
            result.EnsureSuccess();
            var operation = result.Data.Deserialize<OperationSnapshot>(TestJson.Options)!;
            if (operation.Result is { } completed)
                return operation.CleanupError is null ? completed : completed with { Outcome = "failed", ErrorCode = "cleanup-failed", Error = operation.CleanupError };
            await Task.Delay(50, timeout.Token);
        }
    }
    static async Task<TestResult> SendOnce(string name, TestRequest request, CancellationToken token)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(token);
        await Write(pipe, request, token);
        return await Read<TestResult>(pipe, token);
    }
    static async Task Write<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, TestJson.Options);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Control response exceeds 4 MiB.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }
    static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int size = BitConverter.ToInt32(header);
        if (size is < 1 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid control frame.");
        byte[] bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, TestJson.Options) ?? throw new InvalidDataException("Empty control frame.");
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await loop; stop.Dispose(); }
}

/// <summary>Executes IPC requests only on the owning simulation/render thread.</summary>
public sealed class TestCommandQueue
{
    readonly System.Collections.Concurrent.ConcurrentQueue<(TestRequest Request, CancellationToken Token, TaskCompletionSource<TestResult> Done)> queue = new();
    public async Task<TestResult> Enqueue(TestRequest request, CancellationToken token)
    {
        if (queue.Count >= 32) throw new InvalidOperationException("Control queue full.");
        var done = new TaskCompletionSource<TestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue((request, token, done));
        return await done.Task.WaitAsync(token);
    }
    public void Drain(Func<TestRequest, TestResult> execute)
    {
        while (queue.TryDequeue(out var item))
        {
            if (item.Token.IsCancellationRequested) { item.Done.TrySetCanceled(item.Token); continue; }
            try { item.Done.TrySetResult(execute(item.Request)); }
            catch (Exception e) { item.Done.TrySetResult(TestResult.Fail(item.Request, e)); }
        }
    }
}
