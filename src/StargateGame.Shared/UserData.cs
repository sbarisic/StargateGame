namespace StargateGame.Shared;

public static class UserData
{
    // Harness clients opt into a private profile before constructing any settings.
    public static string Root => Environment.GetEnvironmentVariable("STARGATEGAME_USER_DATA") is { Length: > 0 } path
        ? Path.GetFullPath(path) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StargateGame");
}
