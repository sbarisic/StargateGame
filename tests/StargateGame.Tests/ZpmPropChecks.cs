using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;
using StargateGame.Physics;
using StargateGame.WorldGen;

static class ZpmPropChecks
{
    public static void Run()
    {
        var definition = GraphicsFixture.Create("gate-pair");
        var terrain = new TerrainStore(definition.SettingsHash);
        foreach (var c in GraphicsFixture.Chunks) terrain.Put(GraphicsFixture.BuildChunk(definition, c), false);
        using var world = new PhysicsWorld(definition, true, terrain, GraphicsFixture.Chunks);
        foreach (var original in new[] { PropDefinition.ZpmHub(3, new(-12, 2, 9)), PropDefinition.Zpm(4, new(-9, 2, 9)) })
        {
            var prop = original with { PersistentId = Guid.NewGuid() };
            Verify.Check(prop.Valid && !(prop with { Mass = 0 }).Valid && !(prop with { HalfExtent = Vector3.One }).Valid,
                prop.Name + " validates the authoritative dimensions and mass");
            var roundtrip = JsonSerializer.Deserialize<PropDefinition>(JsonSerializer.Serialize(prop, Protocol.Json), Protocol.Json)!;
            Verify.Check(roundtrip == prop && roundtrip.Model == prop.Model, prop.Name + " registry serializes without losing identity or kind");
            var hull = PropGeometry.For(prop);
            Verify.Check(hull.Vertices.All(v => Vector3.Abs(v).X <= prop.HalfExtent.X+.00001f && Vector3.Abs(v).Y <= prop.HalfExtent.Y+.00001f && Vector3.Abs(v).Z <= prop.HalfExtent.Z+.00001f), prop.Name + " collision fits the model envelope");
            var state = new BodyState(prop.Id, prop.Position, Quaternion.Identity, Vector3.Zero, Vector3.Zero, false);
            Verify.Check(world.CanPlaceProp(prop, state), prop.Name + " fits at the clear spawn position");
            world.InsertProp(prop, state);
            Verify.Check(PropGeometry.RayHit(prop.Position + Vector3.UnitZ*3, -Vector3.UnitZ, state, prop, 4), prop.Name + " is pickable through shared convex geometry");
        }
        for (int tick = 0; tick < 240; tick++) world.Step();
        foreach (var id in new[] { 3, 4 })
        {
            var prop = world.World.Definition.Props.Single(p => p.Id == id);
            var body = world.Bodies().Single(b => b.Id == id);
            Verify.Check(body.Position.Y > 0 && body.Position.Y < prop.HalfExtent.Y+.1f && body.LinearVelocity.Length() < .1f,
                prop.Name + " falls and rests on terrain with the authored body origin");
        }
        var snapshot = world.Snapshot();
        using var replica = new PhysicsWorld(definition, false, terrain, GraphicsFixture.Chunks);
        replica.Apply(snapshot);
        Verify.Check(replica.World.Definition.Props.Where(p => p.Id >= 3).Select(p => p.Kind).SequenceEqual(new[] { PropKind.ZpmHub, PropKind.Zpm }),
            "snapshot replication creates both ZPM physics bodies");
    }
}
