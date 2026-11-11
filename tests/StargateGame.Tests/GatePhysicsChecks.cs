using System.Numerics;
using System.Diagnostics;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.Building;

static class GatePhysicsChecks
{
    static BuildTriangle[] Box(Vector3 centre, Vector3 half)
    {
        var hull = PropGeometry.Build(half, false);
        return hull.Faces.Select(f => new BuildTriangle(centre + hull.Vertices[f.A], centre + hull.Vertices[f.B], centre + hull.Vertices[f.C])).ToArray();
    }
    public static async Task Persistence()
    {
        string path = Path.Combine(Path.GetTempPath(), "StargateHazard-" + Guid.NewGuid());
        Guid location, removed;
        WorldChanges committed;
        var definition = new StargateGame.WorldGen.PlanetPlan(7319, "temperate", settlements: new SettlementSettings("wilderness", "auto")).Definition;
        await using (var db = new StargateGame.Persistence.WorldRepository(path))
        {
            var saved = await db.CreateAsync(definition);
            location = saved.Id;
            removed = saved.Definition.Props[0].PersistentId;
            committed = BuildingRules.Destroy(saved.Changes, [], [new("tree", 123, default)]);
            await db.SaveDestructionAsync(committed, saved.Changes.Revision, [removed]);
            await db.SaveCurrentBodiesAsync(location, saved.Bodies, saved.Definition.Props);
            var now = await db.LoadAsync(location);
            Verify.Check(now.Changes.Revision == committed.Revision && now.Definition.Props.All(p => p.PersistentId != removed) && now.Changes.IsRemoved("tree", 123), "hazard transaction commits scenery and prop removal with one revision; stale save cannot resurrect");
            bool rejected = false;
            try
            {
                await db.SaveDestructionAsync(committed, saved.Changes.Revision, [saved.Definition.Props[1].PersistentId]);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Verify.Check(rejected && (await db.LoadAsync(location)).Definition.Props.Any(p => p.PersistentId == saved.Definition.Props[1].PersistentId), "stale hazard transaction rolls back without partially destroying another prop");
        }
        await using (var db = new StargateGame.Persistence.WorldRepository(path))
        {
            var saved = await db.LoadAsync(location);
            Verify.Check(saved.Changes.IsRemoved("tree", 123) && saved.Definition.Props.All(p => p.PersistentId != removed), "hazard removals survive database restart");
        }
        Console.WriteLine("HAZARD PERSISTENCE evidence=" + path);
    }
    public static void Run()
    {
        var frame = new GateFrame(Vector3.Zero, Quaternion.Identity, 2.3f);
        var effect = GateEffectTimeline.Evaluate(GatePhase.Opening, 2);
        var volume = new KawooshVolume(frame, effect);
        Verify.Check(volume.Contains(new(0, 0, 2)) && volume.Contains(new(0, 0, 4)), "liquid volume contains neck and head");
        Verify.Check(!volume.Contains(new(0, 0, -.1f)) && !volume.Intersects(Box(new(0, 0, -1), new(.3f))), "rear sheet and rear objects are not hazardous");
        Verify.Check(!volume.Intersects(Box(new(0, 0, -.5f), new(.5f))), "rear object touching only the flat sheet is immune");
        Verify.Check(volume.Intersects(Box(new(0, 0, 3), new(.1f))), "fully contained small object intersects plume");
        Verify.Check(volume.Intersects(Box(new(0, 0, 3), new(10))), "containing object intersects plume");
        Verify.Check(!volume.Intersects(Box(new(2.7f, 2.7f, 3), new(.1f))), "broad bounds alone do not destroy objects");
        var prop = PlanetDefinition.Default.Props[0] with
        {
            Id = 1,
            PersistentId = Guid.NewGuid(),
            Position = new(0, 1, 0)
        };
        var before = new BodyState(1, new(-4, 0, 3), Quaternion.Identity, new(480, 0, 0), default, false);
        var after = before with
        {
            Position = new(4, 0, 3)
        };
        Verify.Check(volume.IntersectsProp(prop, before, after), "swept fast prop crosses liquid between ticks");
        var rotated = frame with
        {
            Centre = new(12, 5, -7),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1.3f)
        };
        Verify.Check(new KawooshVolume(rotated, effect).Contains(rotated.World(new(0, 0, 3))), "rotated raised gate uses full frame");
        foreach (double age in new[] { 0, 1.2, 1.3, 1.5, 2.0, 2.8, 3.3, 4 })
        {
            var f = GateEffectTimeline.Evaluate(GatePhase.Opening, age);
            var rim = KawooshShape.Position(new(2.3f, 0), f);
            Verify.Check(Vector3.Distance(rim, new(2.3f, 0, 0)) < .001f, "plume collar stays on aperture at " + age);
        }
        var structure = Guid.NewGuid();
        var foundation = new BuildingPiece(Guid.NewGuid(), structure, PieceKind.SquareFoundation, default, 0, 0);
        var wall = new BuildingPiece(Guid.NewGuid(), structure, PieceKind.Wall, default, 0, 0);
        var changes = new WorldChanges(Guid.NewGuid(), Guid.NewGuid(), 0, [new(structure, Vector3.Zero, 0)], [foundation, wall], []);
        var destroyed = BuildingRules.Destroy(changes, [foundation.Id], []);
        Verify.Check(destroyed.Pieces.Length == 0 && destroyed.Structures.Length == 0, "destroyed foundation removes unsupported dependents and empty structure");
        var definition = PlanetDefinition.Default with
        {
            Props = [prop with { Position = new(0, 1.5f, 0) }],
            Entities = []
        };
        using (var world = new PhysicsWorld(definition))
        {
            for (int i = 0; i < 90; i++)
                world.Step();
            var state = world.Bodies()[0];
            float bottom = state.Position.Y - prop.HalfExtent.Y;
            world.BeginPropEntry(1, new(new(0, state.Position.Y, -.05f), Quaternion.Identity, 2.3f));
            Verify.Check(world.EntrySupports.Length == 1, "supported entry records private floor continuation");
            for (int i = 0; i < 90; i++)
                world.Step();
            Verify.Check(world.Bodies()[0].Position.Y - prop.HalfExtent.Y > bottom - .08f, "partially clipped crate keeps support for 1.5 seconds");
            PhysicsWorld.Validate(world.Snapshot(), definition);
            using var checkpoint = world.Save();
            world.Step();
            var expected = world.Snapshot();
            world.Restore(checkpoint);
            world.Step();
            Verify.Check(world.Bodies().SequenceEqual(expected.Bodies) && world.EntrySupports.SequenceEqual(expected.EntrySupports ?? []), "entry floor constraints replay deterministically");
        }
        using (var world = new PhysicsWorld(definition with
        {
            Props = [prop with { Position = new(0, 4, 0) }]
        }))
        {
            world.BeginPropEntry(1, new(new(0, 4, 0), Quaternion.Identity, 2.3f));
            Verify.Check(world.EntrySupports.Length == 0, "airborne insertion does not create invisible floor");
        }
        using (var exit = new PhysicsWorld(definition with
        {
            Props = []
        }))
        {
            var gate = new GateFrame(new(0, 3, 0), Quaternion.Identity, 2.3f);
            var pose = new BodyState(1, new(0, 1.8f, 1.2f), Quaternion.Identity, new(0, 0, 2), default, false);
            Verify.Check(exit.TryFindPropExit(gate, prop, pose, out var grounded) && grounded.Position.Y < pose.Position.Y && grounded.LinearVelocity == pose.LinearVelocity, "receiving placement lowers item onto solid ground");
            var high = pose with
            {
                Position = new(0, 8, 1.2f)
            };
            Verify.Check(exit.TryFindPropExit(gate, prop, high, out var ejected) && ejected.LinearVelocity.Z >= 6 && ejected.LinearVelocity.Y >= 1.5f, "unsupported clear exit ejects outward with bounded speed");
            exit.AddStaticBox(new(0, 8, 1.2f), new(5));
            Verify.Check(!exit.TryFindPropExit(gate, prop, high, out _), "fully blocked receiver never inserts inside solid geometry");
        }
        using (var floor = new PhysicsWorld(definition with
        {
            Props = []
        }))
        {
            floor.AddStaticBox(new(0, 2, 1.2f), new(4, .2f, 4));
            var desired = new BodyState(1, new(0, 2, 1.2f), Quaternion.Identity, default, default, false);
            Verify.Check(floor.TryFindPropExit(new(new(0, 3, 0), Quaternion.Identity, 2.3f), prop, desired, out var pose) && pose.Position.Y > 2.6f, "receiver lifts a penetrating full hull onto a valid building floor");
        }
        List<double> timings = [];
        for (int i = 0; i < 90; i++)
        {
            var sw = Stopwatch.StartNew();
            volume.Update(frame, GateEffectTimeline.Evaluate(GatePhase.Opening, 1.3 + i / 60.0));
            volume.Intersects(Box(new(0, 0, 3), new(.4f)));
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }
        var sorted = timings.Skip(10).Order().ToArray();
        Console.WriteLine($"HAZARD volume+query p95_ms={sorted[(int)(sorted.Length * .95)]:F4}");
        Console.WriteLine($"GATE PHYSICS {Verify.Count} CHECKS PASSED");
    }
}

