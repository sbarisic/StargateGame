using System.IO.Compression;
using System.Text.Json;

namespace StargateGame.Client;

// CPU-only decoding. No graphics objects or mutable world state cross threads.
sealed record TerrainPaletteData(string Preset, int Size, float[] Metres, float[] Gain, float[] Normal, float[] Height, float[] Relief, TerrainPaletteData.Level[] Levels)
{
    public float[] GrassCoverage { get; init; } = new float[8];
    public sealed record Level(int Layer, int Map, int Mip, int Side, byte[] Bytes, string? AssetKey = null);
    public static TerrainPaletteData Load(string root, string preset, int size, CancellationToken token, CookedAssets? assets = null, string? meadowMaterial = null)
    {
        using var json = assets?.Store?.HasCatalog == true
            ? JsonDocument.Parse(assets.Read("terrain/pbr/manifest.json"))
            : JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var entries = json.RootElement.GetProperty("palettes").GetProperty(preset).EnumerateArray().ToArray();
        float[] metres = new float[8], gain = new float[8], normal = new float[8], height = new float[8], relief = new float[8], grass = new float[8];
        List<Level> levels = [];
        int skip = 11 - (int)MathF.Log2(size);
        byte[] scratch = new byte[65536];
        for (int layer = 0; layer < 8; layer++)
        {
            token.ThrowIfCancellationRequested();
            metres[layer] = entries[layer].GetProperty("metres").GetSingle();
            var material = json.RootElement.GetProperty("materials").GetProperty(meadowMaterial is not null && entries[layer].GetProperty("role").GetString() == "meadow" ? meadowMaterial : entries[layer].GetProperty("material").GetString()!);
            grass[layer] = material.TryGetProperty("grassCoverage", out var grassValue) ? grassValue.GetSingle() : 0;
            if (!float.IsFinite(grass[layer]) || grass[layer] < 0 || grass[layer] > 1)
                throw new InvalidDataException("Terrain grass coverage must be between zero and one.");
            gain[layer] = material.TryGetProperty("albedoGain", out var g) ? g.GetSingle() : 1;
            normal[layer] = material.GetProperty("normalStrength").GetSingle();
            height[layer] = material.GetProperty("heightBlendStrength").GetSingle();
            relief[layer] = material.TryGetProperty("parallaxDepthMetres", out var depth) ? depth.GetSingle() : 0;
            if (!float.IsFinite(relief[layer]) || relief[layer] < 0 || relief[layer] > .25f)
                throw new InvalidDataException("Terrain parallax depth must be between 0 and 0.25 metres.");
            if (assets?.Store?.HasCatalog == true && material.TryGetProperty("cookedDescriptor", out var descriptor))
            {
                using var cooked = JsonDocument.Parse(assets.Read(descriptor.GetString()!));
                var mips = cooked.RootElement.GetProperty("levels");
                for (int mip = skip; mip < mips.GetArrayLength(); mip++)
                    for (int map = 0; map < 3; map++)
                        levels.Add(new(layer, map, mip - skip, 2048 >> mip, [], mips[mip][map].GetString()));
                continue;
            }
            using var stream = new GZipStream(File.OpenRead(Path.Combine(root, material.GetProperty("file").GetString()!)), CompressionMode.Decompress);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != 0x31425054 || reader.ReadInt32() != 2048 || reader.ReadInt32() != 12)
                throw new InvalidDataException("Terrain material format.");
            for (int mip = 0; mip < 12; mip++)
                for (int map = 0; map < 3; map++)
                {
                    token.ThrowIfCancellationRequested();
                    int side = 2048 >> mip, length = side * side * 4;
                    if (mip < skip)
                    {
                        // Skip unneeded high-resolution levels without allocating
                        // hundreds of megabytes of short-lived arrays.
                        while (length > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            int n = Math.Min(length, scratch.Length);
                            stream.ReadExactly(scratch.AsSpan(0, n));
                            length -= n;
                        }
                    }
                    else
                    {
                        byte[] bytes = new byte[length];
                        stream.ReadExactly(bytes);
                        levels.Add(new(layer, map, mip - skip, side, bytes));
                    }
                }
        }
        return new(preset, size, metres, gain, normal, height, relief, levels.ToArray()) { GrassCoverage = grass };
    }
}
