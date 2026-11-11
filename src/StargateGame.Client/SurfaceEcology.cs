using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

readonly record struct SurfaceCanopy(Vector2 Centre, Vector2 Radius);
sealed record SurfaceMap(ChunkCoordinate Coordinate, Vector4[] Values, float[]? Grass = null)
{
    public float GrassAt(float x, float z)
    {
        if (Grass is null) return 1;
        float fx = Math.Clamp(x - Coordinate.X * 64 + .5f, 0, 65.999f), fz = Math.Clamp(z - Coordinate.Z * 64 + .5f, 0, 65.999f);
        int ix = (int)fx, iz = (int)fz;
        return float.Lerp(float.Lerp(Grass[iz * 67 + ix], Grass[iz * 67 + ix + 1], fx - ix),
            float.Lerp(Grass[(iz + 1) * 67 + ix], Grass[(iz + 1) * 67 + ix + 1], fx - ix), fz - iz);
    }
    public Vector4 Sample(float x, float z)
    {
        float fx = Math.Clamp(x - Coordinate.X * 64 + .5f, 0, 65.999f), fz = Math.Clamp(z - Coordinate.Z * 64 + .5f, 0, 65.999f);
        int ix = (int)fx, iz = (int)fz;
        return Vector4.Lerp(Vector4.Lerp(Values[iz * 67 + ix], Values[iz * 67 + ix + 1], fx - ix), Vector4.Lerp(Values[(iz + 1) * 67 + ix], Values[(iz + 1) * 67 + ix + 1], fx - ix), fz - iz);
    }
    public Vector4[] Pixels() => Enumerable.Range(0, 1024).Select(i => Sample(Coordinate.X*64+(i%32)*2+1,Coordinate.Z*64+(i/32)*2+1)).ToArray();
}
static class SurfaceEcology
{
    public static float MeadowCoverage(Vector4 environment, float patch, float strength = .85f)
        => environment.W * float.Lerp(1, .32f, environment.X) * Math.Clamp(.65f + environment.Y * .55f, 0, 1)
            * float.Lerp(1, Smooth(.22f, .62f, patch), strength);
    public static float Smooth(float a, float b, float v)
    {
        float t = Math.Clamp((v - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
    public static float Noise(Vector2 p, uint salt)
    {
        int x = (int)MathF.Floor(p.X), z = (int)MathF.Floor(p.Y);
        float u = Smooth(0, 1, p.X - x), v = Smooth(0, 1, p.Y - z);
        float H(int a, int b) => (GroundCover.Hash(unchecked((uint)a * 73856093u ^ (uint)b * 19349663u) ^ salt) & 65535) / 65535f;
        return float.Lerp(float.Lerp(H(x, z), H(x + 1, z), u), float.Lerp(H(x, z + 1), H(x + 1, z + 1), u), v);
    }
    public static float Patch(Vector2 p, uint salt) => Noise(p / 18, salt) * .7f + Noise(p / 2.6f, salt + 19) * .3f;
    public static SurfaceMap Build(TerrainChunkData owner, IReadOnlyDictionary<ChunkCoordinate, TerrainChunkData> chunks, SurfaceCanopy[] canopies, CoverExclusion[] obstacles, CancellationToken token, PlanetDefinition? planet = null, float[]? grassLayers = null)
    {
        Vector4[] data = new Vector4[67 * 67];
        float[]? grass = grassLayers is null ? null : new float[data.Length];
        uint seed = planet is null ? 0 : GroundCover.PlanetSalt(planet) & 65535;
        TerrainChunkData Chunk(float x, float z) => chunks.GetValueOrDefault(ChunkCoordinate.At(x, z), owner);
        bool Water(float x, float z)
        {
            var c = Chunk(x, z);
            return float.IsFinite(c.WaterHeight(Math.Clamp((int)MathF.Floor(x) - c.Coordinate.X * 64, 0, 64), Math.Clamp((int)MathF.Floor(z) - c.Coordinate.Z * 64, 0, 64)));
        }
        for (int z = -1; z <= 65; z++)
            for (int x = -1; x <= 65; x++)
            {
                token.ThrowIfCancellationRequested();
                float wx = owner.Coordinate.X * 64 + x + .5f, wz = owner.Coordinate.Z * 64 + z + .5f;
                var c = Chunk(wx, wz);
                int ix = Math.Clamp((int)MathF.Floor(wx) - c.Coordinate.X * 64, 0, 63), iz = Math.Clamp((int)MathF.Floor(wz) - c.Coordinate.Z * 64, 0, 63);
                var p = new Vector3(wx, c.Sample(wx, wz), wz);
                bool water = Water(wx, wz);
                var habitat = c.Landscape(ix,iz);
                float wet = water ? 1 : Math.Max(c.Material(ix, iz).Z * .18f, habitat.Moisture*.55f), cover = habitat.Forest;
                foreach (int r in new[] { 2, 5, 9 })
                    for (int a = 0; a < 8; a++)
                        if (Water(wx + MathF.Cos(a * MathF.PI / 4) * r, wz + MathF.Sin(a * MathF.PI / 4) * r))
                            wet = Math.Max(wet, 1 - r / 12f);
                foreach (var tree in canopies)
                {
                    float d = ((new Vector2(wx, wz) - tree.Centre) / tree.Radius).Length();
                    cover = Math.Max(cover, 1 - Smooth(.25f, 1, d));
                }
                bool reserved = planet is not null && (planet.Spawns.Any(s => Vector2.DistanceSquared(new(s.X, s.Z), new(wx, wz)) < 2.25f) || planet.Entities.Any(e =>
                    e.Model == "stargate" && Math.Abs(wx - e.Position.X) < 3.4f && wz - e.Position.Z > -1.5f && wz - e.Position.Z < 19 ||
                    e.Model == "dhd" && Vector2.DistanceSquared(new(e.Position.X, e.Position.Z), new(wx, wz)) < 1.69f));
                bool roadPath = planet?.Settlements?.Roads.Any(lane =>
                {
                    var a = new Vector2(lane.A.X,lane.A.Z);
                    var delta = new Vector2(lane.B.X,lane.B.Z)-a;
                    var point = new Vector2(wx,wz);
                    float t = Math.Clamp(Vector2.Dot(point-a,delta)/Math.Max(delta.LengthSquared(),.001f),0,1);
                    return Vector2.Distance(point,a+delta*t) < lane.Width*.5f+(planet.Fixture == "gate-pair" ? .245f : .35f);
                }) == true;
                bool forestPath = planet is not null && GraphicsFixture.Forest(planet) && GraphicsFixture.ForestPathDistance(wx, wz) < 1.5f;
                float available = water || reserved || forestPath || roadPath || GroundCover.TerrainCoverage(c.Normal(ix, iz), c.Material(ix, iz)) <= 0 || obstacles.Any(o => o.Contains(p)) ? 0 : 1;
                int index = (z + 1) * 67 + x + 1;
                data[index] = new(cover, wet, p.Y, available);
                if (grass is not null)
                    grass[index] = TerrainGrass.Coverage(planet?.Preset ?? "temperate", new(wx, wz), c.Normal(ix, iz), c.Material(ix, iz), data[index],
                        new(habitat.Habitat == Habitat.Beach ? -1 : habitat.Forest, habitat.Sediment), seed, grassLayers!);
            }
        return new(owner.Coordinate, data, grass);
    }
}
