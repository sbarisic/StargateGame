using System.Numerics;
using StargateGame.Building;
using StargateGame.Persistence;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class BuildingDemo
{
    public static async Task Create(string directory, bool large)
    {
        using var gen = new GenerationService(7319, "temperate");
        var plan = await gen.Plan;
        Vector3 origin = default;
        float best = float.PositiveInfinity;
        for (int z = (int)plan.Gate.Z - 90; z < (int)plan.Gate.Z + 90; z += 6)
            for (int x = (int)plan.Gate.X - 90; x < (int)plan.Gate.X + 90; x += 6)
            {
                float distance = Vector2.Distance(new(x, z), new(plan.Gate.X, plan.Gate.Z));
                if (distance < 40 || distance > 90)
                    continue;
                var samples = Enumerable.Range(0, 10).SelectMany(iz => Enumerable.Range(0, 10).Select(ix => plan.Sample(x + ix, z + iz))).ToArray();
                if (samples.Any(s => s.Water != int.MinValue))
                    continue;
                float relief = (samples.Max(s => s.Height) - samples.Min(s => s.Height)) / 1000f;
                if (relief > 1.4f)
                    continue;
                float score = relief + distance * .002f;
                if (score < best)
                {
                    best = score;
                    origin = new(x, MathF.Ceiling((samples.Max(s => s.Height) / 1000f + .25f) * 4) / 4, z);
                }
            }
        if (!float.IsFinite(best))
            throw new Exception("No demo site found.");
        List<StructureRecord> structures = [];
        List<BuildingPiece> pieces = [];
        for (int repeat = 0; repeat < (large ? 25 : 1); repeat++)
        {
            var position = origin + new Vector3((repeat % 5) * 12, 0, (repeat / 5) * 12);
            var s = new StructureRecord(Guid.NewGuid(), position, 0);
            structures.Add(s);
            for (int z = 0; z < 3; z++)
                for (int x = 0; x < 3; x++)
                {
                    var p = new BuildPoint(x * 2, 0, z * 2, 0);
                    var c = position + p.Position;
                    float bottom = new[] { plan.Sample((int)c.X, (int)c.Z).Height, plan.Sample((int)c.X + 3, (int)c.Z).Height, plan.Sample((int)c.X, (int)c.Z + 3).Height, plan.Sample((int)c.X + 3, (int)c.Z + 3).Height }.Min() / 1000f - .15f;
                    var f = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.SquareFoundation, p, 0, 0, false, bottom);
                    pieces.Add(f);
                    foreach (int edge in Enumerable.Range(0, 4).Where(i => i == 0 && z == 0 || i == 1 && x == 2 || i == 2 && z == 2 || i == 3 && x == 0))
                    {
                        var e = BuildingGeometry.Edge(f, edge);
                        var k = x == 1 && z == 0 && edge == 0 ? PieceKind.Doorway : PieceKind.Window;
                        pieces.Add(new(Guid.NewGuid(), s.Id, k, e.A, e.Rotation, 0));
                        if (k == PieceKind.Doorway)
                            pieces.Add(new(Guid.NewGuid(), s.Id, PieceKind.Door, e.A, e.Rotation, 0, true));
                    }
                    if (x != 0 || z != 0)
                        pieces.Add(new(Guid.NewGuid(), s.Id, PieceKind.SquareFloor, p, 0, 1));
                    else
                        pieces.Add(new(Guid.NewGuid(), s.Id, PieceKind.Stairs, p, 0, 0));
                }
            var edgeTile = pieces.First(p => p.StructureId == s.Id && p.Kind == PieceKind.SquareFoundation && p.Point == new BuildPoint(4, 0, 2, 0));
            var e1 = BuildingGeometry.Edge(edgeTile, 1);
            var triangle = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.TriangleFoundation, e1.B, BuildingGeometry.Mod(e1.Rotation + 6), 0, false, origin.Y - 1.2f);
            pieces.Add(triangle);
            for (int i = 1; i < 3; i++)
            {
                var e = BuildingGeometry.Edge(triangle, i);
                pieces.Add(new(Guid.NewGuid(), s.Id, PieceKind.Window, e.A, e.Rotation, 0));
            }
            pieces.Add(triangle with
            {
                Id = Guid.NewGuid(),
                Kind = PieceKind.TriangleFloor,
                Level = 1
            });
            var tower = new BuildingPiece(Guid.NewGuid(), s.Id, PieceKind.SquareFloor, new(2, 0, 2, 0), 0, 1);
            for (int i = 0; i < 4; i++)
            {
                var e = BuildingGeometry.Edge(tower, i);
                pieces.Add(new(Guid.NewGuid(), s.Id, i % 2 == 0 ? PieceKind.Window : PieceKind.Wall, e.A, e.Rotation, 1));
            }
            pieces.Add(tower with
            {
                Id = Guid.NewGuid(),
                Level = 2
            });
        }
        var tombstones = new List<SceneryRemoval>();
        foreach (var c in ChunkCoordinate.At(origin.X, origin.Z).Around(2))
        {
            var chunk = await gen.RequestAsync(c);
            foreach (var candidate in chunk.Placements.Where(p => p.Position.X > origin.X - 8 && p.Position.X < origin.X + (large ? 68 : 18) && p.Position.Z > origin.Z - 8 && p.Position.Z < origin.Z + (large ? 68 : 18)))
                tombstones.Add(new(candidate.Category, candidate.Id, c));
        }
        await using var db = new WorldRepository(directory);
        var saved = await db.ResolveAsync(plan.Definition);
        await db.SaveAsync(new(saved.Changes.UniverseId, saved.Id, saved.Changes.Revision + 1, structures.ToArray(), pieces.ToArray(), tombstones.ToArray()), saved.Changes.Revision);
        Console.WriteLine($"DEMO origin={origin} pieces={pieces.Count} save={directory}");
    }
}
