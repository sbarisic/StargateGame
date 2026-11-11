using System.Numerics;
using StargateGame.Shared;
using static StargateGame.Building.BuildingGeometry;

namespace StargateGame.Building;

public sealed record BuildEdit(WorldChanges State, BuildingPiece? Piece, string Error, Guid[] Dependents)
{
    public bool Valid => Error.Length == 0;
}
public static class BuildingRules
{
    public static WorldChanges Destroy(WorldChanges state, IEnumerable<Guid> hitPieces, IEnumerable<SceneryRemoval> scenery)
    {
        var removed = hitPieces.ToHashSet();
        var next = state with
        {
            Pieces = state.Pieces.Where(p => !removed.Contains(p.Id)).ToArray()
        };
        while (true)
        {
            var unsupported = Unsupported(next).ToHashSet();
            if (unsupported.Count == 0)
                break;
            next = next with
            {
                Pieces = next.Pieces.Where(p => !unsupported.Contains(p.Id)).ToArray()
            };
        }
        var structures = next.Pieces.Select(p => p.StructureId).ToHashSet();
        next = next with
        {
            Revision = state.Revision + 1,
            Structures = next.Structures.Where(s => structures.Contains(s.Id)).ToArray(),
            Removed = state.Removed.Concat(scenery).Distinct().ToArray()
        };
        ValidateState(next);
        return next;
    }

    public static void ValidateState(WorldChanges s)
    {
        if (s.Pieces.Length > 4096 || s.Structures.Length > 4096 || s.Removed.Length > 65536 || s.Revision < 0 || s.Structures.Select(x => x.Id).Distinct().Count() != s.Structures.Length || s.Pieces.Select(x => x.Id).Distinct().Count() != s.Pieces.Length)
            throw new InvalidDataException("Invalid building state size/IDs.");
        foreach (var b in s.Structures)
            if (b.Id == Guid.Empty || !Finite(b.Origin) || b.Rotation is < 0 or > 11)
                throw new InvalidDataException("Invalid structure origin.");
        foreach (var p in s.Pieces)
            if (p.Id == Guid.Empty || !Enum.IsDefined(p.Kind) || p.Rotation is < 0 or > 11 || p.Level is < 0 or > 8 || p.Level == 8 && !Floor(p.Kind) || !float.IsFinite(p.FoundationBottom) || Math.Abs((long)p.Point.XA) > 4096 || Math.Abs((long)p.Point.XB) > 4096 || Math.Abs((long)p.Point.ZA) > 4096 || Math.Abs((long)p.Point.ZB) > 4096 || !s.Structures.Any(b => b.Id == p.StructureId))
                throw new InvalidDataException("Invalid building piece.");
        if (s.Removed.Any(r => r.Category is not ("tree" or "rock") || !r.Chunk.Valid) || s.Removed.Select(r => (r.Category, r.Id)).Distinct().Count() != s.Removed.Length)
            throw new InvalidDataException("Invalid scenery removal.");
        foreach (var p in s.Pieces.Where(p => p.Kind == PieceKind.Prefab))
            if (!BuildingPrefabs.Known(p.Prefab) || !Finite(p.Offset) || p.Parent != Guid.Empty && !s.Pieces.Any(q => q.Id == p.Parent && q.StructureId == p.StructureId && q.Id != p.Id))
                throw new InvalidDataException("Invalid prefab attachment.");
        if (BuildingPrefabs.Unsupported(s).Length > 0)
            throw new InvalidDataException("Unsupported or cyclic prefab attachment.");
    }
    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && Math.Abs(v.X) <= 2048 && Math.Abs(v.Y) <= 2048 && Math.Abs(v.Z) <= 2048;
    public static BuildEdit Edit(WorldChanges state, BuildCommand cmd, Func<float, float, float> height, Func<float, float, bool> dry)
    {
        BuildingPiece? draft = null;
        WorldChanges draftState = state;
        BuildEdit Fail(string text, Guid[]? ids = null) => new(draftState, draft, text, ids ?? []);
        if (cmd.Revision != state.Revision)
            return Fail("World changed; try again.");
        if (cmd.RequestId == Guid.Empty || !Enum.IsDefined(cmd.Kind) || cmd.Rotation is < 0 or > 11 || !Finite(cmd.Anchor))
            return Fail("Invalid building command.");
        var target = state.Pieces.FirstOrDefault(p => p.Id == cmd.Target);
        if (target is not null && state.Structures.Any(s => s.Id == target.StructureId && s.Generated) && cmd.Action != "door")
            return Fail("Generated structures can be explored, but not modified.");
        if (cmd.Kind == PieceKind.Prefab && BuildingPrefabs.Known(cmd.Prefab) && BuildingPrefabs.Get(cmd.Prefab).Group == "Generated")
            return Fail("Generated templates are not player building pieces.");
        if (cmd.Kind == PieceKind.Prefab || target?.Kind == PieceKind.Prefab)
            return PrefabRules.Edit(state, cmd, height, dry);
        if (cmd.Action == "remove" || cmd.Action == "door")
        {
            if (target is null)
                return Fail("Piece no longer exists.");
            if (cmd.Action == "door" && target.Kind != PieceKind.Door)
                return Fail("Select a door.");
            var pieces = cmd.Action == "remove" ? state.Pieces.Where(p => p.Id != target.Id).ToArray() : state.Pieces.Select(p => p.Id == target.Id ? p with { Open = !p.Open } : p).ToArray();
            var next = state with
            {
                Revision = state.Revision + 1,
                Pieces = pieces,
                Structures = state.Structures.Where(s => pieces.Any(p => p.StructureId == s.Id)).ToArray()
            };
            var unsupported = Unsupported(next);
            return unsupported.Length > 0 ? Fail("Remove dependent pieces first.", unsupported) : new(next, cmd.Action == "door" ? pieces.Single(p => p.Id == target.Id) : target, "", []);
        }
        if (cmd.Action != "place")
            return Fail("Unknown building action.");
        if (state.Pieces.Length >= 4096)
            return Fail("Location limit: 4096 pieces.");
        StructureRecord structure;
        BuildingPiece piece;
        bool root = target is null;
        if (root)
        {
            if (cmd.Target != Guid.Empty || !Foundation(cmd.Kind))
                return Fail("Start with a foundation on terrain.");
            var anchor = new Vector3(MathF.Round(cmd.Anchor.X * 4) / 4, MathF.Round(cmd.Anchor.Y * 4) / 4, MathF.Round(cmd.Anchor.Z * 4) / 4);
            structure = new(Guid.NewGuid(), anchor, cmd.Rotation);
            piece = new(Guid.NewGuid(), structure.Id, cmd.Kind, default, 0, 0);
        }
        else
        {
            structure = state.Structures.Single(s => s.Id == target!.StructureId);
            if (state.Pieces.Count(p => p.StructureId == structure.Id) >= 1024)
                return Fail("Structure limit: 1024 pieces.");
            if (cmd.Kind == PieceKind.Door)
            {
                if (target!.Kind != PieceKind.Doorway)
                    return Fail("Doors need a doorway.");
                piece = new(Guid.NewGuid(), structure.Id, cmd.Kind, target.Point, target.Rotation, target.Level);
            }
            else if (Wall(target!.Kind))
            {
                if (!Floor(cmd.Kind))
                    return Fail("A wall top supports a floor.");
                if (cmd.Socket is < 0 or > 1)
                    return Fail("Invalid wall socket.");
                var point = cmd.Socket == 0 ? target.Point : Add(target.Point, Direction(target.Rotation));
                piece = new(Guid.NewGuid(), structure.Id, cmd.Kind, point, Mod(target.Rotation + (cmd.Socket == 0 ? 0 : 6)), target.Level + 1);
            }
            else if (Foundation(target.Kind) || Floor(target.Kind))
            {
                if (cmd.Kind == PieceKind.Stairs)
                {
                    if (Triangle(target.Kind))
                        return Fail("Stairs need a square floor.");
                    piece = new(Guid.NewGuid(), structure.Id, cmd.Kind, target.Point, target.Rotation, target.Level);
                }
                else
                {
                    if (cmd.Socket < 0 || cmd.Socket >= Corners(target).Length)
                        return Fail("Invalid socket.");
                    var e = Edge(target, cmd.Socket);
                    piece = Wall(cmd.Kind) ? new(Guid.NewGuid(), structure.Id, cmd.Kind, e.A, e.Rotation, target.Level) :
                        new(Guid.NewGuid(), structure.Id, cmd.Kind, e.B, Mod(e.Rotation + 6), target.Level);
                    if (!Wall(cmd.Kind) && !(Foundation(cmd.Kind) && Foundation(target.Kind) || Floor(cmd.Kind) && Floor(target.Kind)))
                        return Fail("Incompatible socket.");
                }
            }
            else
                return Fail("Select a foundation, floor, or wall socket.");
        }
        draft = piece with
        {
            FoundationBottom = structure.Origin.Y - .2f
        };
        draftState = root ? state with
        {
            Structures = state.Structures.Append(structure).ToArray()
        } : state;
        if (piece.Level > 8 || piece.Level == 8 && !Floor(piece.Kind))
            return Fail("Maximum eight storeys.");
        if (Foundation(piece.Kind))
        {
            float bottom = float.PositiveInfinity;
            foreach (var corner in Corners(piece))
            {
                var p = WorldPoint(structure, corner, 0);
                float h = height(p.X, p.Z);
                if (!float.IsFinite(h) || !dry(p.X, p.Z))
                    return Fail("Foundation needs dry, loaded terrain.");
                if (p.Y - h < .149f || p.Y - h > 2.001f)
                    return Fail("Foundation must be 0.15–2 m above every terrain corner.");
                bottom = Math.Min(bottom, h - .15f);
            }
            piece = piece with
            {
                FoundationBottom = bottom
            };
        }
        draft = piece;
        var structures = root ? state.Structures.Append(structure).ToArray() : state.Structures;
        var mesh = Mesh(structure, piece);
        foreach (var other in state.Pieces)
        {
            var os = state.Structures.Single(s => s.Id == other.StructureId);
            if (piece.StructureId == other.StructureId)
            {
                if (piece.Kind == PieceKind.Door && other.Kind == PieceKind.Doorway && piece.Point == other.Point && piece.Rotation == other.Rotation && piece.Level == other.Level)
                    continue;
                if (Wall(piece.Kind) && Wall(other.Kind) && piece.Level == other.Level && SharesEnd(piece, other))
                    continue;
            }
            if (Overlaps(mesh, Mesh(os, other)))
                return Fail("Space is occupied by another building piece.");
        }
        var proposed = state with
        {
            Revision = state.Revision + 1,
            Structures = structures,
            Pieces = state.Pieces.Append(piece).ToArray()
        };
        var invalid = Unsupported(proposed);
        if (invalid.Length > 0)
            return Fail("Piece needs connected structural support.", invalid);
        return new(proposed, piece, "", []);
    }
    static bool SharesEnd(BuildingPiece a, BuildingPiece b)
    {
        var ae = Add(a.Point, Direction(a.Rotation));
        var be = Add(b.Point, Direction(b.Rotation));
        if (a.Point == b.Point && ae == be || a.Point == be && ae == b.Point)
            return false;
        return a.Point == b.Point || a.Point == be || ae == b.Point || ae == be;
    }
    static bool SameEdge(BuildPoint a, BuildPoint b, BuildPoint c, BuildPoint d) => a == c && b == d || a == d && b == c;
    public static Guid[] Unsupported(WorldChanges state)
    {
        HashSet<Guid> strong = [], weak = [];
        foreach (var f in state.Pieces.Where(p => Foundation(p.Kind) && p.Level == 0))
            strong.Add(f.Id);
        for (int iteration = 0; iteration < 25; iteration++)
        {
            int before = strong.Count + weak.Count;
            foreach (var p in state.Pieces.Where(p => p.Kind != PieceKind.Prefab && !strong.Contains(p.Id)))
            {
                var peers = state.Pieces.Where(q => q.StructureId == p.StructureId && q.Id != p.Id);
                if (Wall(p.Kind))
                {
                    if (peers.Any(q => strong.Contains(q.Id) && q.Level == p.Level && (Floor(q.Kind) || Foundation(q.Kind)) && Enumerable.Range(0, Corners(q).Length).Any(i =>
                    {
                        var e = Edge(q, i);
                        return SameEdge(p.Point, Add(p.Point, Direction(p.Rotation)), e.A, e.B);
                    })))
                        strong.Add(p.Id);
                }
                else if (p.Kind == PieceKind.Door)
                {
                    if (peers.Any(q => strong.Contains(q.Id) && q.Kind == PieceKind.Doorway && q.Point == p.Point && q.Rotation == p.Rotation && q.Level == p.Level))
                        strong.Add(p.Id);
                }
                else if (p.Kind == PieceKind.Stairs)
                {
                    if (peers.Any(q => strong.Contains(q.Id) && !Triangle(q.Kind) && (Floor(q.Kind) || Foundation(q.Kind)) && q.Point == p.Point && q.Rotation == p.Rotation && q.Level == p.Level))
                        strong.Add(p.Id);
                }
                else if (Floor(p.Kind))
                {
                    bool supported = peers.Any(q => strong.Contains(q.Id) && Wall(q.Kind) && q.Level + 1 == p.Level && Enumerable.Range(0, Corners(p).Length).Any(i =>
                    {
                        var e = Edge(p, i);
                        return SameEdge(e.A, e.B, q.Point, Add(q.Point, Direction(q.Rotation)));
                    }));
                    if (supported)
                    {
                        strong.Add(p.Id);
                        weak.Remove(p.Id);
                    }
                    else if (peers.Any(q => strong.Contains(q.Id) && Floor(q.Kind) && q.Level == p.Level && Enumerable.Range(0, Corners(p).Length).Any(i => Enumerable.Range(0, Corners(q).Length).Any(j =>
                    {
                        var a = Edge(p, i);
                        var b = Edge(q, j);
                        return SameEdge(a.A, a.B, b.A, b.B);
                    }))))
                        weak.Add(p.Id);
                }
            }
            if (before == strong.Count + weak.Count)
                break;
        }
        return state.Pieces.Where(p => p.Kind != PieceKind.Prefab && !strong.Contains(p.Id) && !weak.Contains(p.Id)).Select(p => p.Id).Concat(BuildingPrefabs.Unsupported(state)).ToArray();
    }
    // SAT of projected convex footprints plus vertical interval. Conservatively reserves stairwells and window apertures.
    public static bool Overlaps(BuildTriangle[] a, BuildTriangle[] b, float epsilon = .015f)
    {
        if (!Bounds(a).Intersects(Bounds(b), epsilon))
            return false;
        var av = a.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        var bv = b.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        foreach (var t in a.Concat(b))
            foreach (var edge in new[] { t.B - t.A, t.C - t.B, t.A - t.C })
            {
                var axis = new Vector3(-edge.Z, 0, edge.X);
                if (axis.LengthSquared() < 1e-8f)
                    continue;
                axis = Vector3.Normalize(axis);
                float amin = av.Min(p => Vector3.Dot(p, axis)), amax = av.Max(p => Vector3.Dot(p, axis));
                float bmin = bv.Min(p => Vector3.Dot(p, axis)), bmax = bv.Max(p => Vector3.Dot(p, axis));
                if (amax <= bmin + epsilon || bmax <= amin + epsilon)
                    return false;
            }
        return true;
    }
}
