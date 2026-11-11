using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using StargateGame.Shared;
namespace StargateGame.Building;

// Derived trim repairs existing saves as well as newly placed walls. It is owned
// by a real wall and never adds saved pieces. This is detailed render/picking
// geometry; BuildingGeometry.CollisionMesh substitutes smooth corner shells.
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
        // Contact is geometric, not ownership-based: independently started
        // structures may meet. Compare in world space with placement tolerance.
        var worldEnds = state.Pieces.Where(p => p.Kind == PieceKind.Prefab &&
            p.Prefab.StartsWith("kit-", StringComparison.Ordinal) &&
            BuildingPrefabs.Get(p.Prefab).Role is "wall" or "window" or "doorway")
            .SelectMany(p =>
            {
                var def = BuildingPrefabs.Get(p.Prefab);
                var transform = BuildingPrefabs.Transform(state.Structures.Single(s => s.Id == p.StructureId),p);
                return new[] { -1, 1 }.Select(sign => (Owner:p.Id,
                    Point:Vector3.Transform(new Vector3(sign*def.Width/2,0,0),transform), Height:def.Height));
            }).ToArray();
        var solidWalls = state.Pieces.Where(p => p.Kind == PieceKind.Prefab &&
            p.Prefab.StartsWith("kit-", StringComparison.Ordinal) && BuildingPrefabs.Get(p.Prefab).Role == "wall")
            .Select(p =>
            {
                Matrix4x4.Invert(BuildingPrefabs.Transform(state.Structures.Single(s=>s.Id==p.StructureId),p),out var inverse);
                return (Piece:p, Inverse:inverse, Definition:BuildingPrefabs.Get(p.Prefab));
            }).ToArray();
        foreach (var structure in state.Structures)
        {
            var pieces = state.Pieces.Where(p => p.StructureId == structure.Id && p.Kind == PieceKind.Prefab).ToArray();
            var endpoints = new Dictionary<(int, int, int), List<(BuildingPiece Wall, Vector3 Point, Vector3 Inward, int Sign)>>();
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
                    list.Add((wall, point, -tangent * sign, sign));
                }
            }
            foreach (var ends in endpoints.Values)
            {
                if (ends.Count == 1)
                {
                    var end = ends[0];
                    var world = Vector3.Transform(end.Point,BuildingGeometry.Transform(structure));
                    float height = BuildingPrefabs.Get(end.Wall.Prefab).Height;
                    if (worldEnds.Any(other => other.Owner != end.Wall.Id &&
                        Math.Abs(other.Point.Y-world.Y)<.025f && other.Height>=height-.025f &&
                        Vector2.DistanceSquared(new(other.Point.X,other.Point.Z),new(world.X,world.Z))<.025f*.025f))
                        continue;
                    // A solid wall can also cover an end at a T junction. Do
                    // not use whole window/door bounds: their openings are empty.
                    if (solidWalls.Any(other =>
                    {
                        if (other.Piece.Id == end.Wall.Id) return false;
                        var local = Vector3.Transform(world,other.Inverse);
                        var d = other.Definition;
                        return Math.Abs(local.Y)<.025f && d.Height>=height-.025f &&
                            local.X>d.Min[0]+.025f && local.X<d.Max[0]-.025f &&
                            local.Z>=d.Min[2]-.025f && local.Z<=d.Max[2]+.025f;
                    })) continue;
                    if (pieces.Any(p => p.Prefab == Model && Vector3.DistanceSquared(p.Offset, end.Point) < .25f))
                        continue;
                    string cap = $"kit-endcap-{end.Wall.Prefab}-{(end.Sign < 0 ? "left" : "right")}";
                    string capIdentity = $"endcap:{structure.Id}:{end.Wall.Id}:{end.Sign}:{end.Wall.Prefab}";
                    var capId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(capIdentity)).AsSpan(0, 16));
                    result.Add(new(capId, structure.Id, PieceKind.Prefab, default, end.Wall.Rotation, 0,
                        Prefab: cap, Offset: end.Point, Parent: end.Wall.Id));
                    continue;
                }
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
