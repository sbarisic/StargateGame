using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using StargateGame.Shared;
namespace StargateGame.Building;

// Derived trim repairs existing saves as well as newly placed walls. It is owned
// by a real wall, uses the imported collision mesh, and never adds saved pieces.
public static class BuildingCorners
{
    public const string Model = "kit-corner-exterior-brick";
    sealed record Result(BuildingPiece[] Pieces, Dictionary<Guid, BuildTriangle[]> Geometry);
    static readonly ConditionalWeakTable<WorldChanges, Result> cache = new();
    public static IReadOnlyList<BuildingPiece> Pieces(WorldChanges state) => cache.GetValue(state, Build).Pieces;
    public static BuildTriangle[] OwnedMesh(WorldChanges state, Guid wall) => cache.GetValue(state, Build).Geometry.GetValueOrDefault(wall) ?? [];
    static Result Build(WorldChanges state)
    {
        List<BuildingPiece> result = [];
        foreach (var structure in state.Structures)
        {
            var pieces = state.Pieces.Where(p => p.StructureId == structure.Id && p.Kind == PieceKind.Prefab).ToArray();
            var endpoints = new Dictionary<(int, int, int), List<(BuildingPiece Wall, Vector3 Point, Vector3 Inward)>>();
            foreach (var wall in pieces.Where(p => p.Prefab.StartsWith("kit-", StringComparison.Ordinal) && BuildingPrefabs.Get(p.Prefab).Role is "wall" or "window" or "doorway"))
            {
                float half = BuildingPrefabs.Get(wall.Prefab).Width / 2;
                var tangent = Vector3.TransformNormal(Vector3.UnitX, Matrix4x4.CreateRotationY(-wall.Rotation * MathF.PI / 6));
                foreach (int sign in new[] { -1, 1 })
                {
                    var point = wall.Offset + tangent * half * sign;
                    var key = ((int)MathF.Round(point.X * 1000), (int)MathF.Round(point.Y * 1000), (int)MathF.Round(point.Z * 1000));
                    if (!endpoints.TryGetValue(key, out var list))
                        endpoints[key] = list = [];
                    list.Add((wall, point, -tangent * sign));
                }
            }
            foreach (var ends in endpoints.Values)
            {
                if (ends.Count != 2 || Math.Abs(Vector3.Dot(ends[0].Inward, ends[1].Inward)) > .01f)
                    continue;
                var point = ends[0].Point;
                if (pieces.Any(p => p.Prefab == Model && Vector3.DistanceSquared(p.Offset, point) < .25f))
                    continue;
                var desired = -Vector3.Normalize(ends[0].Inward + ends[1].Inward);
                int rotation = Enumerable.Range(0, 12).MaxBy(i => Vector3.Dot(desired, Vector3.TransformNormal(Vector3.Normalize(new Vector3(-1, 0, 1)), Matrix4x4.CreateRotationY(-i * MathF.PI / 6))));
                var owner = ends.Select(e => e.Wall).OrderBy(p => p.Id).First();
                string identity = $"corner:{structure.Id}:{(int)MathF.Round(point.X * 1000)}:{(int)MathF.Round(point.Y * 1000)}:{(int)MathF.Round(point.Z * 1000)}";
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
                result.Add(new(id, structure.Id, PieceKind.Prefab, default, rotation, 0, Prefab: Model, Offset: point, Parent: owner.Id));
            }
        }
        var geometry = result.GroupBy(p => p.Parent).ToDictionary(g => g.Key, g => g.SelectMany(p => BuildingPrefabs.Mesh(state.Structures.Single(s => s.Id == p.StructureId), p)).ToArray());
        return new(result.ToArray(), geometry);
    }
}
