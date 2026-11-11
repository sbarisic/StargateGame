using StargateGame.Shared;
using StargateGame.WorldGen;

static class TerrainDeliveryChecks
{
    public static async Task Run()
    {
        var plan = new PlanetPlan(42, "desert");
        var chunk = plan.BuildChunk(new(-1, -1));
        var bytes = ChunkCodec.Encode(chunk);
        var manifest = new Message { TransferId = 1, Planet = plan.Definition, CollisionChunks = [chunk.Coordinate], Manifest = [new(chunk.Coordinate, ChunkCodec.Hash(bytes), bytes.Length)] };
        TerrainTransfer New() => new(manifest);
        var transfer = New();
        for (int offset = 0; offset < bytes.Length; offset += 32768)
        {
            var packet = new ChunkPacket(chunk.Coordinate, ChunkCodec.Hash(bytes), offset, bytes.AsSpan(offset, Math.Min(32768, bytes.Length - offset)).ToArray());
            using var frame = new MemoryStream();
            await Protocol.WriteAsync(frame, new()
            {
                Kind = "terrain-chunk",
                Chunk = packet
            });
            Verify.Check(frame.Length <= Protocol.MaxFrameBytes + 4, "terrain fragment fits bounded TCP frame");
            transfer.Add(packet);
        }
        Verify.Check(transfer.Complete && transfer.Completed[chunk.Coordinate].SequenceEqual(bytes), "terrain fragments reproduce canonical payload");
        void Invalid(Action action, string name)
        {
            bool rejected = false;
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }
            Verify.Check(rejected, name);
        }
        Invalid(() => New().Add(new(chunk.Coordinate, ChunkCodec.Hash(bytes), 1, bytes[..10])), "out-of-order terrain fragment rejected");
        Invalid(() => New().Add(new(chunk.Coordinate, ChunkCodec.Hash(bytes), 0, new byte[32769])), "oversized fragment rejected");
        Invalid(() => new TerrainTransfer(manifest with { Manifest = [new(chunk.Coordinate, ChunkCodec.Hash(bytes), ChunkCodec.MaxBytes + 1)] }), "oversized decoded terrain rejected");
        var corrupt = (byte[])bytes.Clone();
        corrupt[^1] ^= 1;
        Invalid(() =>
        {
            var t = New();
            for (int offset = 0; offset < corrupt.Length; offset += 32768)
                t.Add(new(chunk.Coordinate, ChunkCodec.Hash(bytes), offset, corrupt.AsSpan(offset, Math.Min(32768, corrupt.Length - offset)).ToArray()));
        }, "corrupt terrain rejected by content hash");
        string cache = Path.Combine(Path.GetTempPath(), "StargateGame-cache-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TerrainStore(plan.Definition.SettingsHash, cache);
            store.Put(chunk);
            var reload = new TerrainStore(plan.Definition.SettingsHash, cache);
            Verify.Check(reload.TryLoad(chunk.Coordinate, out var loaded) && ChunkCodec.Encode(loaded).SequenceEqual(bytes), "disk cache canonical reload");
            var file = Directory.GetFiles(cache, "*.sgchunk").Single();
            var damaged = File.ReadAllBytes(file);
            damaged[^1] ^= 1;
            File.WriteAllBytes(file, damaged);
            Verify.Check(!new TerrainStore(plan.Definition.SettingsHash, cache).TryLoad(chunk.Coordinate, out _), "disk cache corruption is ignored");
        }
        finally
        {
            Directory.Delete(cache, true);
        }
        byte[][] Generate(PlanetPlan p) => new[] { new ChunkCoordinate(-4, 2), new ChunkCoordinate(3, -2), new ChunkCoordinate(-1, -1) }.Select(c => ChunkCodec.Encode(p.BuildChunk(c))).ToArray();
        var expected = Generate(plan);
        // Separate cache roots ensure worker comparisons actually run generation.
        foreach (bool parallel in new[] { false, true })
        {
            string path = Path.Combine(Path.GetTempPath(), "StargateGame-workers-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var service = new GenerationService(42, "desert", path, parallel: parallel);
                var order = new[] { new ChunkCoordinate(-1, -1), new ChunkCoordinate(3, -2), new ChunkCoordinate(-4, 2) };
                var latency = new System.Collections.Concurrent.ConcurrentBag<double>();
                var actual = await Task.WhenAll(order.Select(async c =>
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var result = await service.RequestAsync(c);
                    latency.Add(watch.Elapsed.TotalMilliseconds);
                    return result;
                }));
                Console.WriteLine($"CHUNK LATENCY parallel={parallel} coldRequests={latency.Count} minMs={latency.Min():F2} meanMs={latency.Average():F2} maxMs={latency.Max():F2}");
                Verify.Check(actual.Reverse().Select((c, i) => ChunkCodec.Encode(c).SequenceEqual(expected[i])).All(v => v), "generation order and parallel workers: " + parallel);
            }
            finally
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
        }
    }
}
