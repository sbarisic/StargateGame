namespace StargateGame.Testing;

public static class SessionArtifacts
{
    public static string[] Disposable(string root) => Directory.GetDirectories(root)
        .Where(p => Path.GetFileName(p).StartsWith("world-", StringComparison.Ordinal))
        .Concat(Directory.GetDirectories(root, "client-*").SelectMany(p => new[] { Path.Combine(p, "profile"), Path.Combine(p, "transient") }).Where(Directory.Exists)).ToArray();
    public static object Compact(LaunchOptions launch, bool preview = false)
    {
        string expected = Path.GetFullPath(SessionLauncher.NewRoot(launch.Session));
        string root = Path.GetFullPath(launch.Root);
        if (!root.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Not an owned session artifact root.");
        var manifest = TestJson.Read<SessionManifest>(Path.Combine(root, "session.json"));
        if (launch.RetainState || manifest.State is not ("stopped" or "failed")) return new { retained = true, reason = "Retained or active session" };
        foreach (int pid in manifest.ClientPids.Where(pid => launch.AttachedClientPipe is null || pid != launch.OwnerPid).Append(manifest.ServerPid))
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); if (!p.HasExited) return new { retained = true, reason = "A recorded process is still running; retry cleanup after exit." }; }
            catch (ArgumentException) { }
        }
        long bytes = 0; var removed = new List<string>();
        foreach (string path in Disposable(root))
        {
            var pending = new Stack<string>(); pending.Push(path); var files = new List<FileInfo>(); var directories = new List<string>();
            while (pending.TryPop(out var directory))
            {
                for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
                    if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup refuses linked paths: " + parent.FullName);
                if (!Path.GetFullPath(directory).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Cleanup escaped session.");
                directories.Add(directory);
                foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Cleanup refuses linked entries.");
                    if (item is DirectoryInfo child) pending.Push(child.FullName); else files.Add((FileInfo)item);
                }
            }
            foreach (var file in files)
            {
                // Keep server logs alongside the compact session record, not inside disposable worlds.
                if (!preview && file.Name.StartsWith("server.log", StringComparison.Ordinal)) file.CopyTo(Path.Combine(root, Path.GetFileName(path) + "-" + file.Name), true);
                bytes += file.Length;
                if (!preview) file.Delete();
            }
            if (!preview) foreach (string directory in directories.AsEnumerable().Reverse()) Directory.Delete(directory);
            removed.Add(path);
        }
        var result = new { preview, bytes, paths = removed };
        if (!preview) TestJson.Write(Path.Combine(root, "retention.json"), result);
        return result;
    }
}
