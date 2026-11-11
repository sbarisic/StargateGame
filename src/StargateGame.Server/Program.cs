using System.Net;
using StargateGame.Server;
using StargateGame.Shared;

try
{
    if (args.Contains("--help"))
    {
        Console.WriteLine("StargateGame.Server [--bind 0.0.0.0] [--port 7777] [--max-players 8] [--seed 7319] [--preset temperate|desert|icy] [--dev-planets] [--allow-test-identities] [--save-dir PATH] [--universe default] [--catalogue PATH] [--build-catalogue] [--settlements auto|wilderness|ruins|village|city] [--settlement-style auto|mega|seahill|desert|modern]");
        return;
    }
    var bind = IPAddress.Parse(Arguments.Get(args, "--bind", "0.0.0.0"));
    int port = Arguments.Number(args, "--port", 7777, args.Contains("--test-pipe") ? 0 : 1, 65535);
    int max = Arguments.Number(args, "--max-players", 8, 1, 8);
    using var quit = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        quit.Cancel();
    };
    int seed = Arguments.Number(args, "--seed", 7319, int.MinValue, int.MaxValue);
    string cataloguePath = Arguments.Get(args, "--catalogue", Path.Combine(AppContext.BaseDirectory, "galaxy", "galaxy.bin"));
    if (args.Contains("--build-catalogue"))
    {
        StargateGame.Galaxy.Catalogue.Build(cataloguePath, Console.WriteLine, quit.Token);
        return;
    }
    string fixture = Arguments.Get(args, "--fixture", "");
    if (fixture.Length == 0 && !File.Exists(cataloguePath))
    {
        Console.WriteLine("Building the complete galaxy catalogue for this installation (one-time operation)...");
        StargateGame.Galaxy.Catalogue.Build(cataloguePath, Console.WriteLine, quit.Token);
    }
    string preset = Arguments.Get(args, "--preset", "temperate");
    var startup = System.Diagnostics.Stopwatch.StartNew();
    await using var server = new GameServer(bind, port, max, seed, preset, args.Contains("--dev-planets") || fixture.Length > 0, Arguments.Get(args, "--save-dir", StargateGame.Persistence.WorldRepository.DefaultDirectory), Arguments.Get(args, "--universe", "default"), fixture.Length > 0 ? null : cataloguePath, fixture.Length > 0 ? fixture : null, new SettlementSettings(Arguments.Get(args, "--settlements", "auto"), Arguments.Get(args, "--settlement-style", "auto")), args.Contains("--allow-test-identities"), new PlanetGenerationSettings(LandscapeMode:Enum.Parse<LandscapeMode>(Arguments.Get(args,"--landscape","Auto"),true)), testSeed: args.Contains("--test-pipe") ? seed : null);
    Console.WriteLine($"Planet startup: {startup.ElapsedMilliseconds} ms, {server.CollisionCount} terrain colliders.");
    server.Start();
    await using var testControl = server.StartTestControl(args);
    _ = Task.Run(async () =>
    {
        while (!quit.IsCancellationRequested)
        {
            string? line = Console.ReadLine();
            if (line is null)
                return;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try
            {
                if (parts.Length == 3 && parts[0] == "rebind-player" && Guid.TryParse(parts[1], out var id))
                {
                    await server.RebindPlayerAsync(id, parts[2]);
                    Console.WriteLine("Player key replaced; previous key revoked.");
                }
                else
                    Console.WriteLine("Admin: rebind-player PLAYER_GUID NEW_PUBLIC_KEY_SHA256");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Administration failed: " + ex.Message);
            }
        }
    });
    try
    {
        await await Task.WhenAny(server.Completion, Task.Delay(Timeout.Infinite, quit.Token));
    }
    catch (OperationCanceledException)
    {
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(args.Contains("--test-pipe") ? ex.ToString() : ex.Message);
    Environment.ExitCode = 1;
}
