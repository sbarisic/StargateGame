using System.IO.Compression;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    // Sidecars are made by prepare_leaf_mips.py, using the release cooker's exact
    // colour dilation and alpha-coverage filter. Driver-generated mips lose leaves.
    IEnumerable<bool> UploadLeafMips(string file, string cache, TextureWrap wrap, float cutoff)
    {
        string key = $"models/{file}.alpha-{(int)MathF.Round(cutoff * 1000)}.mips.gz";
        byte[] compressed;
        while (!modelAssets.TryTake(key, out compressed))
            yield return false;
        var work = modelAssets.PrepareCpuAsync(() => ReadLeafMips(compressed), startupStop.Token);
        while (!work.IsCompleted)
            yield return false;
        var (width, height, mips) = work.GetAwaiter().GetResult();
        float anisotropy = graphics.Capabilities.SupportsAnisotropy ? Math.Min(8, graphics.Capabilities.MaximumAnisotropy) : 1;
        var texture = Own(graphics.CreateTexture(new TextureDescriptor(width, height, mipLevels: mips.Length,
            sampling: new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, wrap, wrap, anisotropy: anisotropy))));
        bool complete = false;
        try
        {
            for (int level = 0; level < mips.Length; level++)
                foreach (bool step in UploadTextureRows(texture, mips[level], Math.Max(1, width >> level), Math.Max(1, height >> level), level))
                    yield return step;
            textures.Add(cache, texture);
            modelTextureBytes += mips.Sum(mip => (long)mip.Length);
            complete = true;
        }
        finally
        {
            if (!complete)
            {
                resources.Remove(texture);
                texture.Dispose();
            }
        }
    }

    internal static (int Width, int Height, byte[][] Mips) ReadLeafMips(byte[] compressed)
    {
        using var source = new MemoryStream(compressed);
        using var stream = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt32() != 0x4d414753)
            throw new InvalidDataException("Invalid leaf mip header; run tools/prepare_leaf_mips.py.");
        int width = reader.ReadInt32(), height = reader.ReadInt32(), count = reader.ReadInt32();
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || count != 1 + (int)MathF.Log2(Math.Max(width, height)))
            throw new InvalidDataException("Invalid leaf mip dimensions.");
        stream.ReadExactly(new byte[32]); // Offline source hash, retained for incremental regeneration.
        byte[][] mips = new byte[count][];
        long total = 0;
        for (int level = 0; level < count; level++)
        {
            int length = Math.Max(1, width >> level) * Math.Max(1, height >> level) * 4;
            total += length;
            if (total > 96L * 1024 * 1024)
                throw new InvalidDataException("Leaf mip data exceeds its size limit.");
            mips[level] = new byte[length];
            stream.ReadExactly(mips[level]);
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("Unexpected leaf mip trailing data.");
        return (width, height, mips);
    }
}
