using System.Numerics;
using StargateGame.Shared;
using StargateGame.WorldGen;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    bool SwimVelocity(int id, Vector3 position, Vector3 current, PlayerInput input, out Vector3 velocity)
    {
        velocity = default;
        bool wet = WaterSurfaceData.TrySample(terrainSource, position.X, position.Z, out var water);
        float fraction = wet ? Math.Clamp((water.Height - position.Y) / Movement.BodyHeight(players[id]), 0, 1) : 0;
        bool swimming = wet && water.Depth > 1.05f && fraction > (players[id].Swimming ? .48f : .62f);
        players[id] = players[id] with
        {
            Swimming = swimming,
            Submersion = fraction
        };
        if (!swimming)
            return false;
        velocity = Movement.DesiredVelocity(input) * ((input.Walk ? 2 : 3) / (input.Walk ? Movement.WalkSpeed : Movement.RunSpeed));
        velocity += new Vector3(water.Flow.X, 0, water.Flow.Y);
        float vertical = input.SwimRise == input.SwimDive ? Math.Clamp((water.Height - Movement.BodyHeight(players[id]) * .78f - position.Y) * 2, -.4f, .65f) : input.SwimRise ? 2 : -2;
        velocity.Y = current.Y + (vertical - current.Y) * (1 - MathF.Exp(-5 * Movement.Dt));
        return true;
    }
    readonly Dictionary<int, (Vector3 Position, Quaternion Rotation, long Revision, float Level, Vector2 Flow, int Count)> sleepingWater = [];
    public int SleepingWaterCacheHits
    {
        get;
        private set;
    }
    void ApplyWaterBuoyancy()
    {
        var api = system.BodyInterface;
        foreach (var definition in World.Definition.Props)
        {
            if (!props.TryGetValue(definition.Id, out var body))
                continue;
            var position = api.GetPosition(body);
            var rotation = api.GetRotation(body);
            // Sample the collider footprint, not empty corners around a round barrel.
            float level = 0;
            Vector2 flow = default;
            int count = 0;
            long waterRevision = terrainSource is TerrainStore store ? store.Revision : -1;
            bool reuse = !propClips.ContainsKey(definition.Id) && !api.IsActive(body) && waterRevision >= 0 && sleepingWater.TryGetValue(definition.Id, out var cached) && cached.Position == position && cached.Rotation == rotation && cached.Revision == waterRevision;
            if (reuse)
            {
                var prior = sleepingWater[definition.Id];
                level = prior.Level;
                flow = prior.Flow;
                count = prior.Count;
                SleepingWaterCacheHits++;
            }
            else
                for (int i = 0; i < 9; i++)
                {
                    float angle = (i - 1) * MathF.Tau / 8;
                    var local = definition.IsBarrel ? (i == 0 ? Vector3.Zero : new Vector3(MathF.Cos(angle) * definition.HalfExtent.X, 0, MathF.Sin(angle) * definition.HalfExtent.Z)) :
                        new Vector3((i % 3 - 1) * definition.HalfExtent.X, 0, (i / 3 - 1) * definition.HalfExtent.Z);
                    var point = position + Vector3.Transform(local, rotation);
                    if (propClips.TryGetValue(definition.Id, out var gate) && gate.Distance(point) < 0)
                        continue;
                    if (WaterSurfaceData.TrySample(terrainSource, point.X, point.Z, out var sample))
                    {
                        level += sample.Height;
                        flow += sample.Flow;
                        count++;
                    }
                }
            if (!reuse)
            {
                if (count > 0)
                {
                    level /= count;
                    flow /= count;
                }
                sleepingWater[definition.Id] = (position, rotation, waterRevision, level, flow, count);
            }
            if (count == 0)
                continue;
            // Jolt clips the actual oriented collision shape against this local fluid plane,
            // applies force at its submerged centre, and bounds the buoyancy to 1.6 g.
            if (position.Y - definition.HalfExtent.Length() > level)
                continue;
            if (!api.IsActive(body))
            {
                var extent = definition.HalfExtent;
                float vertical = Math.Abs(Vector3.Transform(new Vector3(extent.X, 0, 0), rotation).Y) + Math.Abs(Vector3.Transform(new Vector3(0, extent.Y, 0), rotation).Y) + Math.Abs(Vector3.Transform(new Vector3(0, 0, extent.Z), rotation).Y);
                if (definition.IsBarrel)
                    vertical = PropGeometry.For(definition).Vertices.Max(v => Math.Abs(Vector3.Transform(v, rotation).Y));
                float immersed = Math.Clamp((level - position.Y + vertical) / (2 * vertical), 0, 1);
                if (Math.Abs(immersed * 1.6f * count / 9f - 1) > .08f || flow.LengthSquared() > .0001f)
                    api.ActivateBody(body);
            }
            Native.Buoyancy(api.Handle, body, new Vector3(position.X, level, position.Z), Vector3.UnitY, 1.6f * count / 9f, .7f, .08f, new Vector3(flow.X, 0, flow.Y), new Vector3(0, -Movement.Gravity, 0), Movement.Dt);
        }
    }
}
