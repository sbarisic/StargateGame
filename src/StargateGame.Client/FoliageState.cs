using System.Numerics;
using StargateGame.Shared;
namespace StargateGame.Client;

readonly record struct FoliageInstance(int Species, Vector3 Root, float Height, float Yaw, float Rank, float Shade, float WindPhase, Vector4 GroundMaterial = default, Vector3 GroundNormal = default);
static class FoliagePlacement
{
    public static FoliageInstance[] Build(GrassTile tile, TerrainChunkData chunk, PlanetDefinition planet, SurfaceMap surface, CoverExclusion[] exclusions, CancellationToken token, int grassCount = 8)
    {
        if (planet.Preset != "temperate")
            return [];
        uint salt = GroundCover.PlanetSalt(planet);
        List<FoliageInstance> result = [];
        float Unit(uint n) => (GroundCover.Hash(n) & 65535) / 65535f;
        for (int z = tile.Z * 8; z < tile.Z * 8 + 8; z++)
            for (int x = tile.X * 8; x < tile.X * 8 + 8; x++)
                for (int i = 0; i < 12; i++)
                {
                    token.ThrowIfCancellationRequested();
                    uint h = GroundCover.Hash(unchecked((uint)x * 73856093u ^ (uint)z * 19349663u ^ (uint)i * 83492791u) ^ salt ^ 9173u);
                    float wx = x + Unit(h + 1), wz = z + Unit(h + 2);
                    var env = surface.Sample(wx, wz);
                    if (env.W < .95f)
                        continue;
                    var p = new Vector3(wx, chunk.Sample(wx, wz) - .02f, wz);
                    int ix = x - chunk.Coordinate.X * 64, iz = z - chunk.Coordinate.Z * 64;
                    if (GroundCover.CoverageAt(p, chunk.Normal(ix, iz), chunk.Material(ix, iz), chunk, planet, exclusions) <= 0)
                        continue;
                    float patch = SurfaceEcology.Patch(new(wx, wz), salt & 65535), shade = env.X;
                    float density = float.Lerp(.85f, .12f, shade) * float.Lerp(.35f, 1, SurfaceEcology.Smooth(.18f, .65f, patch));
                    if (Unit(h + 3) > density)
                        continue;
                    int species;
                    if (shade > .35f && Unit(h + 8) < .55f)
                        species = 3 + (int)(Unit(h + 9) * 1.999f);
                    else if (patch > .64f && Unit(h + 8) < .12f)
                        species = (int)(Unit(h + 9) * 2.999f);
                    else
                        species = 5 + Math.Min(grassCount - 1, (int)(Unit(h + 9) * grassCount));
                    // Extra candidates fill close-range grass; retain the existing fern/bush population.
                    if (i >= 4 && species < 5)
                        continue;
                    bool flower=planet.Fixture is null && shade<.35f && patch>.55f && i<2 && Unit(h+13)<.14f;
                    bool wood=planet.Fixture is null && shade>.6f && i==0 && Unit(h+14)<.003f;
                    if(flower) species=5+grassCount; if(wood) species=6+grassCount;
                    float height = species < 3 ? .45f + Unit(h + 4) * .65f : species < 5 ? .35f + Unit(h + 4) * .5f : .24f + Unit(h + 4) * .38f;
                    if(flower)height=.28f+Unit(h+4)*.3f; if(wood)height=.7f+Unit(h+4)*.4f;
                    result.Add(new(species, p, height, Unit(h + 5) * MathF.Tau, Unit(h + 6), .87f + Unit(h + 7) * .22f, Unit(h + 10) * .6f, chunk.Material(ix, iz), chunk.Normal(ix, iz)));
                }
        return result.ToArray();
    }
}
