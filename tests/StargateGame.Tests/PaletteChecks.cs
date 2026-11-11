using System.IO.Compression;
using System.Text.Json;
using StargateGame.Client;

static class PaletteChecks
{
    public static void Mounted()
    {
        using var assets = new CookedAssets(Path.GetFullPath("artifacts/cooked"), development: Path.GetFullPath("artifacts/cooked/dev-override"));
        foreach (string preset in new[] { "temperate", "desert", "icy" })
        {
            var loose = TerrainPaletteData.Load(Path.GetFullPath("assets/terrain/pbr"), preset, 512, CancellationToken.None);
            var packed = TerrainPaletteData.Load("unused", preset, 512, CancellationToken.None, assets);
            Verify.Check(loose.Metres.SequenceEqual(packed.Metres) && loose.Gain.SequenceEqual(packed.Gain) && loose.Relief.SequenceEqual(packed.Relief)
                && loose.Normal.SequenceEqual(packed.Normal) && loose.Height.SequenceEqual(packed.Height), "packed and loose PBR settings agree: " + preset);
            Verify.Check(packed.Levels.All(l => l.AssetKey is not null && assets.Read(l.AssetKey).Length > 0), "every packed mip is readable: " + preset);
            if (preset == "temperate")
                Verify.Check(!packed.Levels.Where(l => l.Layer == 0).Select(l => l.AssetKey).SequenceEqual(packed.Levels.Where(l => l.Layer == 1).Select(l => l.AssetKey)), "meadow and beach retain distinct material payloads");
        }
        Console.WriteLine($"MOUNTED PALETTE {Verify.Count} CHECKS PASSED");
    }
    public static void Run()
    {
        string root = Path.GetFullPath("assets/terrain/pbr");
        foreach (string preset in new[] { "temperate", "desert", "icy" })
        {
            var data = TerrainPaletteData.Load(root, preset, 512, CancellationToken.None);
            Verify.Check(data.Relief.Length == 8 && data.Relief.All(d => float.IsFinite(d) && d >= 0 && d <= .25f), $"bounded terrain relief: {preset}");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
            if (preset == "temperate")
            {
                var grass = manifest.RootElement.GetProperty("palettes").GetProperty(preset).EnumerateArray().Take(2).ToArray();
                Verify.Check(grass[0].GetProperty("material").GetString() == "rocky-rugged-terrain-pbr" && grass[1].GetProperty("material").GetString() == "ground_sand", "meadow uses displaced rocky substrate; beach retains sand");
                Verify.Check(Math.Abs(data.Relief[0] - .16f) < .001f && data.Relief[1] <= .015f, "rock relief retained under grass without raising beach relief");
            }
            int layer = 0;
            // Compare against the previous full-resolution sequential reader.
            // Includes alpha/packed masks and every supplied mip, byte for byte.
            foreach (var entry in manifest.RootElement.GetProperty("palettes").GetProperty(preset).EnumerateArray())
            {
                string material = entry.GetProperty("material").GetString()!;
                string file = manifest.RootElement.GetProperty("materials").GetProperty(material).GetProperty("file").GetString()!;
                using var reader = new BinaryReader(new GZipStream(File.OpenRead(Path.Combine(root, file)), CompressionMode.Decompress));
                reader.ReadBytes(12);
                bool identical = true;
                for (int mip = 0; mip < 12; mip++)
                    for (int map = 0; map < 3; map++)
                    {
                        int side = 2048 >> mip;
                        var bytes = reader.ReadBytes(side * side * 4);
                        if (mip >= 2)
                            identical &= data.Levels.Single(l => l.Layer == layer && l.Map == map && l.Mip == mip - 2).Bytes.SequenceEqual(bytes);
                    }
                Verify.Check(identical, $"background palette preserves every channel and mip: {preset}/{material}");
                layer++;
            }
            Verify.Check(data.Levels.Sum(l => (long)l.Bytes.Length) < 33L * 1024 * 1024, "512 palette retains only requested resolution");
        }
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        bool cancelled = false;
        try
        {
            TerrainPaletteData.Load(root, "desert", 1024, stop.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        Verify.Check(cancelled, "superseded palette decode can be cancelled");
        Console.WriteLine($"PALETTE {Verify.Count} CHECKS PASSED");
    }
}
