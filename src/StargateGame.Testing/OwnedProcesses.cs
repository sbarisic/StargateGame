using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StargateGame.Testing;

public sealed class OwnedProcesses : IDisposable
{
    readonly nint job;
    bool disposed;
    readonly List<Process> processes = [];
    readonly List<Task> logs = [];
    [StructLayout(LayoutKind.Sequential)] struct Basic { public long A, B; public uint Flags; public nuint Min, Max; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct Io { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)] struct Limits { public Basic Basic; public Io Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int kind, ref Limits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    public OwnedProcesses()
    {
        if (!OperatingSystem.IsWindows()) return;
        job = CreateJobObject(0, null);
        var info = new Limits { Basic = new Basic { Flags = 0x2000 } };
        if (job == 0 || !SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<Limits>())) throw new Win32Exception();
    }
    public Process Start(string executable, IEnumerable<string> arguments, string log, IDictionary<string, string>? environment = null, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        if (workingDirectory is not null) info.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(log))!);
        var process = Process.Start(info) ?? throw new InvalidOperationException("Process failed to start.");
        if (job != 0 && !AssignProcessToJobObject(job, process.Handle)) { process.Kill(true); process.Dispose(); throw new Win32Exception(); }
        process.StandardInput.Close();
        processes.Add(process);
        logs.Add(Copy(process.StandardOutput, log));
        logs.Add(Copy(process.StandardError, log + ".error"));
        return process;
    }
    static async Task Copy(StreamReader reader, string path)
    {
        try { await using var writer = new StreamWriter(path) { AutoFlush = true }; while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        foreach (var p in processes) { try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch (InvalidOperationException) { } }
        if (job != 0) CloseHandle(job);
        try { Task.WhenAll(logs).Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        foreach (var p in processes) p.Dispose();
    }
}
