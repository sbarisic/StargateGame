using System.Numerics;
using System.Text;
using StargateGame.Shared;
using StargateGame.WorldGen;
using StargateGame.Client;
using StargateGame.Physics;

static class MeadowPropChecks
{
    public static async Task Network()
    {
        string saves = Path.Combine(Path.GetTempPath(), "StargateMeadow-" + Guid.NewGuid());
        await using var server = new StargateGame.Server.GameServer(System.Net.IPAddress.Loopback, 0, 2, 7319, "temperate", saveDirectory: saves);
        server.Start();
        await using var proxy = new DelayProxy(server.Port);
        using var a = new ClientSession(await NetworkChecks.Connect(proxy.Port, "BarrelPusher"));
        using var b = new ClientSession(await NetworkChecks.Connect(proxy.Port, "Observer"));
        var barrel = a.World.Definition.Props.Single(p => p.Kind == PropKind.BlackBarrel);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool moved = false;
        float maximumMotion = 0;
        while (clock.Elapsed.TotalSeconds < 14)
        {
            a.Poll();
            b.Poll();
            if (a.Failure is { } failure)
                throw new Exception(failure);
            if (b.Failure is { } other)
                throw new Exception(other);
            double t = clock.Elapsed.TotalSeconds;
            var destination = barrel.Position + new Vector3(0, 0, t < 5 ? 3 : -6);
            var delta = destination - a.Local.Position;
            var input = new Vector2(delta.X, -delta.Z);
            if (input.Length() > 1)
                input = Vector2.Normalize(input);
            a.Step(t < 10 ? input.X : 0, t < 10 ? input.Y : 0, 0, 0, false, false);
            b.Step(0, 0, 0, 0, false, false);
            var pose = a.RenderPrediction().Bodies.Single(p => p.Id == barrel.Id);
            maximumMotion = Math.Max(maximumMotion, Vector2.Distance(new(pose.Position.X, pose.Position.Z), new(barrel.Position.X, barrel.Position.Z)));
            moved |= maximumMotion > .3f;
            await Task.Delay(3);
        }
        Verify.Check(a.PlayerCount == 2 && b.PlayerCount == 2, "two generated-world clients with 100 ms RTT and jitter");
        Verify.Check(moved, $"predicted barrel pushing changes visible position ({maximumMotion:F2} m)");
        foreach (var client in new[] { a, b })
            foreach (var body in client.Physics.Bodies())
            {
                var authoritative = client.LastSnapshot.Bodies.Single(p => p.Id == body.Id);
                Verify.Check(Vector3.Distance(body.Position, authoritative.Position) < .03f && Math.Abs(Quaternion.Dot(body.Rotation, authoritative.Rotation)) > MathF.Cos(MathF.PI / 180), "mixed prop prediction converges " + body.Id);
            }
    }
    public static void Run()
    {
        Directory.CreateDirectory("artifacts/meadow");
        foreach (string preset in PlanetSettings.Presets)
        {
            List<double> reductions = [];
            List<object> rows = [];
            for (int i = 0; i < 32; i++)
            {
                int seed = i == 0 ? 7319 : i * 7919;
                var p = new PlanetPlan(seed, preset);
                double reduction = 1 - p.ChannelLength / p.OriginalChannelLength;
                reductions.Add(reduction);
                rows.Add(new
                {
                    seed,
                    original = p.OriginalChannelLength,
                    current = p.ChannelLength,
                    reduction
                });
                var channels = p.Channels.ToArray();
                var downstream = p.Downstream.ToArray();
                var heights = p.DrainageHeight.ToArray();
                Verify.Check(Enumerable.Range(0, channels.Length).Where(j => channels[j]).All(j => downstream[j] >= 0 && heights[j] >= heights[downstream[j]] && (downstream[downstream[j]] < 0 || channels[downstream[j]])), preset + " downstream continuity " + seed);
                {
                    var svg = new StringBuilder("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1024 1024'><rect width='1024' height='1024' fill='#202b24'/>");
                    var accumulation = p.FlowAccumulation.ToArray();
                    for (int j = 0; j < channels.Length; j++)
                        if (accumulation[j] >= 160 && downstream[j] >= 0)
                        {
                            int d = downstream[j];
                            svg.Append($"<path d='M {j % 257 * 4} {j / 257 * 4} L {d % 257 * 4} {d / 257 * 4}' stroke='{(channels[j] ? "#62c9fa" : "#66716a")}' stroke-width='{(channels[j] ? 2 : 1)}'/>");
                        }
                    svg.Append("</svg>");
                    File.WriteAllText($"artifacts/meadow/rivers-{preset}-{seed}.svg", svg.ToString());
                    if (i == 0)
                        File.WriteAllText($"artifacts/meadow/rivers-{preset}.svg", svg.ToString());
                }
            }
            reductions.Sort();
            double median = (reductions[15] + reductions[16]) / 2;
            Console.WriteLine($"RIVER {preset} median reduction {median:P2}");
            File.WriteAllText($"artifacts/meadow/rivers-{preset}.json", System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (preset != "desert")
                Verify.Check(median >= .60 && median <= .70, preset + " channel length reduction 60–70%");
        }
        var barrel = PropDefinition.Barrel(3, new(0, .612f, 0), false);
        var hull = PropGeometry.For(barrel);
        Verify.Check(hull.Vertices.Length == 128 && barrel.Valid, "rounded four-ring barrel hull");
        var body = new BodyState(3, new(0, 2, 0), Quaternion.Identity, default, default, false);
        Verify.Check(PropGeometry.RayHit(new(0, 2, 2), -Vector3.UnitZ, body, barrel, 4), "ray hits barrel centre");
        Verify.Check(!PropGeometry.RayHit(new(.35f, 3, .35f), -Vector3.UnitY, body, barrel, 4), "ray misses empty barrel bounding-box corner");
        var upright = EventHorizonState.Footprint(body, barrel.HalfExtent, Vector3.Zero, 4, true);
        var rotated = EventHorizonState.Footprint(body with
        {
            Rotation = Quaternion.CreateFromYawPitchRoll(.6f, .5f, .4f)
        }, barrel.HalfExtent, Vector3.Zero, 4, true);
        Verify.Check(upright.Count >= 4 && rotated.Count > 4 && !upright.SequenceEqual(rotated), "barrel membrane contour follows rotated collision hull");
        var world = PlanetDefinition.Default with
        {
            Entities = [],
            Props = [barrel],
            Preset = "temperate"
        };
        using var physics = new PhysicsWorld(world, false);
        physics.AddStaticBox(new(0, -.5f, 0), new(20, .5f, 20));
        physics.AddProp(barrel);
        var state = physics.Snapshot();
        physics.Apply(state with
        {
            Bodies = [state.Bodies[0] with { Position = new(0, .39f, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), LinearVelocity = new(0, 0, 2), AngularVelocity = new(5, 0, 0) }]
        });
        for (int i = 0; i < 120; i++)
            physics.Step();
        var rolled = physics.Snapshot().Bodies[0];
        Verify.Check(float.IsFinite(rolled.Position.Y) && rolled.Position.Y > .30f && Math.Abs(rolled.Position.Z) > .25f, "barrel rolls and remains supported");
        var crate = new PropDefinition(1, new(3, .5f, 0), new(.5f), 10);
        physics.AddProp(crate);
        physics.AddPlayer(new(1, "Walker", new(3, 1.1f, 0), default, 0, 0, false, 0));
        for (int i = 0; i < 90; i++)
            physics.Step();
        Verify.Check(physics.Player(1).SupportId == 1 && physics.Player(1).Surface == GroundSurface.Wood, "supporting crate supplies wood footsteps");
        var store = new TerrainStore("barrel-water");
        foreach (var c in new ChunkCoordinate(0, 0).Around(1))
            store.Put(new TerrainChunkData(c, new int[TerrainChunkData.Count], Enumerable.Repeat(3000, TerrainChunkData.Count).ToArray(), new byte[TerrainChunkData.Count * 4], []), false);
        var floating = barrel with
        {
            Position = new(2, 1, 2)
        };
        using var waterPhysics = new PhysicsWorld(world with
        {
            Props = [floating]
        }, true, store, new ChunkCoordinate(0, 0).Around(1));
        for (int i = 0; i < 180; i++)
            waterPhysics.Step();
        Verify.Check(waterPhysics.Bodies()[0].Position.Y > 2, "sealed barrel rises through actual hull buoyancy");
        using var checkpoint = waterPhysics.Save();
        for (int i = 0; i < 30; i++)
            waterPhysics.Step();
        var expected = waterPhysics.Snapshot();
        waterPhysics.Restore(checkpoint);
        for (int i = 0; i < 30; i++)
            waterPhysics.Step();
        Verify.Check(waterPhysics.Snapshot().Bodies.SequenceEqual(expected.Bodies), "barrel buoyancy rollback remains deterministic");
    }
}
