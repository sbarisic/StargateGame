using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Text;

namespace StargateGame.Testing;

public static class SessionLauncher
{
    public const string RuntimePathsFile = "testing-runtime.paths";
    public static string Sessions => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame", "Testing", "sessions");
    public static string NewRoot(string id) => Path.Combine(Sessions, id);
    public static string? DevelopmentAssets(string root)
    {
        string manifest = Path.Combine(root, RuntimePathsFile);
        if (!File.Exists(manifest)) return null;
        const string prefix = "development-assets=";
        var paths = File.ReadAllLines(manifest).Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]).ToArray();
        if (paths.Length == 0) return null;
        if (paths.Length != 1 || !Path.IsPathFullyQualified(paths[0]) || !File.Exists(Path.Combine(paths[0], "manifest.json")))
            throw new FileNotFoundException("Development asset overlay missing or invalid. Rebuild the client with asset cooking enabled.");
        return paths[0];
    }
    public static string Binary(string root, string project)
    {
        string manifest = Path.Combine(root, RuntimePathsFile);
        if (File.Exists(manifest))
        {
            var paths = File.ReadAllLines(manifest).Select(p => p.Trim()).Where(p =>
                Path.GetFileName(p).Equals(project + ".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (paths.Length != 1 || !Path.IsPathFullyQualified(paths[0]) || !File.Exists(paths[0]))
                throw new FileNotFoundException("Missing or invalid " + project + " in " + manifest + ". Rebuild the client and its project dependencies in the selected configuration.");
            return paths[0];
        }
        foreach (string p in new[] { Path.Combine(root, "bin", project, "release", project + ".dll"), Path.Combine(root, project, project + ".dll"), Path.Combine(root, project + ".dll"), Path.Combine(root, "testing", project, project + ".dll") })
            if (File.Exists(p)) return p;
        throw new FileNotFoundException("Missing " + project + " in " + root + ". Build the test runtime first.");
    }
    public static string FindBuildRoot(string from)
    {
        string clientDirectory = Path.GetFullPath(from).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var directory = new DirectoryInfo(from); directory is not null; directory = directory.Parent)
        {
            // A build-generated manifest is authoritative; never fall back to an
            // older Release build when a Debug dependency is missing or stale.
            if (File.Exists(Path.Combine(directory.FullName, RuntimePathsFile)))
            {
                foreach (string project in new[] { "StargateGame.Client", "StargateGame.Server", "StargateGame.Harness" }) Binary(directory.FullName, project);
                if (!Path.GetDirectoryName(Binary(directory.FullName, "StargateGame.Client"))!.Equals(clientDirectory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Local testing manifest belongs to another client build. Rebuild this client.");
                return directory.FullName;
            }
            foreach (string root in new[] { directory.FullName, Path.Combine(directory.FullName, "testing") })
                try
                {
                    Binary(root, "StargateGame.Harness"); Binary(root, "StargateGame.Server");
                    if (Path.GetDirectoryName(Binary(root, "StargateGame.Client"))!.Equals(clientDirectory, StringComparison.OrdinalIgnoreCase)) return root;
                }
                catch (FileNotFoundException) { }
        }
        throw new FileNotFoundException("Matching local test runtime missing. Rebuild the client with its Server and Harness project dependencies, or publish the client with testing support.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public int X, Y, Width, Height, XChars, YChars, Fill, Flags; public short Show, ReservedCount; public nint ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? application, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string? directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    public static Process Launch(LaunchOptions options)
    {
        var paths = new[] { "StargateGame.Client", "StargateGame.Server", "StargateGame.Harness" }.Select(p => Path.GetDirectoryName(Binary(options.BuildRoot, p))!).ToArray();
        foreach (string name in new[] { "StargateGame.Testing.dll", "StargateGame.Shared.dll" })
        {
            var hashes = paths.Select(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(p, name))))).Distinct().ToArray();
            if (hashes.Length != 1) throw new InvalidDataException("Incompatible local testing binaries: " + name + ". Rebuild the selected build root.");
        }
        Directory.CreateDirectory(options.Root);
        string path = Path.Combine(options.Root, "launch.json"); TestJson.Write(path, options);
        if (OperatingSystem.IsWindows())
        {
            // Do not inherit the invoking PowerShell pipeline's handles. Otherwise `start | ConvertFrom-Json`
            // waits for the persistent coordinator to exit even after the CLI has returned its session ID.
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder("dotnet \"" + Binary(options.BuildRoot, "StargateGame.Harness") + "\" serve \"" + path + "\"");
            if (!CreateProcess(null, command, 0, 0, false, 0x08000000, 0, null, ref startup, out var child)) throw new Win32Exception();
            try { return Process.GetProcessById(child.ProcessId); }
            finally { CloseHandle(child.Process); CloseHandle(child.Thread); }
        }
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Binary(options.BuildRoot, "StargateGame.Harness")); start.ArgumentList.Add("serve"); start.ArgumentList.Add(path);
        return Process.Start(start) ?? throw new InvalidOperationException("Coordinator failed to launch.");
    }
}
