using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

public sealed record TerrainMeshData(Vector3[] Positions, Vector3[] Normals, Vector4[] Materials, Vector3[] WaterPositions)
{
    public Vector2[] Ecology { get; init; } = [];
    public Vector4[] Landscape { get; init; } = [];
    public float[] WaterDepth { get; init; } = [];
    public Vector2[] WaterFlow
    {
        get;
        init;
    } = [];
}

public static class TerrainMesher
{
    /// <summary>Water's normal attribute stores canonical bed depth in X.
    /// Reconstruct from retained metre-grid bed vertices, including decoded far
    /// tiles, so no extra network field or per-fragment terrain lookup is needed.</summary>
    public static Vector3[] WaterAttributes(TerrainMeshData mesh)
    {
        if (mesh.WaterPositions.Length == 0)
            return [];
        if (mesh.WaterDepth.Length == mesh.WaterPositions.Length)
            return mesh.WaterDepth.Select(d => new Vector3(d,1,0)).ToArray();
        var bed = new Dictionary<Vector2, float>();
        foreach (var p in mesh.Positions)
            bed.TryAdd(new(p.X, p.Z), p.Y);
        float maxX = mesh.Positions.Max(p => p.X), maxZ = mesh.Positions.Max(p => p.Z);
        return mesh.WaterPositions.Select(p =>
        {
            float x = Math.Min(MathF.Floor(p.X), maxX - 1), z = Math.Min(MathF.Floor(p.Z), maxZ - 1), u = p.X - x, v = p.Z - z;
            float a = bed[new(x, z)], b = bed[new(x + 1, z)], c = bed[new(x, z + 1)], d = bed[new(x + 1, z + 1)];
            float h = u + v <= 1 ? a + (b - a) * u + (c - a) * v : d + (c - d) * (1 - u) + (b - d) * (1 - v);
            return new Vector3(Math.Max(0, p.Y - h), 1, 0);
        }).ToArray();
    }
    public static TerrainMeshData Build(TerrainChunkData data, int step)
    {
        if (step is not (1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(step));
        List<Vector3> positions = [], normals = [], water = [];
        List<Vector4> materials = [], landscape = []; List<Vector2> ecology=[];
        void Vertex(int x, int z)
        {
            positions.Add(data.Point(x, z));
            normals.Add(data.Normal(x, z));
            materials.Add(data.Material(x, z));
            var e=data.Landscape(x,z); ecology.Add(new(e.Habitat==Habitat.Beach?-1:e.Forest,e.Sediment));
            landscape.Add(new(data.Drainage(x,z),e.Sediment,(float)e.Geology,(float)e.Habitat));
        }
        void Triangle((int X, int Z) a, (int X, int Z) b, (int X, int Z) c)
        {
            Vertex(a.X, a.Z);
            Vertex(b.X, b.Z);
            Vertex(c.X, c.Z);
        }
        int blocks = 64 / step;
        bool[,] refined = new bool[blocks, blocks];
        for (int bz = 0; bz < blocks; bz++)
            for (int bx = 0; bx < blocks; bx++)
                for (int dz = -1; dz <= step + 1; dz++)
                    for (int dx = -1; dx <= step + 1; dx++)
                        if (data.GradedPlot(Math.Clamp(bx * step + dx, -1, 65), Math.Clamp(bz * step + dz, -1, 65)) || data.Road(Math.Clamp(bx * step + dx, -1, 65), Math.Clamp(bz * step + dz, -1, 65)) > 0 || float.IsFinite(data.WaterHeight(Math.Clamp(bx * step + dx, -1, 65), Math.Clamp(bz * step + dz, -1, 65))))
                            refined[bx, bz] = true;
        for (int z = 0; z < 64; z += step)
            for (int x = 0; x < 64; x += step)
            {
                int bx = x / step, bz = z / step;
                if (refined[bx, bz])
                {
                    for (int dz = 0; dz < step; dz++)
                        for (int dx = 0; dx < step; dx++)
                        {
                            Triangle((x + dx, z + dz), (x + dx, z + dz + 1), (x + dx + 1, z + dz));
                            Triangle((x + dx + 1, z + dz), (x + dx, z + dz + 1), (x + dx + 1, z + dz + 1));
                        }
                    continue;
                }
                bool left = x == 0 || refined[bx - 1, bz], right = x + step == 64 || refined[bx + 1, bz], bottom = z == 0 || refined[bx, bz - 1], top = z + step == 64 || refined[bx, bz + 1];
                if (step == 1 || !left && !right && !bottom && !top)
                {
                    Triangle((x, z), (x, z + step), (x + step, z));
                    Triangle((x + step, z), (x, z + step), (x + step, z + step));
                    continue;
                }
                // Every outer edge retains canonical metre samples, so neighboring
                // LODs share the same polyline rather than hiding cracks with skirts.
                List<(int X, int Z)> ring = [];
                for (int dz = 0; dz < step; dz += (left ? 1 : step))
                    ring.Add((x, z + dz));
                for (int dx = 0; dx < step; dx += (top ? 1 : step))
                    ring.Add((x + dx, z + step));
                for (int dz = step; dz > 0; dz -= (right ? 1 : step))
                    ring.Add((x + step, z + dz));
                for (int dx = step; dx > 0; dx -= (bottom ? 1 : step))
                    ring.Add((x + dx, z));
                for (int i = 0; i < ring.Count; i++)
                    Triangle((x + step / 2, z + step / 2), ring[i], ring[(i + 1) % ring.Count]);
            }
        var flows = new List<Vector2>();
        var waterDepth = new List<float>();
        var nodeFlow = new Vector2[65, 65];
        for (int z = 0; z <= 64; z++)
            for (int x = 0; x <= 64; x++)
            {
                float h = data.WaterHeight(x, z);
                if (!float.IsFinite(h))
                    continue;
                float H(int dx, int dz)
                {
                    float n = data.WaterHeight(x + dx, z + dz);
                    return float.IsFinite(n) && data.WaterBody(x + dx, z + dz) == data.WaterBody(x, z) ? n : h;
                }
                var f = new Vector2(H(-1, 0) - H(1, 0), H(0, -1) - H(0, 1)) * .5f;
                nodeFlow[x, z] = f.LengthSquared() > 1e-8f ? Vector2.Normalize(f) * Math.Min(1, f.Length() * 8) : Vector2.Zero;
            }
        void WaterVertex(Vector3 p)
        {
            float x = Math.Clamp(p.X - data.Coordinate.X * 64, 0, 64), z = Math.Clamp(p.Z - data.Coordinate.Z * 64, 0, 64);
            int ix = Math.Min(63, (int)x), iz = Math.Min(63, (int)z);
            var f = Vector2.Lerp(Vector2.Lerp(nodeFlow[ix, iz], nodeFlow[ix + 1, iz], x - ix), Vector2.Lerp(nodeFlow[ix, iz + 1], nodeFlow[ix + 1, iz + 1], x - ix), z - iz);
            water.Add(p);
            waterDepth.Add(Math.Max(0,p.Y-data.Sample(p.X,p.Z)));
            flows.Add(f);
        }
        foreach (var t in WaterSurfaceData.For(data).Triangles)
        {
            WaterVertex(t.A);
            WaterVertex(t.B);
            WaterVertex(t.C);
        }
        return new(positions.ToArray(), normals.ToArray(), materials.ToArray(), water.ToArray())
        {
            WaterFlow = flows.ToArray(), WaterDepth=waterDepth.ToArray(), Ecology=ecology.ToArray(), Landscape=landscape.ToArray()
        };
    }
}
