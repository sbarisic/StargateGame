using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.WorldGen;
using StargateGame.Client;
using StargateGame.Physics;
using StargateGame.Shared;
using StargateGame.Server;

static class StreamingChecks
{
    public static async Task Run(bool stress = false, bool delayed = false, bool explore = false, bool slow = false)
    {
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, 7319, "temperate", true, Path.Combine(Path.GetTempPath(), "stargate-streaming-" + Guid.NewGuid().ToString("N")));
        server.Start();
        await using var proxy = delayed || slow ? new DelayProxy(server.Port, slow ? 512 * 1024 : 0) : null;
        int port = proxy?.Port ?? server.Port;
        var a = await GameConnection.ConnectAsync("127.0.0.1", port, "StreamA", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        using var first = new ClientSession(a);
        var b = await GameConnection.ConnectAsync("127.0.0.1", port, "StreamB", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        using var second = new ClientSession(b);
        async Task Pump(int ms)
        {
            var time = Stopwatch.StartNew();
            while (time.ElapsedMilliseconds < ms)
            {
                first.Poll();
                second.Poll();
                first.Step(0, 0, 0, 0, false, false);
                second.Step(0, 0, 0, 0, false, false);
                if (first.Failure is { } f)
                    throw new Exception("first: " + f);
                if (second.Failure is { } g)
                    throw new Exception("second: " + g);
                await Task.Delay(8);
            }
        }
        await Pump(2500);
        if (explore)
        {
            var start = first.Local.Position;
            long epoch = first.Physics.Epoch;
            var travel = Stopwatch.StartNew();
            // Dense forests and swimming can slow this route; assert travelled coverage, not a fixed arrival time.
            while (travel.Elapsed.TotalSeconds < 90 && Vector3.Distance(first.Local.Position, second.Local.Position) < 360)
            {
                first.Poll();
                second.Poll();
                bool sideways = travel.Elapsed.TotalSeconds < 3;
                first.Step(sideways ? -1 : 0, sideways ? 0 : 1, 0, 0, true, false);
                second.Step(0, sideways ? 0 : -1, 0, 0, true, false);
                if (first.Failure is { } f || second.Failure is not null)
                    throw new Exception(first.Failure ?? second.Failure);
                await Task.Delay(16);
            }
            await Pump(1000);
            Console.WriteLine($"TRAVERSE a={first.Local.Position} b={second.Local.Position} separation={Vector3.Distance(first.Local.Position, second.Local.Position):F2} epoch={first.Physics.Epoch}");
            Verify.Check(Vector3.Distance(first.Local.Position, second.Local.Position) > 350, "clients explore opposite regions");
            Verify.Check(first.Physics.Epoch > epoch && server.CollisionCount > 70, "collision coverage follows separated players");
            Verify.Check(first.Local.Position.Y >= first.World.SampleHeight(first.Local.Position.X, first.Local.Position.Z) - .1f && second.Local.Position.Y >= second.World.SampleHeight(second.Local.Position.X, second.Local.Position.Z) - .1f, "traversal never falls through streamed terrain");
        }
        Verify.Check(first.PlayerCount == 2 && second.PlayerCount == 2, "generated planet shared by two clients");
        Verify.Check(first.Developer && !second.Developer, "only first loopback client controls planets");
        Verify.Check(first.Local.Grounded && second.Local.Grounded, "generated clearing supports both players");
        for (int i = 0; i < 120 && first.TransitionPending; i++)
            await Pump(100);
        first.SwitchPlanet(9917, "icy");
        first.CancelPlanetLoad();
        await Pump(1000);
        Verify.Check(first.World.Definition.Preset == "temperate", "cancelled destination leaves active planet playable");
        for (int i = 0; i < 120 && first.TransitionPending; i++)
            await Pump(100);
        first.SwitchPlanet(44123, "icy");
        for (int i = 0; i < 80 && !first.TransitionPending; i++)
            await Pump(50);
        Verify.Check(first.TransitionPending, "destination enters staged preparation");
        var joining = GameConnection.ConnectAsync("127.0.0.1", port, "LateLoading", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        await Pump(50);
        first.CancelPlanetLoad();
        for (int i = 0; i < 160 && !joining.IsCompleted; i++)
            await Pump(100);
        using (var late = new ClientSession(await joining.WaitAsync(TimeSpan.FromSeconds(5))))
        {
            await Pump(500);
            late.Poll();
            Verify.Check(first.PlayerCount == 3 && late.World.Definition.Id == first.World.Definition.Id, "late join waits through cancelled destination preparation");
        }
        await Pump(500);
        Verify.Check(first.World.Definition.Preset == "temperate" && !first.TransitionPending, "mid-preparation cancellation retains the original world");
        var remembered = first.LastSnapshot.Bodies;
        first.SwitchPlanet(42, "desert");
        for (int i = 0; i < 60 && first.World.Definition.Preset != "desert"; i++)
        {
            if (i % 10 == 0 && !first.TransitionPending)
                first.SwitchPlanet(42, "desert");
            await Pump(500);
        }
        Verify.Check(first.World.Definition.Preset == "desert" && second.World.Definition.Preset == "desert", "synchronized developer planet switch");
        await Pump(500);
        for (int i = 0; i < 60 && first.TransitionPending; i++)
            await Pump(250);
        first.SwitchPlanet(7319, "temperate");
        for (int i = 0; i < 60 && first.World.Definition.Preset != "temperate"; i++)
        {
            if (i % 10 == 0 && !first.TransitionPending)
                first.SwitchPlanet(7319, "temperate");
            await Pump(500);
        }
        Verify.Check(first.World.Definition.Preset == "temperate" && first.Local.Grounded, "return to generated planet");
        Verify.Check(remembered.All(b => Vector3.Distance(b.Position, first.Physics.Bodies().Single(p => p.Id == b.Id).Position) < .03f), "inactive planet preserves settled crate state");
        long lowMemory = 0, highMemory = 0;
        if (stress)
            for (int cycle = 0; cycle < 20; cycle++)
            {
                await Pump(500);
                for (int i = 0; i < 120 && first.TransitionPending; i++)
                    await Pump(100);
                int seed = 7319 + cycle % 3;
                string preset = PlanetSettings.Presets[(cycle + 1) % 3];
                string id = $"planet-{preset}-{seed}";
                for (int i = 0; i < 120 && first.World.Definition.Id != id; i++)
                {
                    if (i % 20 == 0 && !first.TransitionPending)
                        first.SwitchPlanet(seed, preset);
                    await Pump(250);
                }
                Verify.Check(first.World.Definition.Id == id && second.World.Definition.Id == id, "repeated planet switch " + cycle);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long memory = Process.GetCurrentProcess().PrivateMemorySize64;
                if (cycle == 5)
                    lowMemory = memory;
                if (cycle >= 5)
                    highMemory = Math.Max(highMemory, memory);
                Console.WriteLine($"STREAM METRICS cycle={cycle} privateMiB={memory / 1048576} managedMiB={GC.GetTotalMemory(true) / 1048576} contexts={GenerationService.ActiveContexts} colliders={server.CollisionCount} networkMiB={first.TerrainBytesReceived / 1048576f:F2}");
            }
        if (stress)
            Verify.Check(highMemory - lowMemory < 200L * 1024 * 1024, "twenty switches have bounded memory after warmup");
        await server.DisposeAsync();
        for (int i = 0; i < 100 && (first.Failure is null || second.Failure is null); i++)
        {
            first.Poll();
            second.Poll();
            await Task.Delay(20);
        }
        Verify.Check(first.Failure is not null && second.Failure is not null, "server loss during generated session");
    }
}
