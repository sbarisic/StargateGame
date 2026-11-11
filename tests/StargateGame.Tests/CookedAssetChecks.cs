using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StargateGame.Client;

static class CookedAssetChecks
{
    public static async Task Run()
    {
        var canopy = CanopyMipSelection.Select([256, 256, 256], 9, 128, 512L << 20, 600000);
        Verify.Check(canopy.First == 1 && canopy.PayloadBytes < 19L << 20,
            "world canopy mip chains fit all ten species without full-resolution allocations");
        var pressured = CanopyMipSelection.Select([256, 256, 256], 9, 128, 0, 600000);
        Verify.Check(pressured.First == 3 && pressured.PayloadBytes < 1L << 20,
            "memory pressure retains custom canopy at reduced detail instead of old leaf meshes");
        Verify.Check(CanopyMipSelection.Select([256, 256, 256], 9, 256, long.MaxValue, 600000).First == 0,
            "explicit inspection retains full-resolution canopy");
        await MountedPacks();
        string directory = Path.Combine(Path.GetTempPath(), "stargate-cooked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            byte[] original = Enumerable.Range(0, 8192).Select(i => (byte)(i % 83)).ToArray();
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var zip = new BrotliStream(ms, CompressionLevel.Fastest, true))
                    zip.Write(original);
                compressed = ms.ToArray();
            }
            var entries = new Dictionary<string, CookedAssets.Entry> { { "models/test.mesh", new(48, compressed.Length, original.Length, Convert.ToHexString(SHA256.HashData(original)), true) } };
            byte[] index = JsonSerializer.SerializeToUtf8Bytes(entries);
            string file = Path.Combine(directory, "models.sga");
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write(0x31414753u);
                writer.Write(48L + compressed.Length);
                writer.Write(index.Length);
                writer.Write(SHA256.HashData(index));
                writer.Write(compressed);
                writer.Write(index);
            }
            using (var assets = new CookedAssets(directory))
            {
                Verify.Check(assets.IsCooked && assets.Read("models/test.mesh").SequenceEqual(original), "cooked compressed block roundtrip");
                byte[] data = [];
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (!assets.TryTake("models/test.mesh", out data))
                {
                    if (timeout.Elapsed.TotalSeconds > 3)
                        throw new Exception("Asset worker stalled");
                    await Task.Delay(1);
                }
                Verify.Check(data.SequenceEqual(original) && assets.PreparedBytes == 0, "background block consumed and queue bytes released");
                CookedAssets.StagingLease lease;
                timeout.Restart();
                while (!assets.TryTakeLease("models/test.mesh", out lease))
                {
                    if (timeout.Elapsed.TotalSeconds > 3) throw new Exception("Leased asset worker stalled");
                    await Task.Delay(1);
                }
                assets.CancelPending();
                Verify.Check(assets.PreparedBytes == original.Length && lease.Bytes.SequenceEqual(original), "cancellation retains staging accounting for an active upload lease");
                lease.Dispose();
                lease.Dispose();
                Verify.Check(assets.PreparedBytes == 0, "upload lease releases staging exactly once");
                assets.Prefetch("models/test.mesh");
                assets.CancelPending();
                timeout.Restart();
                while (assets.PreparedBytes != 0 && timeout.Elapsed.TotalSeconds < 3) await Task.Delay(1);
                Verify.Check(assets.PreparedBytes == 0, "cancelled queued and in-flight reads release their reservations");
                Verify.Check(assets.Read("models/test.mesh").SequenceEqual(original), "consumed block can be requested again");
                bool rejected = false;
                try
                {
                    assets.Read("../save.db");
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }
                Verify.Check(rejected, "asset traversal rejected");
            }
            byte[] corrupt = File.ReadAllBytes(file);
            corrupt[48] ^= 127;
            File.WriteAllBytes(file, corrupt);
            bool invalid = false;
            try
            {
                using var assets = new CookedAssets(directory);
                assets.Read("models/test.mesh");
            }
            catch (InvalidDataException)
            {
                invalid = true;
            }
            Verify.Check(invalid, "corrupt block cannot silently fall back to loose files");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    static async Task MountedPacks()
    {
        string root = Path.Combine(Path.GetTempPath(), "stargate-mount-" + Guid.NewGuid().ToString("N"));
        string packs = Path.Combine(root, "packs"), overlay = Path.Combine(root, "override");
        Directory.CreateDirectory(packs);
        Directory.CreateDirectory(Path.Combine(overlay, "blobs"));
        string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
        string identity = new('a', 64), cooker = new('b', 64);
        byte[] original = [1, 2, 3], updated = [4, 5, 6];
        string key = "models/test.json", deleted = "models/deleted.json";
        var index = new Dictionary<string, CookedAssets.Entry> {
            [key] = new(48, 3, 3, Hash(original), false), [deleted] = new(48, 3, 3, Hash(original), false)
        };
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(index);
        using (var writer = new BinaryWriter(File.Create(Path.Combine(packs, "shared.sga"))))
        {
            writer.Write(0x31414753u); writer.Write(51L); writer.Write(encoded.Length);
            writer.Write(SHA256.HashData(encoded)); writer.Write(original); writer.Write(encoded);
        }
        byte[] patched = [10, 11, 12];
        byte[] patchIndex = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, CookedAssets.Entry> {
            [key] = new(48, 3, 3, Hash(patched), false)
        });
        using (var writer = new BinaryWriter(File.Create(Path.Combine(packs, "patch.sga"))))
        {
            writer.Write(0x31414753u); writer.Write(51L); writer.Write(patchIndex.Length);
            writer.Write(SHA256.HashData(patchIndex)); writer.Write(patched); writer.Write(patchIndex);
        }
        var assets = new Dictionary<string, object> {
            [key] = new { category = "shared", dependencies = new[] { key }, reload = "visual" },
            [deleted] = new { category = "shared", dependencies = new[] { deleted }, reload = "visual" }
        };
        File.WriteAllBytes(Path.Combine(packs, "catalog.json"), JsonSerializer.SerializeToUtf8Bytes(new {
            version = 1, cooker, id = identity,
            entries = index.ToDictionary(p => p.Key, p => new { hash = p.Value.Hash, size = p.Value.Size }),
            assets, packs = new[] { new { name = "shared", file = "shared.sga", indexHash = Hash(encoded) } },
            patches = new[] { new {
                name = "patch", file = "patch.sga", indexHash = Hash(patchIndex),
                assets = new Dictionary<string, object> { [key] = new { category = "weapons", dependencies = new[] { key }, reload = "visual" } }
            } }
        }));
        void Manifest(byte[] data, string generation, bool tombstone = false, string? baseId = null, bool sessionChange = false)
        {
            File.WriteAllBytes(Path.Combine(overlay, "blobs", Hash(data) + ".bin"), data);
            var changed = new Dictionary<string, object>();
            if (sessionChange) changed[key] = new { category = "weapons", dependencies = Array.Empty<string>(), reload = "session" };
            if (tombstone) changed[deleted] = new { category = "shared", dependencies = new[] { deleted }, reload = "visual", deleted = true };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new {
                version = 1, cooker, baseId = baseId ?? identity, generation,
                entries = new Dictionary<string, object> { [key] = new { hash = Hash(data), size = data.Length } },
                tombstones = tombstone ? new[] { deleted } : Array.Empty<string>(), assets = changed
            });
            File.WriteAllBytes(Path.Combine(overlay, "manifest.tmp"), bytes);
            File.Move(Path.Combine(overlay, "manifest.tmp"), Path.Combine(overlay, "manifest.json"), true);
        }
        try
        {
            using (var patchedStore = new CookedAssets(root))
                Verify.Check(patchedStore.Read(key).SequenceEqual(patched) && patchedStore.Store!.Current.Assets[key].Category == "weapons",
                    "declared release patch replaces payload and complete catalog descriptor");
            Manifest(updated, new('1', 64));
            using var store = new CookedAssets(root, development: overlay);
            Verify.Check(store.Read(key).SequenceEqual(updated), "development overlay overrides declared patches and base packs");
            var old = store.Store!.Current;
            store.Prefetch(key);
            byte[] newest = [7, 8, 9];
            Manifest(newest, new('2', 64), tombstone: true);
            var refresh = await Task.Run(store.Store.CheckRefresh);
            Verify.Check(refresh is not null && !refresh.RequiresReload && store.Read(key).SequenceEqual(updated), "refresh preparation leaves the current generation active");
            store.Publish(refresh!);
            Verify.Check(store.Read(key).SequenceEqual(newest) && !store.Exists(deleted), "published replacement and tombstone are atomic");
            Verify.Check(MountedAssetStore.Read(old.Blocks[key], out _, out _).SequenceEqual(updated), "in-flight snapshot retains its immutable payload");
            Verify.Check(store.Store.CheckRefresh() is null, "unchanged generation does not refresh");
            Manifest(newest, new('4', 64), tombstone: true, sessionChange: true);
            var metadataRefresh = store.Store.CheckRefresh();
            Verify.Check(metadataRefresh is { RequiresReload: true } && metadataRefresh.Changed.Contains(key),
                "descriptor-only gameplay change requires reload even when payload is unchanged");
            Manifest(newest, new('3', 64), baseId: new('0', 64));
            bool incompatible = false;
            try { store.Store.CheckRefresh(); } catch (InvalidDataException) { incompatible = true; }
            Verify.Check(incompatible && store.Read(key).SequenceEqual(newest), "incompatible override preserves active generation");
            bool conflict = false;
            try { using var invalid = new CookedAssets(root, loose: true, development: overlay); } catch (ArgumentException) { conflict = true; }
            Verify.Check(conflict, "loose and development providers cannot be combined");
            store.CancelPending();
        }
        finally { Directory.Delete(root, true); }
    }
}
