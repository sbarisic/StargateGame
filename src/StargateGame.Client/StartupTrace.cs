using System.Diagnostics;
using System.Text.Json;

namespace StargateGame.Client;

static class StartupTrace
{
    static readonly long start = Stopwatch.GetTimestamp();
    static readonly double processOffset = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
    static readonly object sync = new();
    static string? output;
    public static double Milliseconds => processOffset + Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    public static void Initialize(string[] args)
    {
        int index = Array.IndexOf(args, "--startup-log");
        if (index >= 0 && index + 1 < args.Length)
        {
            output = Path.GetFullPath(args[index + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        }
        Mark("process-entry");
    }
    public static void Mark(string stage, double durationMs = 0, string? detail = null)
    {
        lock (sync)
        {
            double elapsed = Milliseconds;
            Console.WriteLine($"STARTUP {stage} elapsed={elapsed:F1}ms duration={durationMs:F2}ms {detail}");
            if (output is not null)
                try
                {
                    File.AppendAllText(output, JsonSerializer.Serialize(new
                    {
                        stage,
                        elapsedMs = elapsed,
                        durationMs,
                        detail
                    }) + Environment.NewLine);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    output = null;
                }
        }
    }
}
