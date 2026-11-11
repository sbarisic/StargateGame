using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

static partial class VisualChecks
{
    static (Guid Location, string View, int Count, Vector3 Target) waterCamera;
    static void WaterCamera(Camera camera, ClientSession session, string view)
    {
        if (view is not ("water-shallow" or "water-deep" or "water-grazing" or "water-river" or "water-lake" or "water-underwater" or "water-half-screen"))
            throw new ArgumentException("Unknown water view. Use shallow, deep, grazing, river, lake, underwater or half-screen.");
        var location = session.World.Definition.LocationId;
        int count = session.Terrain?.Count ?? 0;
        if (waterCamera.Location != location || waterCamera.View != view || waterCamera.Count != count)
        {
            float best = float.NegativeInfinity;
            var target = session.World.Definition.Spawns[0];
            if (session.Terrain is { } terrain)
                foreach (var chunk in terrain.Chunks.OrderBy(c => c.Point(0, 0).X).ThenBy(c => c.Point(0, 0).Z))
                    for (int z = 2; z < 63; z += 4)
                        for (int x = 2; x < 63; x += 4)
                        {
                            var bed = chunk.Point(x, z);
                            float level = chunk.WaterHeight(x, z), depth = level - bed.Y;
                            if (!float.IsFinite(level) || depth < .10f || Vector3.DistanceSquared(bed, session.Local.Position) > 160 * 160) continue;
                            if (!WaterSurfaceData.TrySample(terrain, bed.X, bed.Z, out var water)) continue;
                            var point = new Vector3(bed.X, water.Height, bed.Z);
                            float flow = water.Flow.Length();
                            depth = water.Depth;
                            float score = view switch
                            {
                                "water-shallow" => -Math.Abs(depth - .5f),
                                "water-river" => Math.Min(depth, 2) + flow * 4,
                                "water-lake" => Math.Min(depth, 4) - flow * 8,
                                _ => Math.Min(depth, 5)
                            };
                            score -= Vector3.Distance(point, session.Local.Position) * .001f;
                            if (score <= best) continue;
                            best = score; target = point;
                        }
            waterCamera = (location, view, count, target);
        }
        var offset = view switch
        {
            "water-grazing" => new Vector3(0, .35f, 6),
            "water-shallow" => new Vector3(1, 1.8f, 2.5f),
            "water-underwater" => new Vector3(0, -1, 0),
            "water-half-screen" => new Vector3(0, Movement.EyeHeight, 5),
            _ => new Vector3(3, 3.5f, 6)
        };
        camera.Position = waterCamera.Target + offset;
        camera.LookAt(waterCamera.Target + (view == "water-underwater" ? new Vector3(0, -.25f, -5) : Vector3.Zero));
    }
}
