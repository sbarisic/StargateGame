using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Persistence;

static class RiverChecks
{
    public static async Task Run()
    {
        var current = new PlanetPlan(7319, "temperate");
        bool rejected = false;
        try
        {
            new PlanetPlan(7319, "temperate", 5);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Verify.Check(rejected, "pre-settlement generator requires regeneration");

        float newJump = 0, raised = 0;
        int wet = 0;
        for (int z = -270; z <= -240; z++)
            for (int x = 80; x <= 110; x++)
            {
                var s = current.Sample(x, z);
                raised = Math.Max(raised, s.Height * .001f - current.NaturalHeight(x, z));
                if (s.Water != int.MinValue)
                    wet++;
                foreach (var (dx, dz) in new[] { (1, 0), (0, 1) })
                {
                    var q = current.Sample(x + dx, z + dz);
                    if (s.Water != int.MinValue && q.Water != int.MinValue)
                        newJump = Math.Max(newJump, Math.Abs(s.Water - q.Water) * .001f);
                }
            }
        Console.WriteLine($"RIVER reported-bank newStep={newJump:F3}m raised={raised:F4}m wetSamples={wet}");
        Verify.Check(raised <= .0006f && wet > 200, "new river carves into natural ground without raising a shelf or removing the river");
        // Retained measurement from artifacts/river-fix/rivers.log: generator 4 was 2.119 m.
        Verify.Check(newJump < 2.119f * .5f, "reported bend no longer switches between disconnected reach heights");
        var chunk = current.BuildChunk(new(1, -4));
        var mesh = TerrainMesher.Build(chunk, 1);
        Verify.Check(mesh.WaterPositions.Zip(mesh.WaterFlow).GroupBy(v => v.First).All(g => g.Select(v => v.Second).Distinct().Count() == 1), "shared water vertices have continuous animation flow");
        var neighbour = TerrainMesher.Build(current.BuildChunk(new(1, -5)), 8);
        // Zero-depth endpoints can bound water on just one side of the seam.
        bool Wet(System.Numerics.Vector3 p) => p.Z == -256 && p.Y > chunk.Sample(p.X, p.Z) + .0001f;
        var edge = mesh.WaterPositions.Zip(mesh.WaterFlow).Where(v => Wet(v.First)).ToHashSet();
        var otherEdge = neighbour.WaterPositions.Zip(neighbour.WaterFlow).Where(v => Wet(v.First)).ToHashSet();
        Verify.Check(edge.Count > 0 && edge.SetEquals(otherEdge), "water geometry and interpolated flow agree across chunk and LOD boundaries");
        foreach (int step in new[] { 1, 2, 4, 8 })
        {
            var m = TerrainMesher.Build(chunk, step);
            var attributes = TerrainMesher.WaterAttributes(m);
            Verify.Check(attributes.Zip(m.WaterPositions).All(v => Math.Abs(v.First.X - Math.Max(0, v.Second.Y - chunk.Sample(v.Second.X, v.Second.Z))) < .00001f), "water shader receives actual bed depth at terrain LOD " + step);
        }
        var far = FarTile.Decode(FarTile.Build(chunk, "temperate").Encode(), chunk.Coordinate);
        Verify.Check(TerrainMesher.WaterAttributes(far.Mesh).SequenceEqual(TerrainMesher.WaterAttributes(mesh)), "far-tile depth reconstruction matches canonical water without a wire-format change");
        string save = Path.Combine(Path.GetTempPath(), "StargateLegacyRiver-" + Guid.NewGuid().ToString("N"));
        await using (var repo = new WorldRepository(save))
            await repo.ResolveAsync(current.Definition);
        await using (var repo = new WorldRepository(save))
        {
            var loaded = await repo.ResolveAsync(current.Definition);
            Verify.Check(loaded.Definition.GeneratorVersion == PlanetSettings.Version && loaded.Definition.SettingsHash == current.Definition.SettingsHash, "loading current worlds preserves terrain cache identity");
            using var generation = new GenerationService(7319, "temperate", version: loaded.Definition.GeneratorVersion);
            Verify.Check(ChunkCodec.Encode(await generation.RequestAsync(new(1, -4))).SequenceEqual(ChunkCodec.Encode(current.BuildChunk(new(1, -4)))), "saved settlement terrain regenerates byte-for-byte");
        }
        Verify.Check(current.Definition.SettingsHash != PlanetSettings.Hash(7319, "temperate", 5), "settlement generator uses an independent terrain cache");
        await using (var server = new StargateGame.Server.GameServer(System.Net.IPAddress.Loopback, 0, 8, 7319, "temperate", true, save))
        {
            server.Start();
            using var client = new StargateGame.Client.ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "LegacyRiver", StargateGame.Physics.PhysicsWorld.PhysicsHash, StargateGame.Physics.PhysicsWorld.ContentHash));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            TerrainChunkData? received = null;
            while (timer.Elapsed.TotalSeconds < 20)
            {
                client.Poll();
                if (client.Failure is { } error)
                    throw new Exception(error);
                if (client.Terrain is not null && client.Terrain.TryGetChunk(new(1, -4), out received))
                    break;
                await Task.Delay(10);
            }
            Verify.Check(server.CurrentPlanet.GeneratorVersion == PlanetSettings.Version && received is not null && ChunkCodec.Encode(received).SequenceEqual(ChunkCodec.Encode(current.BuildChunk(new(1, -4)))), "server sends reproducible current terrain when reopening a saved world");
        }
    }
}
