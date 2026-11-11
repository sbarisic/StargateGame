namespace StargateGame.Client;

internal readonly record struct CanopyMipSelection(int First, long PayloadBytes)
{
    public static long Bytes(int[] size, int levels, int first) => Enumerable.Range(first, levels - first)
        .Sum(mip => 8L * Math.Max(1, size[0] >> mip) * Math.Max(1, size[1] >> mip) * Math.Max(1, size[2] >> mip));

    public static CanopyMipSelection Select(int[] size, int levels, int maximum, long available, long ambient)
    {
        if (size.Length != 3 || size.Any(v => v is < 1 or > 256) || levels is < 1 or > 9)
            throw new InvalidDataException("Invalid canopy dimensions.");
        int first = 0;
        while (first < levels - 1 && size.Max() >> first > maximum) first++;
        // Essential crowns retain a small complete chain even while ordinary
        // model textures are being reduced to recover the residency budget.
        while (first < levels - 1 && size.Max() >> first > 32 && Bytes(size, levels, first) + ambient > available) first++;
        return new(first, Bytes(size, levels, first));
    }
}
