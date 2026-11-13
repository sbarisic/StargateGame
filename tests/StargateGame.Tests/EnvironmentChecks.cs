using System.Numerics;
using StargateGame.Client;
using StargateGame.Shared;

static class EnvironmentChecks
{
    public static async Task Network()
    {
        string saves = Path.Combine(Path.GetTempPath(), "stargate-weather-" + Guid.NewGuid().ToString("N"));
        await using var server = new StargateGame.Server.GameServer(System.Net.IPAddress.Loopback, 0, 8, 7319, "icy", true, saves);
        server.Start();
        await using var proxy = new DelayProxy(server.Port, 0);
        using var a = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", server.Port, "WeatherDirect", StargateGame.Physics.PhysicsWorld.PhysicsHash, StargateGame.Physics.PhysicsWorld.ContentHash));
        using var b = new ClientSession(await GameConnection.ConnectAsync("127.0.0.1", proxy.Port, "WeatherDelayed", StargateGame.Physics.PhysicsWorld.PhysicsHash, StargateGame.Physics.PhysicsWorld.ContentHash));
        var clocks = new[] { new WeatherClock(), new WeatherClock() };
        var clients = new[] { a, b };
        var timer = System.Diagnostics.Stopwatch.StartNew();
        double maxDifference = 0;
        int comparisons = 0;
        while (timer.Elapsed.TotalSeconds < 7)
        {
            WeatherFrame[] frames = new WeatherFrame[2];
            for (int i = 0; i < 2; i++)
            {
                var c = clients[i];
                c.Poll();
                if (c.Failure is { } error)
                    throw new Exception(error);
                c.Step(0, 0, 0, 0, false, false);
                frames[i] = WeatherFrame.Sample(c.World.Definition.Preset, c.World.Definition.Seed, clocks[i].Update(c.World.Definition.LocationId, c.ServerTick / 60d, timer.Elapsed.TotalSeconds), WinterWeather.Auto);
            }
            if (timer.Elapsed.TotalSeconds > 2)
            {
                maxDifference = Math.Max(maxDifference, Math.Abs(frames[0].Gust - frames[1].Gust));
                comparisons++;
            }
            await Task.Delay(16);
        }
        Verify.Check(a.World.Definition.LocationId == b.World.Definition.LocationId && a.PlayerCount == 2 && b.PlayerCount == 2, "direct and delayed winter clients share one retained world");
        Verify.Check(comparisons > 100 && maxDifference < .025, "live 100 ms RTT/jitter weather agreement, maximum gust difference " + maxDifference.ToString("F5"));
    }
    public static void Run()
    {
        var night = AtmosphereIllumination.SkyFill(-1, "temperate");
        var dusk = AtmosphereIllumination.SkyFill(0, "temperate");
        var noon = AtmosphereIllumination.SkyFill(1, "temperate");
        Verify.Check(night == new Vector3(.07f, .085f, .12f) && noon == new Vector3(.24f, .28f, .34f), "clear-day fill remains cooler and weaker than direct sunlight");
        Verify.Check(dusk.X > .25f && dusk.Y > .30f && AtmosphereIllumination.GroundBounce(0) > .2f, "dusk retains readable diffuse sky and ground illumination");
        Verify.Check(AtmosphereIllumination.GroundBounce(-1) == 0 && AtmosphereIllumination.GroundBounce(1) == .28f, "daytime ground bounce keeps the deep-night floor unchanged");
        var settings = new GroundCoverSettings();
        var planet = PlanetDefinition.Default with
        {
            Preset = "temperate",
            Entities = [],
            Spawns = [],
            LocationId = Guid.Parse("798105bd-fd0c-4dca-ac7d-aad5edac143b")
        };
        TerrainChunkData Flat(ChunkCoordinate c, bool water = false, float slope = 0, byte rock = 0)
        {
            var heights = new int[TerrainChunkData.Count];
            var wet = new int[heights.Length];
            var material = new byte[heights.Length * 4];
            for (int z = 0; z < 67; z++)
                for (int x = 0; x < 67; x++)
                {
                    int i = z * 67 + x;
                    heights[i] = (int)((c.X * 64 + x - 1) * slope * 1000);
                    wet[i] = water ? heights[i] + 100 : int.MinValue;
                    material[i * 4 + 1] = rock;
                    material[i * 4 + 2] = 150;
                }
            return new(c, heights, wet, material, []);
        }
        var chunk = Flat(new(-1, -1));
        var tile = new GrassTile(-2, -2);
        var neighbours = new Dictionary<ChunkCoordinate, TerrainChunkData>();
        foreach (var c in new ChunkCoordinate(-1, -1).Around(2).Where(c => c.Valid))
            neighbours[c] = Flat(c);
        var crowns = new[] { new SurfaceCanopy(new(0, -12), new(9, 7)) };
        var map = SurfaceEcology.Build(chunk, neighbours, crowns, [], default);
        var otherMap = SurfaceEcology.Build(neighbours[new(0, -1)], neighbours, crowns, [], default);
        Verify.Check(Vector4.Distance(map.Sample(0, -12), otherMap.Sample(0, -12)) < .00001, "surface canopy and wetness agree across chunk boundaries");
        var reversed = SurfaceEcology.Build(chunk, neighbours.Reverse().ToDictionary(p => p.Key, p => p.Value), crowns, [], default);
        Verify.Check(map.Values.SequenceEqual(reversed.Values), "surface masks do not depend on chunk arrival order");
        var cleared = SurfaceEcology.Build(chunk, neighbours, [], [], default);
        Verify.Check(map.Sample(-2, -12).X > .5f && cleared.Sample(-2, -12).X == 0, "tree removal clears canopy influence beyond the trunk");
        var planted = FoliagePlacement.Build(tile, chunk, planet, map, [], default, 6);
        Verify.Check(planted.Length > 0 && planted.SequenceEqual(FoliagePlacement.Build(tile, chunk, planet, map, [], default, 6)), "imported foliage repeats across reloads");
        Verify.Check(planted.All(p => p.Species >= 0 && p.Species < 11 && Math.Abs(p.Root.Y + .02f - chunk.Sample(p.Root.X, p.Root.Z)) < .0001f), "six imported grasses and five woodland plants use valid indices and canonical terrain height");
        CoverExclusion wholeTile = new(new(-16, -1, -16), new(-8, 3, -8));
        Verify.Check(FoliagePlacement.Build(tile, chunk, planet, map, [wholeTile], default).Length == 0, "building edits exclude imported clumps");
        var waterMap = SurfaceEcology.Build(chunk, new Dictionary<ChunkCoordinate, TerrainChunkData> { { chunk.Coordinate, Flat(chunk.Coordinate, true) } }, [], [], default);
        Verify.Check(waterMap.Sample(-12, -12).W == 0 && FoliagePlacement.Build(tile, chunk, planet, waterMap, [], default).Length == 0, "water excludes vegetation and dust emission");
        var gatePlanet = planet with
        {
            Preset = "desert",
            Entities = [new("gate", "stargate", new(-12, 0, -15))]
        };
        var gateMap = SurfaceEcology.Build(chunk, neighbours, [], [], default, gatePlanet);
        Verify.Check(gateMap.Sample(-12, -12).W == 0, "desert dust excludes the reserved gate approach");
        var rockMap = SurfaceEcology.Build(Flat(chunk.Coordinate, rock: 255), new Dictionary<ChunkCoordinate, TerrainChunkData>(), [], [], default);
        Verify.Check(rockMap.Sample(-12, -12).W == 0, "desert dust excludes exposed-rock material even on gentle slopes");
        var dust = Enumerable.Range(0, 3600).Select(i => DesertDust.Sample(7319, i * .1, true)).ToArray();
        Verify.Check(dust.All(d => d.Extinction >= .0012f && d.Extinction <= .0025f && d.Gust >= 0 && d.Gust <= 1), "desert extinction stays in the approved range");
        Verify.Check(dust.Zip(dust.Skip(1)).All(d => Vector2.Distance(d.First.Travel, d.Second.Travel) < .61f), "integrated desert wind travels continuously through gusts");
        var quiet = DesertDust.Sample(7319, 300, false);
        Verify.Check(!quiet.Moving && quiet.Gust == 0 && quiet.Travel == Vector2.Zero && quiet.Extinction == .0012f, "weather off stops moving dust but retains baseline haze");
        Verify.Check(DesertDust.Budget(0) == (1200, 48) && DesertDust.Budget(1) == (400, 32) && DesertDust.Budget(2) == (2400, 64), "desert particle quality limits");
        var full = GroundCover.Candidates(tile, chunk, planet, settings, 1, []);
        Verify.Check(full.Count > 100 && full.All(t => t.Height >= settings.MinHeight && t.Height <= settings.MaxHeight), "dense grass with requested physical height range");
        Verify.Check(GrassTile.At(-.01f, -8.01f) == new GrassTile(-1, -2), "negative ground-cover cells use floor division");
        Verify.Check(full.SequenceEqual(GroundCover.Candidates(tile, chunk, planet, settings, 1, [])), "returning to location reproduces every tuft");
        var low = GroundCover.Candidates(tile, chunk, planet, settings, 1 / 3f, []);
        Verify.Check(low.All(full.Contains) && low.Count < full.Count / 2, "lower quality is a stable subset, not regenerated positions");
        Verify.Check(!full.SequenceEqual(GroundCover.Candidates(tile, chunk, planet with
        {
            LocationId = Guid.NewGuid()
        }, settings, 1, [])), "location identity separates same-seed ground cover");
        Verify.Check(GroundCover.Candidates(tile, Flat(new(-1, -1), true), planet, settings, 1, []).Count == 0, "grass excludes shallow water");
        Verify.Check(GroundCover.Candidates(tile, Flat(new(-1, -1), slope: 1), planet, settings, 1, []).Count == 0, "grass excludes steep slopes");
        Verify.Check(GroundCover.Candidates(tile, Flat(new(-1, -1), rock: 255), planet, settings, 1, []).Count == 0, "grass excludes exposed rock");
        Verify.Check(GroundCover.Candidates(tile, chunk, planet with
        {
            Preset = "icy"
        }, settings, 1, []).Count == 0, "icy terrain has no temperate grass");
        CoverExclusion exclusion = new(new(-16, -1, -16), new(-12, 2, -8));
        var cut = GroundCover.Candidates(tile, chunk, planet, settings, 1, [exclusion]);
        Verify.Check(cut.Count > 0 && cut.Count < full.Count && cut.All(t => !exclusion.Contains(t.Root) && full.Contains(t)), "foundation/scenery exclusions preserve surrounding tufts");
        var roof = exclusion with
        {
            Min = exclusion.Min + Vector3.UnitY * 4,
            Max = exclusion.Max + Vector3.UnitY * 4
        };
        Verify.Check(GroundCover.Candidates(tile, chunk, planet, settings, 1, [roof]).SequenceEqual(full), "elevated roof alone does not remove ground grass");
        var slopeChunk = Flat(new(-1, -1), slope: .1f);
        var slopeTufts = GroundCover.Candidates(tile, slopeChunk, planet, settings, 1, []);
        var hybridSurface = HybridGrass.Surface(tile, slopeChunk, planet, settings, [], null, slopeTufts, default);
        Verify.Check(hybridSurface.SequenceEqual(HybridGrass.Surface(tile, slopeChunk, planet, settings, [], null, slopeTufts, default)), "hybrid grass fields reproduce exactly");
        Verify.Check(Enumerable.Range(0, hybridSurface.Length).All(i => Math.Abs(hybridSurface[i].X + .015f - slopeChunk.Sample(tile.X * 8 + i % 33 * .25f, tile.Z * 8 + i / 33 * .25f)) < .00001), "hybrid grass roots follow slopes including negative world coordinates");
        var excludedSurface = HybridGrass.Surface(tile, chunk, planet, settings, [wholeTile], null, full, default);
        Verify.Check(excludedSurface.All(p => p.Y == 0 && p.W == 0), "hybrid fields retain hard building exclusions despite existing candidates");
        Verify.Check(HybridGrass.Transition(1) == new Vector2(12, 20) && HybridGrass.Transition(0) == new Vector2(20, 28) && HybridGrass.Transition(2) == new Vector2(28, 36), "hybrid transition distances match all quality tiers");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { HybridGrass.Surface(tile, chunk, planet, settings, [], null, full, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            Verify.Check(stopped, "travel cancellation interrupts grass field generation");
        }
        Verify.Check(slopeTufts.All(t => Math.Abs(t.Root.Y + .015f - slopeChunk.Sample(t.Root.X, t.Root.Z)) < .0001f), "grass uses canonical triangle surface height");
        var boundaryA = GroundCover.Candidates(new(-1, -1), chunk, planet, settings, 1, []);
        var boundaryB = GroundCover.Candidates(new(0, -1), Flat(new(0, -1)), planet, settings, 1, []);
        Verify.Check(boundaryA.All(t => t.Root.X < 0) && boundaryB.All(t => t.Root.X >= 0) && !boundaryA.Select(t => t.Root).Intersect(boundaryB.Select(t => t.Root)).Any(), "cross-chunk tufts have unique owners and no duplicated seam");
        var mesh = GroundCover.Build(tile, chunk, planet, settings, 1, [], default);
        Verify.Check(mesh.Positions.Length == full.Count * 8 * 9 && mesh.Normals.Length == mesh.Positions.Length, "eight tapered three-triangle blades per clump");
        var records = GroundCover.Instances(tile, slopeChunk, planet, settings, [], default);
        Verify.Check(records.Records.Length % 128 == 0 && records.Count > 0, "six-texel clump records remain row-aligned");
        var ordered = slopeTufts.OrderBy(t => t.Rank).ToArray();
        Verify.Check(Enumerable.Range(0, records.Count).All(i => Enumerable.Range(0, 8).All(b => Math.Abs(records.Records[i * 6 + 2 + b / 4][b % 4] + .015f - slopeChunk.Sample(GroundCover.BladeRoot(ordered[i], b, slopeChunk).X, GroundCover.BladeRoot(ordered[i], b, slopeChunk).Z)) < .0001f)), "every blade root follows the canonical sloped ground");
        Verify.Check(SurfaceEcology.MeadowCoverage(new(0, .8f, 0, 1), .7f) > SurfaceEcology.MeadowCoverage(new(0, .1f, 0, 1), .7f), "moist ground supports fuller clumps");
        var protectedPlanet = planet with
        {
            Entities = [new("gate", "stargate", new(-12, 0, -15))],
            Spawns = [new(-12, 0, -12)]
        };
        Verify.Check(GroundCover.Candidates(tile, chunk, protectedPlanet, settings, 1, []).All(t => Math.Abs(t.Root.X + 12) >= 3.4f || t.Root.Z < -16.5f), "reserved gate approach stays clear");
        var light = WeatherFrame.Sample("icy", 7319, 10, WinterWeather.LightSnow);
        var storm = WeatherFrame.Sample("icy", 7319, 10, WinterWeather.Storm);
        Verify.Near(MathF.Exp(-115 * (light.Fog.X + light.Fog.Y)), .5f, .0001f, "light snow half transmission at 115 m");
        Verify.Near(MathF.Exp(-65 * (storm.Fog.X + storm.Fog.Y)), .5f, .0001f, "storm half transmission at 65 m");
        Verify.Near(light.SunScale, .35f, .0001f, "winter sun starts at 35 percent");
        Verify.Near(storm.SunScale, .15f, .0001f, "storm sun falls to 15 percent");
        Verify.Check(Enumerable.Range(0, 600).All(i => Math.Abs((WeatherFrame.WindTravel(7319, i + .001, WinterWeather.Auto) - WeatherFrame.WindTravel(7319, i - .001, WinterWeather.Auto)) / .002 - WeatherFrame.Sample("icy", 7319, i, WinterWeather.Auto).Wind.Length()) < .001), "cloud and particle travel follows changing wind without phase jumps");
        var cycle = Enumerable.Range(0, 3600).Select(i => WeatherFrame.Sample("icy", 7319, i / 10d, WinterWeather.Auto)).ToArray();
        Verify.Check(cycle.All(w => w.Snow && w.Snowfall >= .35 && w.AmbientScale >= .65f && w.AmbientScale <= .8f), "winter stays snowy and retains ambient lighting throughout gust cycles");
        Verify.Check(cycle.Zip(cycle.Skip(1)).All(p => Math.Abs(p.First.Gust - p.Second.Gust) < .01f), "weather changes smoothly over tens of seconds");
        Verify.Check(WeatherFrame.Sample("temperate", 1, 0, WinterWeather.Storm).Snow && !WeatherFrame.Sample("desert", 1, 0, WinterWeather.Auto).Snow && !WeatherFrame.Sample("icy", 1, 0, WinterWeather.Off).Snow, "forced snow crosses biomes while auto and clear preserve biome defaults");
        var a = new WeatherClock();
        var b = new WeatherClock();
        double maxDifference = 0;
        for (int i = 0; i < 6000; i++)
        {
            double time = i / 60d;
            var wa = WeatherFrame.Sample("icy", 7319, a.Update(planet.LocationId, Math.Floor(time * 20) / 20, time), WinterWeather.Auto);
            var wb = WeatherFrame.Sample("icy", 7319, b.Update(planet.LocationId, Math.Max(0, Math.Floor((time - .1 - .02 * Math.Sin(time)) * 20) / 20), time), WinterWeather.Auto);
            maxDifference = Math.Max(maxDifference, Math.Abs(wa.Gust - wb.Gust));
        }
        Verify.Check(maxDifference < .012, "two render clocks agree on gusts with 100 ms delay and jitter");
        a.Clear();
        Verify.Check(a.Update(planet.LocationId, 25, 200) == 25 && a.Update(Guid.NewGuid(), 3, 201) == 3, "reconnect and location change reset extrapolated clock");
        Verify.Check(GroundCoverSettings.Budget(0) == (112, 24) && WeatherSettings.Budget(0) == (4000, 512), "Performance ground-cover/weather budgets");
        Verify.Check(GroundCoverSettings.Budget(1) == (64, 12) && WeatherSettings.Budget(1) == (1500, 256), "Low ground-cover/weather budgets");
        Verify.Check(GroundCoverSettings.Budget(2) == (160, 24) && WeatherSettings.Budget(2) == (8000, 1024), "High ground-cover/weather budgets");
        var nearRecords = GroundCover.Instances(tile, chunk, planet, settings, [], CancellationToken.None);
        var outerRecords = GroundCover.Instances(tile, chunk, planet, settings, [], CancellationToken.None, maximumRank: .25f);
        Verify.Check(outerRecords.Count > 0 && outerRecords.Count < nearRecords.Count &&
            outerRecords.Records.Take(outerRecords.Count * 6).SequenceEqual(nearRecords.Records.Take(outerRecords.Count * 6)),
            "outer grass promotes to identical near roots and blade attributes at negative coordinates");
        foreach (int quality in new[] { 0, 1, 2 })
        {
            var (range, density) = GroundCoverSettings.Budget(quality);
            Verify.Check(Enumerable.Range(40, range - 39).All(d => GroundCoverSettings.DistanceDensity(d, range) * density / 24f + .025f <= .25f),
                "outer grass record subset contains every potentially visible blade");
        }
    }
}
