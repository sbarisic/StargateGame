using System.Diagnostics;
using System.Text;

namespace StargateGame.Client;

static class ClientLog
{
    static readonly object sync = new();
    static StreamWriter? file;
    [ThreadStatic] static bool writing;

    public static void Initialize()
    {
        try
        {
            // Keep this location stable when Local Testing switches user profiles.
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame", "logs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"client-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            file = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(new TeeWriter(Console.Out, "OUT"));
            Console.SetError(new TeeWriter(Console.Error, "ERR"));
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("FATAL", $"Terminating={e.IsTerminating}: {e.ExceptionObject}\n");
            // VS can stop on a thrown exception before Program's catch runs.
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                if (Debugger.IsAttached && !writing)
                    Write("THROWN", e.Exception + "\n");
            };
            TaskScheduler.UnobservedTaskException += (_, e) => Write("TASK", e.Exception + "\n");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Write("EXIT", $"ExitCode={Environment.ExitCode}\n");
            Console.WriteLine($"Client log: {path}");
            Console.WriteLine($"UTC: {DateTime.UtcNow:O}; PID: {Environment.ProcessId}; debugger: {Debugger.IsAttached}");
            Console.WriteLine($"Build: {AppContext.BaseDirectory}; working directory: {Environment.CurrentDirectory}");
            Console.WriteLine($"Arguments: {string.Join(" ", Environment.GetCommandLineArgs().Skip(1))}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Unable to open client log: " + ex.Message);
        }
    }

    static void Write(string channel, string? value)
    {
        if (writing || value is null) return;
        lock (sync)
        {
            writing = true;
            try { file?.Write($"[{DateTime.UtcNow:O}] [{channel}] {value}"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging failure must not turn a recoverable game error into a crash.
                file = null;
            }
            finally { writing = false; }
        }
    }

    sealed class TeeWriter(TextWriter original, string channel) : TextWriter
    {
        public override Encoding Encoding => original.Encoding;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value)
        {
            ClientLog.Write(channel, value);
            original.Write(value);
        }
        public override void WriteLine(string? value)
        {
            ClientLog.Write(channel, value + Environment.NewLine);
            original.WriteLine(value);
        }
        public override void Flush() => original.Flush();
    }
}
