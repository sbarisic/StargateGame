using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

static class HybridGrass
{
    public const int SurfaceSize = 33;
    public static Vector2 Transition(int quality) => quality switch { 1 => new(6, 10), 2 => new(14, 18), _ => new(10, 14) };
    internal static float[] FootprintCoverage(GrassTile tile, IReadOnlyList<GrassTuft> tufts, CancellationToken token)
    {
        float[] coverage = new float[SurfaceSize * SurfaceSize];
        // Rasterize only cells touched by each footprint, instead of scanning
        // every tuft at all 1,089 samples. Keep exactly the same max-union field.
        foreach (var tuft in tufts)
        {
            token.ThrowIfCancellationRequested();
            float radius = tuft.Footprint + .24f;
            float cx = tuft.Root.X - tile.X * 8, cz = tuft.Root.Z - tile.Z * 8;
            int x0 = Math.Clamp((int)MathF.Ceiling((cx - radius) * 4), 0, 32), x1 = Math.Clamp((int)MathF.Floor((cx + radius) * 4), 0, 32);
            int z0 = Math.Clamp((int)MathF.Ceiling((cz - radius) * 4), 0, 32), z1 = Math.Clamp((int)MathF.Floor((cz + radius) * 4), 0, 32);
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
            {
                float dx = tile.X * 8 + x * .25f - tuft.Root.X, dz = tile.Z * 8 + z * .25f - tuft.Root.Z;
                int i = z * SurfaceSize + x;
                coverage[i] = Math.Max(coverage[i], Math.Clamp((radius - MathF.Sqrt(dx * dx + dz * dz)) * 5, 0, 1));
            }
        }
        return coverage;
    }
    public static Vector4[] Surface(GrassTile tile, TerrainChunkData chunk, PlanetDefinition planet,
        GroundCoverSettings settings, CoverExclusion[] exclusions, SurfaceMap? ecology, IReadOnlyList<GrassTuft> tufts, CancellationToken token)
    {
        Vector4[] values = new Vector4[SurfaceSize * SurfaceSize];
        var coverage = FootprintCoverage(tile, tufts, token);
        for (int z = 0; z < SurfaceSize; z++) for (int x = 0; x < SurfaceSize; x++)
        {
            token.ThrowIfCancellationRequested();
            float wx = tile.X * 8 + x * .25f, wz = tile.Z * 8 + z * .25f;
            int ix = Math.Clamp((int)MathF.Floor(wx) - chunk.Coordinate.X * 64, 0, 63), iz = Math.Clamp((int)MathF.Floor(wz) - chunk.Coordinate.Z * 64, 0, 63);
            float y = chunk.Sample(wx, wz);
            var p = new Vector3(wx, y, wz);
            float allowed = GroundCover.CoverageAt(p, chunk.Normal(ix, iz), chunk.Material(ix, iz), chunk, planet, exclusions);
            var environment = ecology?.Sample(wx, wz) ?? new Vector4(0, .25f, y, 1);
            // Keep continuous undergrowth between tuft footprints. Otherwise a
            // sparse candidate field punches bare holes into the volume renderer,
            // including at tile edges where neighbouring tufts are not in this list.
            // The selected images control eligibility, including on slopes and
            // in other climates; physical reservations remain separate.
            float materialCover = ecology?.GrassAt(wx, wz) ?? 1;
            float cover = Math.Max(coverage[z * SurfaceSize + x], .85f) * materialCover;
            float mask = allowed > 0 && environment.W > .5f ? cover * environment.W : 0;
            // Continuous world-space height replaces one short/tall choice per
            // eight-metre tile, which made habitat boundaries look like squares.
            float variation = SurfaceEcology.Noise(new Vector2(wx, wz) / 11, GroundCover.PlanetSalt(planet) + 79);
            float heightScale = float.Lerp(.34f, .82f + .18f * variation, 1 - SurfaceEcology.Smooth(.15f, .8f, environment.X));
            values[z * SurfaceSize + x] = new(y - .015f, mask, heightScale, allowed > 0 && environment.W > .5f ? 1 : 0);
        }
        return values;
    }
}

