namespace StargateGame.Shared;

public static class Arguments
{
    public static string Get(string[] args, string key, string fallback)
    {
        int i = Array.IndexOf(args, key);
        if (i < 0)
            return fallback;
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--"))
            throw new ArgumentException($"Missing value for {key}.");
        return args[i + 1];
    }
    public static int Number(string[] args, string key, int fallback, int min, int max)
    {
        if (!int.TryParse(Get(args, key, fallback.ToString()), out int value) || value < min || value > max)
            throw new ArgumentException($"{key} must be between {min} and {max}.");
        return value;
    }
}
