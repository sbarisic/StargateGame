using StargateGame.Client;

try
{
    StartupTrace.Initialize(args);
    if (args.Contains("--verify-assets"))
    {
        string directory = StargateGame.Shared.Arguments.Get(args, "--verify-assets", Path.Combine(AppContext.BaseDirectory, "assets"));
        using var assets = new CookedAssets(directory);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int count = assets.VerifyAll();
        Console.WriteLine($"Verified {count} asset blocks in {watch.Elapsed.TotalSeconds:F2}s; read={assets.ReadBytes / 1048576d:F1} MiB decode={assets.DecodeMilliseconds:F1}ms");
        return;
    }
    if (args.Contains("--identity-fingerprint"))
    {
        using var identity = DeviceIdentity.Load(StargateGame.Shared.Arguments.Get(args, "--identity-profile", ""));
        Console.WriteLine(identity.Fingerprint);
        return;
    }
    int cueIndex = Array.IndexOf(args, "--audio-cue");
    if (cueIndex >= 0)
    {
        if (cueIndex + 1 >= args.Length)
            throw new ArgumentException("--audio-cue requires a cue ID.");
        using var audio = new WorldAudioController() { Trace = true };
        int record = Array.IndexOf(args, "--audio-record");
        if (record >= 0 && record + 1 < args.Length)
            audio.Record(args[record + 1]);
        audio.Preview(args[cueIndex + 1]);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 10)
        {
            audio.Idle(clock.Elapsed.TotalSeconds);
            Thread.Sleep(10);
        }
    }
    else
        new GameApp(args).Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}
