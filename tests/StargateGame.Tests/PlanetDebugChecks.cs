using System.Diagnostics;
using System.Net;
using System.Numerics;
using StargateGame.Client;
using StargateGame.Physics;
using StargateGame.Server;
using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Persistence;

static class PlanetDebugChecks
{
    public static async Task Run()
    {
        if (Environment.GetCommandLineArgs().Contains("--network-only")) { await Network(Path.GetFullPath("artifacts/planet-debug-tests/network-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))); return; }
        foreach (string biome in PlanetSettings.Presets)
        {
            var old = new PlanetPlan(7319, biome, PlanetSettings.Version, new("wilderness"), new());
            var neutral = new PlanetPlan(7319, biome, settlements: new("wilderness"));
            var coordinate = new ChunkCoordinate(0, 0);
            Verify.Check(ChunkCodec.Hash(ChunkCodec.Encode(old.BuildChunk(coordinate))) == ChunkCodec.Hash(ChunkCodec.Encode(neutral.BuildChunk(coordinate))), "explicit defaults match " + biome + " terrain");
            foreach (var settings in new[] { new PlanetGenerationSettings(.25f, -3, 0, 0), new PlanetGenerationSettings(2, 3, 2, 2) })
            {
                var a = new PlanetPlan(-7319, biome, settlements: new("wilderness"), generation: settings);
                var b = new PlanetPlan(-7319, biome, settlements: new("wilderness"), generation: settings);
                Verify.Check(ChunkCodec.Hash(ChunkCodec.Encode(a.BuildChunk(coordinate))) == ChunkCodec.Hash(ChunkCodec.Encode(b.BuildChunk(coordinate))), "repeatable extreme " + biome);
                Verify.Check(a.Definition.Spawns.All(p => a.Sample((int)p.X, (int)p.Z).Water == int.MinValue), "dry spawn " + biome);
                if (settings.TreeDensity == 0) Verify.Check(a.BuildChunk(coordinate).Placements.Count == 0, "zero scenery " + biome);
            }
        }
        Verify.Check(WeatherFrame.Sample("temperate", 1, 5, WinterWeather.Storm).Snow && !WeatherFrame.Sample("icy", 1, 5, WinterWeather.Off).Snow, "forced snow and clear ignore biome");
        string root = Path.GetFullPath("artifacts/planet-debug-tests/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        Guid home, other; PlanetDefinition replaced;
        await using (var repository = new WorldRepository(root, "persistence"))
        {
            var first = await repository.CreateAsync(new PlanetPlan(100, "temperate", settlements: new("wilderness")).Definition);
            home = first.Id;
            var second = await repository.CreateAsync(new PlanetPlan(200, "desert", settlements: new("wilderness")).Definition);
            other = second.Id;
            await repository.SaveAsync(first.Changes with { Revision = 1, Removed = [new("tree", 123, new(0, 0))] }, 0);
            var player = await repository.AuthenticatePlayerAsync(new string('A', 64), home, "Tester");
            var fresh = new PlanetPlan(300, "icy", settlements: new("wilderness"), generation: new(1.5f, -1, .5f, 0)).Definition;
            replaced = fresh with { UniverseId = first.Definition.UniverseId, LocationId = home,
                Environment = new PlanetEnvironmentSettings(18.5, true, PlanetWeather.DesertDust).Apply(first.Definition.Environment!, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                Props = fresh.Props.Select(p => p with { PersistentId = Guid.NewGuid() }).ToArray() };
            var saved = await repository.ReplaceHomeAsync(replaced, 1, player);
            Verify.Check(saved.Id == home && saved.Changes.Removed.Length == 0 && saved.Changes.Revision == 0, "replace preserves home identity and resets edits");
            Verify.Check((await repository.LoadAsync(other)).Definition == second.Definition || (await repository.LoadAsync(other)).Definition.SettingsHash == second.Definition.SettingsHash, "unrelated planet preserved");
            Verify.Check(File.Exists(repository.DatabasePath + ".before-regeneration"), "previous working save backed up");
            var resumed = await repository.AuthenticatePlayerAsync(player.Key, home, "Tester");
            Verify.Check(resumed.Id == player.Id && resumed.State.Position == replaced.Spawns[0], "persistent explorer respawns safely");
            bool rejected = false;
            try { await repository.ReplaceHomeAsync(replaced, 99, player); } catch (InvalidOperationException) { rejected = true; }
            Verify.Check(rejected && (await repository.LoadAsync(home)).Definition.Seed == 300, "failed revision preserves working planet");
            var environment = await repository.ApplyHomeEnvironmentAsync(home, new(3.5, false, PlanetWeather.Snowstorm));
            Verify.Check(environment.SettingsHash == replaced.SettingsHash && environment.Generation == replaced.Generation, "environment does not invalidate terrain");
            await repository.SetHomeTimeAsync(home);
            Verify.Check((await repository.LoadAsync(home)).Definition.Environment!.FixedHour is null, "startup does not overwrite saved day cycle");
            Directory.CreateDirectory(repository.DatabasePath + ".before-regeneration.tmp");
            bool backupFailed = false;
            try { await repository.ReplaceHomeAsync(replaced with { Name = "must not publish" }, 0, player); }
            catch (Microsoft.Data.Sqlite.SqliteException) { backupFailed = true; }
            Verify.Check(backupFailed && (await repository.LoadAsync(home)).Definition.Name != "must not publish", "backup failure preserves active save");
        }
        await using (var repository = new WorldRepository(root, "persistence"))
            Verify.Check((await repository.LoadAsync(home)).Definition.Generation.Relief == 1.5f, "custom generation survives restart");
        await Network(root);
        Console.WriteLine($"PLANET DEBUG {Verify.Count} CHECKS PASSED / {root}");
    }
    static async Task Network(string root)
    {
        await using var server = new GameServer(IPAddress.Loopback, 0, 8, seed: 7319, devPlanets: true, saveDirectory: root, universe: "network", settlements: new("wilderness"), cataloguePath: Path.GetFullPath("artifacts/galaxy/galaxy.bin"));
        server.Start();
        using var identity = new PlayerIdentity();
        using var client = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Generator tester", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity));
        ClientSession? observer = null;
        async Task Until(Func<bool> condition, string label, int seconds = 80)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.Elapsed.TotalSeconds < seconds)
            {
                client.Poll(); client.Step(0, 0, 0, 0, false, false);
                observer?.Poll(); observer?.Step(0, 0, 0, 0, false, false);
                if (client.Failure is not null) throw new Exception(client.Failure);
                await Task.Delay(5);
            }
            Verify.Check(condition(), label + " / " + client.PlanetDebugState?.Detail);
        }
        client.ReadPlanetDebug();
        await Until(() => client.DebugHome is not null, "home settings received");
        var home = client.DebugHome!;
        var homeAddress = client.ReturnAddress.ToArray();
        var settings = PlanetDebugSettings.Read(home) with { Environment = new(18, true, PlanetWeather.Snowstorm) };
        client.ApplyPlanetDebug(settings, false);
        await Until(() => client.PlanetDebugState is { Busy: false }, "environment request completed");
        Verify.Check(client.World.Definition.Environment?.Weather == PlanetWeather.Snowstorm && client.World.Definition.SettingsHash == home.SettingsHash, "environment applies without regeneration");
        foreach (string biome in new[] { "desert", "icy", "temperate" })
        {
            settings = settings with { Seed = settings.Seed + 1, Preset = biome, Generation = new(1.2f, -1, .5f, .5f) };
            client.ApplyPlanetDebug(settings, true);
            await Until(() => client.PlanetDebugState is { Busy: false }, "regenerate " + biome);
            Verify.Check(client.PlanetDebugState?.Phase == "Ready" && client.World.Definition.Preset == biome && client.World.Definition.LocationId == home.LocationId, "authoritative " + biome + " replacement");
        }
        // Regenerate the home while inspecting a different planet, without gate travel.
        var route = client.Destinations.First(d => d.DhdCompatible && StargateGame.Galaxy.Address.Canonical(d.Symbols) != StargateGame.Galaxy.Address.Canonical(homeAddress));
        client.InspectDestination(route.Symbols);
        await Until(() => client.World.Definition.LocationId != home.LocationId && !client.TransitionPending, "inspect another planet", 120);
        var settle = Stopwatch.StartNew();
        await Until(() => settle.Elapsed.TotalSeconds > 3, "arrival collision settles");
        client.ApplyPlanetDebug(settings, true);
        await Until(() => client.PlanetDebugState is { Busy: false }, "regenerate home from elsewhere");
        Verify.Check(client.World.Definition.LocationId == home.LocationId && client.ReturnAddress.SequenceEqual(homeAddress), "home address and relocation preserved");
        using var observerIdentity = new PlayerIdentity();
        observer = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Observer", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: observerIdentity));
        await Until(() => client.PlayerCount == 2, "second player connected");
        client.ApplyPlanetDebug(settings with { Seed = 998 }, true);
        await Until(() => client.PlanetDebugState is { Busy: false }, "multiplayer request answered");
        Verify.Check(client.PlanetDebugState?.Phase == "Failed" && client.World.Definition.Seed != 998, "server refuses multiplayer regeneration");
        observer.Dispose(); observer = null;
        await Until(() => client.PlayerCount == 1, "observer disconnected");
        client.ApplyPlanetDebug(settings with { Seed = 999 }, true);
        await Until(() => client.PlanetDebugState?.CanCancel == true || client.PlanetDebugState?.Busy == false, "cancellable preparation");
        client.CancelPlanetDebug();
        await Until(() => client.PlanetDebugState?.Busy == false, "cancel acknowledged");
        Verify.Check(client.World.Definition.Seed != 999, "cancel leaves active planet intact");
        var cleanup = Stopwatch.StartNew();
        await Until(() => cleanup.Elapsed.TotalSeconds > 1, "cancelled resources released");
        Verify.Check(GenerationService.ActiveContexts <= server.LoadedLocationCount + 1, "repeated regeneration does not retain old generation contexts");
        int retainedSeed = client.World.Definition.Seed;
        client.ApplyPlanetDebug(settings with { Seed = 997 }, true);
        await Until(() => client.PlanetDebugState?.CanCancel == true, "disconnect preparation started");
        client.Dispose();
        using var resumed = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "Generator tester", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash, identity: identity));
        Verify.Check(resumed.World.Definition.Seed == retainedSeed && resumed.World.Definition.LocationId == home.LocationId, "disconnect before commit preserves planet and reconnect identity");
    }
}

