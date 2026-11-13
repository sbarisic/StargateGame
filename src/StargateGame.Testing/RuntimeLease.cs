namespace StargateGame.Testing;

/// <summary>Sessions share a read lease; preparation/publication require exclusive access.</summary>
public static class RuntimeLease
{
    public static string PathFor(string root) => Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".prepare.lock";
    public static FileStream Read(string root) => new(PathFor(root), FileMode.OpenOrCreate, FileAccess.Read, FileShare.Read);
}
