using System.Numerics;
using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Physics;
using StargateGame.Persistence;
using StargateGame.Client;

static class AtmosphereWaterChecks
{
    public static async Task Network()
    {
        var plan = new PlanetPlan(7319, "icy");
        Vector3 point = default;
        float best = 0;
        for (int z = -480; z <= 480; z += 4)
            for (int x = -480; x <= 480; x += 4)
            {
                var s = plan.Sample(x, z);
                if (s.Water == int.MinValue)
                    continue;
                float depth = (s.Water - s.Height) * .001f;
                if (depth > best)
                {
                    best = depth;
                    point = new(x, s.Water * .001f, z);
                }
            }
        Verify.Check(best > 3, "network fixture has deep water");
        var definition = plan.Definition with
        {
            Spawns = Enumerable.Range(0, 8).Select(i => point + new Vector3((i % 4 - 1.5f) * .3f, -1.4f, (i / 4 - .5f) * .3f)).ToArray(),
            Props = plan.Definition.Props.Select((p, i) => p with { Position = point + new Vector3((i - 1.5f) * .3f, -2, 1) }).ToArray()
        };
        string saves = Path.Combine(Path.GetTempPath(), "StargateWaterNetwork-" + Guid.NewGuid().ToString("N"));
        await using (var repository = new WorldRepository(saves))
            await repository.ResolveAsync(definition);
        await using var server = new StargateGame.Server.GameServer(System.Net.IPAddress.Loopback, 0, 8, 7319, "icy", true, saves);
        server.Start();
        await using var proxy = new DelayProxy(server.Port, 0);
        using var a = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "WaterDirect", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash));
        var joining = GameConnection.ConnectAsync("127.0.0.1", proxy.Port, "WaterDelayed", PhysicsWorld.PhysicsHash, PhysicsWorld.ContentHash);
        while (!joining.IsCompleted)
        {
            a.Poll();
            await Task.Delay(8);
        }
        using var b = new ClientSession(await joining);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool swimA = false, swimB = false;
        double difference = 0;
        while (clock.Elapsed.TotalSeconds < 8)
        {
            foreach (var client in new[] { a, b })
            {
                client.Poll();
                if (client.Failure is { } error)
                    throw new Exception(error);
                client.Step(0, 0, 0, 0, false, false, rise: true);
            }
            if (clock.Elapsed.TotalSeconds > 2)
            {
                difference = Math.Max(difference, Math.Abs(a.EnvironmentSeconds - b.EnvironmentSeconds));
                swimA |= a.LastSnapshot.Players.Any(p => p.Id == a.Local.Id && p.Swimming);
                swimB |= b.LastSnapshot.Players.Any(p => p.Id == b.Local.Id && p.Swimming);
            }
            await Task.Delay(16);
        }
        Verify.Check(swimA && swimB, "both network clients receive authoritative swimming states under delay/jitter");
        Verify.Check(difference < .3, "network planetary UTC agreement within 300 ms: " + difference.ToString("F4"));
        Verify.Check(a.World.Definition.Environment == b.World.Definition.Environment, "network clients share persisted environment profile");
        Verify.Check(a.LastSnapshot.Bodies.Any(body => body.Position.Y > point.Y - 1), "network snapshot contains buoyant risen crates");
    }

    static void Shoreline()
    {
        int[] heights = new int[TerrainChunkData.Count], water = Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray();
        for (int z = -1; z <= 65; z++)
            for (int x = -1; x <= 65; x++)
            {
                int index = (z + 1) * 67 + x + 1;
                heights[index] = (x - 20) * 200 + ((z & 1) == 0 ? 40 : 0);
                if (heights[index] < 0)
                    water[index] = 0;
            }
        var chunk = new TerrainChunkData(new(-1, -1), heights, water, new byte[heights.Length * 4], []);
        var surface = WaterSurfaceData.For(chunk);
        Verify.Check(surface.Triangles.Length > 0, "clipped shoreline contains water");
        Verify.Check(surface.Triangles.All(t => t.A.Y == 0 && t.B.Y == 0 && t.C.Y == 0 && t.Flow == Vector2.Zero), "lake plane is level without elevation offset or current");
        Verify.Check(surface.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).All(p => p.X <= -44.00001f + .00002f), "water ends at the bank instead of extending over dry terrain");
        var canonical = TerrainMesher.Build(chunk, 1);
        foreach (int step in new[] { 2, 4, 8 })
        {
            var mesh = TerrainMesher.Build(chunk, step);
            Verify.Check(mesh.WaterPositions.SequenceEqual(canonical.WaterPositions), "water mesh remains identical at terrain LOD " + step);
            var vertices = mesh.Positions.ToHashSet();
            Verify.Check(canonical.Positions.Where(p => p.X <= -42).All(vertices.Contains), "wet bed and shoreline retain canonical vertices at LOD " + step);
        }
        var exclusion = Enumerable.Repeat(int.MinValue, TerrainChunkData.Count).ToArray();
        for (int z = -1; z <= 65; z++)
            for (int x = -1; x < 20; x++)
                exclusion[(z + 1) * 67 + x + 1] = 1000;
        var exclusionChunk = new TerrainChunkData(new(-1, -1), new int[heights.Length], exclusion, new byte[heights.Length * 4], []);
        var ends = WaterSurfaceData.For(exclusionChunk).Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Where(p => p.X == -45).ToArray();
        Verify.Check(ends.Length > 0 && ends.All(p => p.Y == 0), "water meets the terrain on the wet side of an intentional dry boundary");
        var slopedWater = water.ToArray();
        for (int z = -1; z <= 65; z++)
            for (int x = -1; x <= 65; x++)
            {
                int i = (z + 1) * 67 + x + 1;
                if (slopedWater[i] != int.MinValue)
                    slopedWater[i] = 1000 - z * 10;
            }
        var river = new TerrainChunkData(new(-1, -1), heights, slopedWater, new byte[heights.Length * 4], []);
        Verify.Check(WaterSurfaceData.For(river).Triangles.All(t => t.Flow.Length() <= 1.0001f && t.Flow.Y >= -.0001f), "currents stay bounded and follow the downhill surface");
        // Regression: a river on a steep ledge previously draped a reflective
        // triangle down the outside bank, disconnected from the actual bed.
        TerrainChunkData Bank(ChunkCoordinate coordinate)
        {
            var h = new int[TerrainChunkData.Count];
            var w = Enumerable.Repeat(int.MinValue, h.Length).ToArray();
            for (int z = -1; z <= 65; z++)
                for (int x = -1; x <= 65; x++)
                {
                    int i = (z + 1) * 67 + x + 1;
                    h[i] = x < 20 ? 3000 : 0;
                    if (x < 20)
                        w[i] = 3500;
                }
            return new(coordinate, h, w, new byte[h.Length * 4], []);
        }
        var bank = Bank(new(0, 0));
        var before = ChunkCodec.Encode(bank);
        var bankSurface = WaterSurfaceData.For(bank);
        var shore = bankSurface.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Where(p => p.X >= 19).ToArray();
        Verify.Check(shore.Length > 0 && shore.All(p => p.X == 19 && p.Y == 3), "steep river edge closes on the bed without a hanging water skirt");
        Verify.Check(bankSurface.TrySample(18.5f, 32, out var near) && near.Height is > 3 and < 3.5f && !bankSurface.TrySample(19.5f, 32, out _), "water queries use the repaired shoreline rather than the original raised sheet");
        var adjacent = WaterSurfaceData.For(Bank(new(0, 1)));
        var border = bankSurface.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Where(p => p.Z == 64).ToHashSet();
        Verify.Check(border.SetEquals(adjacent.Triangles.SelectMany(t => new[] { t.A, t.B, t.C }).Where(p => p.Z == 64)), "shoreline correction is identical across chunk boundaries");
        Verify.Check(ChunkCodec.Encode(bank).SequenceEqual(before), "shoreline repair preserves canonical terrain, saves and collision samples");
    }
    public static async Task Run()
    {
        var id = Guid.Parse("8ded7928-3d22-4c36-b814-dbf7ee9a7d04");
        var e = PlanetEnvironmentDefinition.Create(id, 7319, "temperate");
        e.Validate();
        Verify.Check(e == PlanetEnvironmentDefinition.Create(id, 7319, "temperate"), "environment identity is deterministic");
        Verify.Check(e != PlanetEnvironmentDefinition.Create(Guid.NewGuid(), 7319, "temperate"), "same seed distinct locations have different clocks");
        double now = 1793923200, p = e.Phase(now), later = e.Phase(now + e.DaySeconds / 4);
        Verify.Check(Math.Abs((later - p + 1) % 1 - .25) < 1e-8, "offline elapsed time advances planet phase");
        Verify.Check(e.Sun(.5).Y > .999 && e.Sun(0).Y < -.999, "planet noon and midnight sun elevation");
        var plan = new PlanetPlan(7319, "temperate");
        bool rejected = false;
        try
        {
            new PlanetPlan(7319, "temperate", 2);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Verify.Check(rejected, "old terrain generator requires regeneration");
        int deep = 0;
        float maximum = 0;
        foreach (var preset in new[] { "temperate", "icy" })
        {
            var lakePlan = new PlanetPlan(7319, preset);
            for (int z = -504; z <= 504; z += 4)
                for (int x = -504; x <= 504; x += 4)
                {
                    var s = lakePlan.Sample(x, z);
                    if (s.Water == int.MinValue)
                        continue;
                    float d = (s.Water - s.Height) * .001f;
                    if (d > 1.8f)
                        deep++;
                    maximum = Math.Max(maximum, d);
                }
        }
        Console.WriteLine($"HYDROLOGY deepSamples={deep} maxDepth={maximum:F3}");
        Verify.Check(deep > 0 && maximum <= 12.001f, "new generation contains bounded deep lakes");
        var store = new TerrainStore("water-test-3", Path.Combine(Path.GetTempPath(), "StargateWaterTest"));
        foreach (var c in new ChunkCoordinate(0, 0).Around(2))
        {
            var h = new int[TerrainChunkData.Count];
            var w = Enumerable.Repeat(3000, TerrainChunkData.Count).ToArray();
            var chunk = new TerrainChunkData(c, h, w, new byte[h.Length * 4], []);
            store.Put(chunk, false);
            Verify.Check(ChunkCodec.Decode(ChunkCodec.Encode(chunk)).WaterBodies.Span.SequenceEqual(chunk.WaterBodies.Span), "deep water region codec roundtrip");
        }
        Verify.Check(WaterSurfaceData.TrySample(store, -.1f, -.1f, out var sample) && sample.Height == 3 && sample.Depth == 3, "negative-coordinate water query matches clipped surface");
        Shoreline();
        var definition = plan.Definition with
        {
            Entities = [],
            Props = [new(1, new(2, 1, 2), new(.5f), 10)]
        };
        using (var physics = new PhysicsWorld(definition, true, store, new ChunkCoordinate(0, 0).Around(1)))
        {
            physics.AddPlayer(new(1, "Swimmer", new(0, 1, 0), default, 0, 0, false, 0));
            for (int i = 0; i < 120; i++)
                physics.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 1, 0, 0, 0, 0, false, false) } });
            Verify.Check(physics.Player(1).Swimming && physics.Player(1).Position.Y is > 1 and < 2, "swimmer settles near surface");
            Console.WriteLine($"BUOYANCY {physics.Bodies()[0]}");
            Verify.Check(physics.Bodies()[0].Position.Y > 2, "crate rises with shape-based buoyancy");
            float start = physics.Player(1).Position.Y;
            for (int i = 0; i < 45; i++)
                physics.Step(new Dictionary<int, PlayerInput> { { 1, new(i + 121, 0, 0, 0, 0, false, false, SwimDive: true) } });
            Verify.Check(physics.Player(1).Position.Y < start - .5f, "continuous dive input descends");
            using var checkpoint = physics.Save();
            for (int i = 0; i < 30; i++)
                physics.Step();
            var expected = physics.Snapshot();
            physics.Restore(checkpoint);
            for (int i = 0; i < 30; i++)
                physics.Step();
            Verify.Check(physics.Snapshot().Players.SequenceEqual(expected.Players) && physics.Bodies().SequenceEqual(expected.Bodies), "water physics replays across prediction checkpoints");
            var state = physics.Snapshot();
            var submerged = state.Bodies[0] with
            {
                Position = new(2, 1, 2),
                Rotation = Quaternion.CreateFromYawPitchRoll(.5f, .2f, .4f),
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
                Sleeping = true
            };
            physics.Apply(state with
            {
                Bodies = [submerged]
            });
            for (int i = 0; i < 180; i++)
                physics.Step();
            Verify.Check(physics.Bodies()[0].Position.Y > 2 && !physics.Bodies()[0].Sleeping, "submerged sleeping rotated crate wakes and floats");
        }
        string save = Path.Combine(Path.GetTempPath(), "StargateAtmosphere-" + Guid.NewGuid().ToString("N"));
        Guid location;
        PlanetEnvironmentDefinition? retained;
        await using (var repo = new WorldRepository(save))
        {
            var s = await repo.ResolveAsync(plan.Definition);
            location = s.Id;
            retained = s.Definition.Environment;
            await repo.AddAliasAsync("test", location);
            Verify.Check((await repo.ResolveAliasAsync("test")).Definition.Environment == retained, "aliases retain the same planetary clock");
        }
        await using (var repo = new WorldRepository(save))
        {
            var s = await repo.ResolveAsync(plan.Definition);
            Verify.Check(s.Id == location && s.Definition.Environment == retained, "restart retains planet clock and identity");
        }
        string database = Path.Combine(save, "default", "universe.sqlite");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE locations SET generator=2";
            command.ExecuteNonQuery();
        }
        bool regeneration = false;
        await using (var repo = new WorldRepository(save))
            try
            {
                await repo.ResolveAsync(plan.Definition);
            }
            catch (InvalidDataException ex)
            {
                regeneration = ex.Message.Contains("requires regeneration") && ex.Message.Contains("--universe");
            }
        Verify.Check(regeneration, "old persisted world gives an explicit regeneration instruction");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT generator FROM locations";
            Verify.Check(Convert.ToInt32(command.ExecuteScalar()) == 2, "rejecting an old world preserves its saved record");
        }
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = $"UPDATE locations SET generator={PlanetSettings.Version},scenery={PlanetSettings.SceneryVersion - 1}";
            command.ExecuteNonQuery();
        }
        bool oldScenery = false;
        await using (var repo = new WorldRepository(save))
            try
            {
                await repo.ResolveAsync(plan.Definition);
            }
            catch (InvalidDataException ex)
            {
                oldScenery = ex.Message.Contains("requires regeneration");
            }
        Verify.Check(oldScenery, "old scenery catalogue cannot silently reroll trees around saved buildings");
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + database + ";Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT scenery FROM locations";
            Verify.Check(Convert.ToInt32(command.ExecuteScalar()) == PlanetSettings.SceneryVersion - 1, "rejecting old scenery preserves its saved catalogue version");
        }
    }
}
