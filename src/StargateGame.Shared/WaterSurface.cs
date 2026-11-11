using System.Numerics;
using System.Runtime.CompilerServices;
namespace StargateGame.Shared;

public readonly record struct WaterSample(float Height, float Depth, Vector2 Flow, int BodyId = 1);
public readonly record struct WaterTriangle(Vector3 A, Vector3 B, Vector3 C, Vector2 Flow, int BodyId = 1);

/// <summary>Canonical clipped water geometry, shared by drawing and authoritative queries.</summary>
public sealed class WaterSurfaceData
{
    static readonly ConditionalWeakTable<TerrainChunkData, WaterSurfaceData> cache = new();
    readonly List<WaterTriangle>[] cells = new List<WaterTriangle>[64 * 64];
    public WaterTriangle[] Triangles
    {
        get;
    }
    public static WaterSurfaceData For(TerrainChunkData chunk) => cache.GetValue(chunk, c => new(c));
    WaterSurfaceData(TerrainChunkData chunk)
    {
        // A wet sample cannot hold a higher surface than an adjacent dry bank.
        // Lower only the water, never the canonical terrain. In particular, do
        // not drape a full-depth river over the outside of a steep bank.
        float Level(int x, int z)
        {
            float h = chunk.WaterHeight(x, z);
            if (!float.IsFinite(h))
                return h;
            // Standing water stays level right up to its terrain intersection.
            if (chunk.WaterType(x, z) is WaterKind.Lake or WaterKind.Ocean)
                return h;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!float.IsFinite(chunk.WaterHeight(x + dx, z + dz)))
                        h = Math.Min(h, chunk.Height(x + dx, z + dz));
            return Math.Max(chunk.Height(x, z), h);
        }
        float[,] levelsAt = new float[65, 65];
        for (int z = 0; z <= 64; z++)
            for (int x = 0; x <= 64; x++)
                levelsAt[x, z] = Level(x, z);
        List<WaterTriangle> all = [];
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                var list = cells[z * 64 + x] = [];
                Add((x, z), (x, z + 1), (x + 1, z), list);
                Add((x + 1, z), (x, z + 1), (x + 1, z + 1), list);
                all.AddRange(list);
            }
        Triangles = all.ToArray();
        void Add((int X, int Z) a, (int X, int Z) b, (int X, int Z) c, List<WaterTriangle> output)
        {
            var points = new[] { a, b, c };
            var levels = points.Select(p => levelsAt[p.X, p.Z]).ToArray();
            int body = points.Select(p => chunk.WaterBody(p.X, p.Z)).FirstOrDefault(id => id > 0);
            var wet = levels.Where(float.IsFinite).ToArray();
            if (wet.Length == 0)
                return;
            List<(Vector3 P, float Depth)> polygon = [];
            Vector3[] flowPoints = new Vector3[3];
            float Extrapolate(int x, int z)
            {
                float sum = 0;
                int count = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        float value = chunk.WaterHeight(x + dx, z + dz);
                        if (float.IsFinite(value) && chunk.WaterBody(x + dx, z + dz) == body)
                        {
                            sum += value;
                            count++;
                        }
                    }
                return count > 0 ? sum / count : wet.Average();
            }
            for (int i = 0; i < 3; i++)
            {
                var p = points[i];
                float bed = chunk.Height(p.X, p.Z), h = float.IsFinite(levels[i]) ? levels[i] : Extrapolate(p.X, p.Z);
                flowPoints[i] = new(p.X, float.IsFinite(chunk.WaterHeight(p.X, p.Z)) ? chunk.WaterHeight(p.X, p.Z) : h, p.Z);
                // Intentional dry exclusions close against the terrain too. Dropping
                // the entire triangle leaves a raised, exposed edge at the last wet row.
                if (!float.IsFinite(levels[i]))
                    h = Math.Min(h, bed - .001f);
                polygon.Add((new(chunk.Coordinate.X * 64 + p.X, h, chunk.Coordinate.Z * 64 + p.Z), h - bed));
            }
            if (polygon.All(p => p.Depth <= 0))
                return;
            // Current follows the mean water plane, not the final taper to a dry bank.
            var flowNormal = Vector3.Cross(flowPoints[1] - flowPoints[0], flowPoints[2] - flowPoints[0]);
            Vector2 flow = new(flowNormal.X / flowNormal.Y, flowNormal.Z / flowNormal.Y);
            float speed = Math.Min(1, flow.Length() * 8);
            flow = flow.LengthSquared() > 1e-8f ? Vector2.Normalize(flow) * speed : Vector2.Zero;
            List<(Vector3 P, float Depth)> clipped = [];
            for (int i = 0; i < 3; i++)
            {
                var p = polygon[i];
                var q = polygon[(i + 1) % 3];
                if (p.Depth >= 0)
                    clipped.Add(p);
                if ((p.Depth >= 0) != (q.Depth >= 0))
                    clipped.Add((Vector3.Lerp(p.P, q.P, p.Depth / (p.Depth - q.Depth)), 0));
            }
            for (int i = 1; i + 1 < clipped.Count; i++)
            {
                var p = clipped[0].P;
                var q = clipped[i].P;
                var r = clipped[i + 1].P;
                var n = Vector3.Cross(q - p, r - p);
                if (Math.Abs(n.Y) < 1e-7f)
                    continue;
                output.Add(new(p, q, r, flow, body));
            }
        }
    }
    public bool TrySample(float x, float z, out WaterSample sample)
    {
        sample = default;
        int cx = (int)MathF.Floor(x), cz = (int)MathF.Floor(z);
        cx = ((cx % 64) + 64) % 64;
        cz = ((cz % 64) + 64) % 64;
        foreach (var t in cells[cz * 64 + cx])
        {
            Vector2 p = new(x - t.A.X, z - t.A.Z), u = new(t.B.X - t.A.X, t.B.Z - t.A.Z), v = new(t.C.X - t.A.X, t.C.Z - t.A.Z);
            float det = u.X * v.Y - u.Y * v.X, b = (p.X * v.Y - p.Y * v.X) / det, c = (u.X * p.Y - u.Y * p.X) / det;
            if (b < -.0001f || c < -.0001f || b + c > 1.0001f)
                continue;
            sample = new(t.A.Y + b * (t.B.Y - t.A.Y) + c * (t.C.Y - t.A.Y), 0, t.Flow, t.BodyId);
            return true;
        }
        return false;
    }
    public static bool TrySample(ITerrainSource? terrain, float x, float z, out WaterSample sample)
    {
        sample = default;
        if (terrain is null || !terrain.TryGetChunk(ChunkCoordinate.At(x, z), out var chunk) || !For(chunk).TrySample(x, z, out sample))
            return false;
        sample = sample with
        {
            Depth = Math.Max(0, sample.Height - terrain.SampleHeight(x, z))
        };
        return sample.Depth > 0;
    }
}
