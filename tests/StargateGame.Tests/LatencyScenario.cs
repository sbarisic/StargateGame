using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using StargateGame.Client;
using StargateGame.Server;
using StargateGame.Shared;
using System.Numerics;

static class LatencyScenario
{
    public static async Task Run()
    {
        await using var server = new GameServer(IPAddress.Loopback, 0, 2);
        server.Start();
        await using var proxy = new DelayProxy(server.Port);
        using var a = new ClientSession(await NetworkChecks.Connect(proxy.Port, "DelayedAlice"));
        using var b = new ClientSession(await NetworkChecks.Connect(proxy.Port, "DelayedBob"));
        var timer = Stopwatch.StartNew();
        bool immediate = false;
        double nextVisual = 0;
        List<(Vector3 Player, Vector3 DrawPlayer, Vector3 Box, Vector3 DrawBox)> motion = [];
        List<string> trace = ["seconds,server_tick,predicted_tick,render_tick,player_x,player_z,draw_player_z,box_z,draw_box_z"];
        while (timer.Elapsed.TotalSeconds < 9)
        {
            a.Poll();
            b.Poll();
            VerifyFailure(a);
            VerifyFailure(b);
            foreach (var client in new[] { a, b })
            {
                var before = client.Local.Position;
                float strafe = timer.Elapsed.TotalSeconds < 3 ? Math.Clamp((-7 - before.X) * 2, -1, 1) : 0;
                float forward = timer.Elapsed.TotalSeconds is >= 3 and < 5 ? 1 : 0;
                client.Step(strafe, forward, 0, 0, false, false);
                if (Vector3.Distance(before, client.Local.Position) > .01f && timer.Elapsed.TotalSeconds < 1)
                    immediate = true;
            }
            double now = timer.Elapsed.TotalSeconds;
            if (now >= nextVisual)
            {
                nextVisual = now + 1.0 / 144;
                if (now is > 3.5 and < 4.8)
                {
                    var pose = a.RenderPrediction();
                    var box = a.Physics.Bodies().Single(p => p.Id == 4);
                    var drawBox = pose.Bodies.Single(p => p.Id == 4);
                    motion.Add((a.Local.Position, pose.Player.Position, box.Position, drawBox.Position));
                    trace.Add(FormattableString.Invariant($"{now:F6},{a.ServerTick},{a.Physics.Tick},{a.PresentationTick:F6},{a.Local.Position.X:F6},{a.Local.Position.Z:F6},{pose.Player.Position.Z:F6},{box.Position.Z:F6},{drawBox.Position.Z:F6}"));
                }
            }
            await Task.Delay(2);
        }
        Verify.Check(immediate, "local movement responds before delayed server snapshots");
        Verify.Check(a.PlayerCount == 2 && b.PlayerCount == 2, "two real client sessions through 100 ms RTT / 20 ms jitter");
        Verify.Check(a.Physics.Bodies().Any(p => Math.Abs(p.Position.Z - a.World.Definition.Props.Single(d => d.Id == p.Id).Position.Z) > .1f), "delayed clients push shared props");
        if (Directory.Exists("artifacts"))
            File.WriteAllLines("artifacts/movement-latency.csv", trace);
        var deltas = motion.Zip(motion.Skip(1)).Select(p => (RawPlayer: Vector3.Distance(p.First.Player, p.Second.Player), DrawPlayer: Vector3.Distance(p.First.DrawPlayer, p.Second.DrawPlayer), RawBox: Vector3.Distance(p.First.Box, p.Second.Box), DrawBox: Vector3.Distance(p.First.DrawBox, p.Second.DrawBox))).ToArray();
        Verify.Check(deltas.Length > 50, "latency scenario samples presentation during sustained box contact");
        int rawHeld = deltas.Count(d => d.RawBox < .00001f), drawHeld = deltas.Count(d => d.DrawBox < .00001f);
        Console.WriteLine($"MOTION push samples={deltas.Length} held_box raw={rawHeld} rendered={drawHeld}; player_max_step={deltas.Max(d => d.DrawPlayer):F4}m box_max_step={deltas.Max(d => d.DrawBox):F4}m; matched={a.PredictionConfirmations} corrections={a.PredictionCorrections}");
        Verify.Check(drawHeld <= Math.Max(1, rawHeld / 3) && deltas.Max(d => d.DrawPlayer) < .15f && deltas.Max(d => d.DrawBox) < .15f, "predicted box presentation removes held physics frames without correction snaps");
        foreach (var client in new[] { a, b })
        {
            var serverPlayer = client.LastSnapshot.Players.Single(p => p.Id == client.Local.Id);
            Verify.Check(Vector3.Distance(serverPlayer.Position, client.Local.Position) < .02f, "delayed player converges to authority");
            foreach (var body in client.Physics.Bodies())
            {
                var authoritative = client.LastSnapshot.Bodies.Single(p => p.Id == body.Id);
                Verify.Check(Vector3.Distance(authoritative.Position, body.Position) < .02f && Math.Abs(Quaternion.Dot(authoritative.Rotation, body.Rotation)) > MathF.Cos(MathF.PI / 180), "delayed crate converges within 2 cm / 2 degrees: " + body.Id);
            }
        }
    }
    static void VerifyFailure(ClientSession session)
    {
        if (session.Failure is { } reason)
            throw new Exception("Latency client failed: " + reason);
    }
}

/// <summary>Delays complete TCP frames while preserving their ordering. Seeded jitter is ±10 ms per direction.</summary>
sealed class DelayProxy : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly List<Task> clients = [];
    readonly Task accept;
    readonly int target, terrainBytesPerSecond;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public DelayProxy(int target, int terrainBytesPerSecond = 0)
    {
        this.target = target;
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
                    long due = Stopwatch.GetTimestamp() + (long)((.05 + (random.NextDouble() - .5) * .02) * Stopwatch.Frequency);
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
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        await accept;
        await Task.WhenAll(clients);
        stop.Dispose();
    }
}
