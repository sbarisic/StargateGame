namespace StargateGame.Client;

// Source key numbers are stable identities, not mesh indices or dial sequence positions.
// Key 13 shares key 01's physical socket and is absent from the standard DHD layout.
sealed class DhdGlyphState
{
    readonly Dictionary<string, ulong> lit = [];
    readonly Dictionary<string, bool> orbs = [];
    public bool OrbEnabledByDefault
    {
        get;
        set;
    }
    public bool IsOrbLit(string dhdId) => orbs.GetValueOrDefault(dhdId, OrbEnabledByDefault);
    public void SetOrb(string dhdId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dhdId);
        orbs[dhdId] = enabled;
    }
    public bool AnyEmission(string dhdId) => AnyLit(dhdId) || IsOrbLit(dhdId);
    public static bool IsAvailable(int key) => key is >= 1 and <= 39 && key != 13;
    static void Validate(int key)
    {
        if (!IsAvailable(key))
            throw new ArgumentOutOfRangeException(nameof(key), "DHD keys are 1–39, excluding the overlapping key 13.");
    }
    public bool IsLit(string dhdId, int key) => IsAvailable(key) && (lit.GetValueOrDefault(dhdId) & (1UL << key)) != 0;
    public bool AnyLit(string dhdId) => lit.GetValueOrDefault(dhdId) != 0;
    public void SetGlyph(string dhdId, int key, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dhdId);
        Validate(key);
        ulong value = lit.GetValueOrDefault(dhdId), bit = 1UL << key;
        value = enabled ? value | bit : value & ~bit;
        if (value == 0)
            lit.Remove(dhdId);
        else
            lit[dhdId] = value;
    }
    public void SetGlyphs(string dhdId, IEnumerable<int> keys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dhdId);
        ulong value = 0;
        foreach (int key in keys)
        {
            Validate(key);
            value |= 1UL << key;
        }
        if (value == 0)
            lit.Remove(dhdId);
        else
            lit[dhdId] = value;
    }
    public void Clear(string dhdId)
    {
        lit.Remove(dhdId);
        orbs.Remove(dhdId);
    }
    public void Clear()
    {
        lit.Clear();
        orbs.Clear();
    }
    public static int[] ParsePreview(string text)
    {
        if (text.Equals("off", StringComparison.OrdinalIgnoreCase))
            return [];
        var keys = text.Split(',').Select(s => int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).Distinct().ToArray();
        foreach (int key in keys)
            Validate(key);
        return keys;
    }
}
