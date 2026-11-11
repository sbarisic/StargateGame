using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Diagnostics;
using StargateGame.Shared;
namespace StargateGame.Harness;
/// <summary>Delays complete TCP frames while preserving their ordering. Seeded jitter is ±10 ms per direction.</summary>
sealed class TestDelayProxy : IDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly List<Task> clients = [];
    readonly Task accept;
    readonly int target, terrainBytesPerSecond;
    readonly bool delayed;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public TestDelayProxy(int target, bool delayed = true, int terrainBytesPerSecond = 0)
    {
        this.target = target;
        this.delayed = delayed;
        this.terrainBytesPerSecond = terrainBytesPerSecond;
        listener.Start();
        accept = Accept();
    }
    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                clients.Add(Bridge(client, clients.Count));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
    async Task Bridge(TcpClient downstream, int seed)
    {
        using (downstream)
        using (var upstream = new TcpClient())
        {
            try
            {
                downstream.NoDelay = true;
                upstream.NoDelay = true;
                await upstream.ConnectAsync(IPAddress.Loopback, target, stop.Token);
                var incoming = Pump(downstream.GetStream(), upstream.GetStream(), seed * 2);
                var outgoing = Pump(upstream.GetStream(), downstream.GetStream(), seed * 2 + 1);
                await Task.WhenAny(incoming, outgoing);
                downstream.Close();
                upstream.Close();
                await Task.WhenAll(incoming, outgoing);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }
    async Task Pump(Stream source, Stream destination, int seed)
    {
        var random = new Random(4100 + seed);
        var queue = Channel.CreateBounded<(Message Message, long Due)>(256);
        long terrainDue = 0;
        var reader = Read();
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stop.Token))
            {
                var delay = TimeSpan.FromSeconds((double)(item.Due - Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stop.Token);
                await Protocol.WriteAsync(destination, item.Message, stop.Token);
            }
        }
        finally
        {
            await reader;
        }
        async Task Read()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var message = await Protocol.ReadAsync(source, stop.Token);
                    long due = Stopwatch.GetTimestamp() + (long)((delayed ? .05 + (random.NextDouble() - .5) * .02 : 0) * Stopwatch.Frequency);
                    if (terrainBytesPerSecond > 0 && message.Kind == "terrain-chunk" && message.Chunk is { } chunk)
                    {
                        terrainDue = Math.Max(due, terrainDue) + (long)((double)chunk.Data.Length / terrainBytesPerSecond * Stopwatch.Frequency);
                        due = terrainDue;
                    }
                    await queue.Writer.WriteAsync((message, due), stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
            finally
            {
                queue.Writer.TryComplete();
            }
        }
    }
    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        accept.GetAwaiter().GetResult();
        Task.WhenAll(clients).GetAwaiter().GetResult();
        stop.Dispose();
    }
}
