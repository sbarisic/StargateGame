namespace StargateGame.Client;

readonly record struct DhdHintFrame(IReadOnlyList<int>? Symbols, double Position)
{
    public float ForKey(int key)
    {
        if (Symbols is null || !DhdGlyphState.IsAvailable(key))
            return 0;
        for (int i = 0; i < Symbols.Count; i++)
            if (Symbols[i] == key)
            {
                double distance = Math.Abs(i - Position);
                return distance < 1.5 ? (float)Math.Pow(Math.Cos(distance * Math.PI / 3), 2) : 0;
            }
        return 0;
    }
    public int Key
    {
        get
        {
            int key = -1;
            float peak = .001f;
            if (Symbols is not null)
                foreach (int candidate in Symbols)
                {
                    float value = ForKey(candidate);
                    if (value > peak)
                    {
                        key = candidate;
                        peak = value;
                    }
                }
            return key;
        }
    }
    public float Strength => ForKey(Key);
}

// Local guidance only: never modifies accepted glyphs or sends dialing commands.
sealed class DhdAddressHint
{
    public const double KeySeconds = .75, PauseSeconds = 3;
    int[] symbols = [];
    Guid location;
    double started;
    public DhdHintFrame Sample(Guid world, IReadOnlyList<int> address, double now)
    {
        if (world != location || !symbols.SequenceEqual(address) || now < started)
        {
            location = world;
            symbols = address.ToArray();
            started = now;
        }
        if (symbols.Length != 6 || !double.IsFinite(now))
            return default;
        double travel = (symbols.Length + 2) * KeySeconds;
        double phase = (now - started) % (travel + PauseSeconds);
        // A three-symbol cosine envelope enters and leaves smoothly; it does not
        // wrap across the ends during the quiet pause.
        return phase < travel ? new(symbols, phase / KeySeconds - 1.5) : default;
    }
}
