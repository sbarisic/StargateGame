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
    async Task Listen(string name, Func<TestRequest, CancellationToken, Task<TestResult>> handle)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(300));
                var request = await Read<TestRequest>(pipe, deadline.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300)));
                TestResult result;
                try { result = await handle(request, deadline.Token); }
                catch (Exception e) { result = TestResult.Fail(request, e); }
                await Write(pipe, result, stop.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
        }
    }
    public static async Task<TestResult> Send(string name, TestRequest request, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds, 1, 300) + 5));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await Write(pipe, request, timeout.Token);
        return await Read<TestResult>(pipe, timeout.Token);
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
