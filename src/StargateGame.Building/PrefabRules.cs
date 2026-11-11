using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Building;

public static class PrefabRules
{
    public static BuildEdit Edit(WorldChanges state, BuildCommand cmd, Func<float, float, float> height, Func<float, float, bool> dry)
    {
        BuildEdit Fail(string reason, BuildingPiece? p = null, WorldChanges? s = null, Guid[]? dependents = null) => new(s ?? state, p, reason, dependents ?? []);
        var target = state.Pieces.FirstOrDefault(p => p.Id == cmd.Target);
        if (cmd.Action == "remove" && target is not null && BuildingPrefabs.IsAssemblyChild(state, target))
            target = state.Pieces.Single(p => p.Id == target.Parent);
        if (cmd.Action != "door" && target is not null && state.Structures.Any(s => s.Id == target.StructureId && s.Generated))
            return Fail("Generated structures cannot be modified.");
        if (cmd.Action == "place" && BuildingPrefabs.Known(cmd.Prefab) && BuildingPrefabs.Get(cmd.Prefab).Group == "Generated")
            return Fail("Generated templates are not player building pieces.");
        if (cmd.Action is "remove" or "door")
        {
            if (target is null)
                return Fail("Piece no longer exists.");
            if (cmd.Action == "door" && !BuildingPrefabs.IsDoor(target))
                return Fail("Select a door.");
            var pieces = cmd.Action == "remove" ? state.Pieces.Where(p => p.Id != target.Id && !(p.Parent == target.Id && BuildingPrefabs.IsAssemblyChild(state, p))).ToArray() : state.Pieces.Select(p => p.Id == target.Id ? p with { Open = !p.Open } : p).ToArray();
            var next = state with
            {
                Revision = state.Revision + 1,
                Pieces = pieces,
                Structures = state.Structures.Where(s => pieces.Any(p => p.StructureId == s.Id)).ToArray()
            };
            var invalid = BuildingRules.Unsupported(next);
            return invalid.Length > 0 ? Fail("Remove attached pieces first.", dependents: invalid) : new(next, cmd.Action == "remove" ? target : pieces.Single(p => p.Id == target.Id), "", []);
        }
        if (cmd.Action != "place" || cmd.Kind != PieceKind.Prefab || !BuildingPrefabs.Known(cmd.Prefab))
            return Fail("Unknown building prefab.");
        if (state.Pieces.Length >= 4096)
            return Fail("Location limit: 4096 pieces.");
        var definition = BuildingPrefabs.Get(cmd.Prefab);
        StructureRecord structure;
        BuildingPiece piece;
        if (target is null)
        {
            if (cmd.Target != Guid.Empty || !definition.Ground)
                return Fail("Attach this piece to a foundation, floor or wall.");
            var anchor = new Vector3(MathF.Round(cmd.Anchor.X * 4) / 4, MathF.Round(cmd.Anchor.Y * 4) / 4, MathF.Round(cmd.Anchor.Z * 4) / 4);
            structure = new(Guid.NewGuid(), anchor, cmd.Rotation);
            piece = new(Guid.NewGuid(), structure.Id, PieceKind.Prefab, default, 0, 0, Prefab: definition.Id);
        }
        else
        {
            if (target.Kind != PieceKind.Prefab)
                return Fail("Start the new kit on its own foundation; existing buildings keep their grid.");
            structure = state.Structures.Single(s => s.Id == target.StructureId);
            if (state.Pieces.Count(p => p.StructureId == structure.Id) >= 1024)
                return Fail("Structure limit: 1024 pieces.");
            var sockets = BuildingPrefabs.Sockets(target, definition);
            if (cmd.Socket < 0 || cmd.Socket >= sockets.Length)
                return Fail("No compatible attachment socket.");
            var (offset, rotation) = BuildingPrefabs.Attach(target, sockets[cmd.Socket]);
            piece = new(Guid.NewGuid(), structure.Id, PieceKind.Prefab, default, rotation, 0, Prefab: definition.Id, Offset: offset, Parent: target.Id);
            if (offset.Y > 24 || offset.Y < -2)
                return Fail("Maximum building height: 24 m.");
        }
        var children = BuildingPrefabs.CreateChildren(piece);
        if (state.Pieces.Length + 1 + children.Length > 4096 || state.Pieces.Count(p => p.StructureId == structure.Id) + 1 + children.Length > 1024)
            return Fail("Building piece limit reached.");
        var draft = target is null ? state with
        {
            Structures = state.Structures.Append(structure).ToArray()
        } : state;
        var mesh = BuildingPrefabs.Mesh(structure, piece);
        var bounds = BuildingGeometry.Bounds(mesh);
        if (target is null || definition.Role == "foundation")
        {
            // Footings may bury slightly; attached foundations also need ground support.
            foreach (var point in new[] { new Vector2(bounds.Min.X, bounds.Min.Z), new(bounds.Max.X, bounds.Min.Z), new(bounds.Min.X, bounds.Max.Z), new(bounds.Max.X, bounds.Max.Z) })
            {
                float ground = height(point.X, point.Y);
                if (!float.IsFinite(ground) || !dry(point.X, point.Y))
                    return Fail("Needs dry, loaded terrain.", piece, draft);
                float datum = structure.Origin.Y + piece.Offset.Y - ground;
                float min = definition.Role == "foundation" ? .15f : -.2f, max = definition.Role == "foundation" ? .85f : .3f;
                if (datum < min - .01f || datum > max + .01f)
                    return Fail("Footings must meet the terrain; adjust height or choose flatter ground.", piece, draft);
            }
        }
        foreach (var other in state.Pieces)
        {
            if (other.Kind == PieceKind.Prefab && other.StructureId == piece.StructureId)
            {
                var od = BuildingPrefabs.Get(other.Prefab);
                bool same = Vector3.DistanceSquared(other.Offset, piece.Offset) < .0001f && other.Rotation == piece.Rotation;
                bool sameLayer = od.Role == definition.Role || od.Role is "wall" or "window" or "doorway" && definition.Role is "wall" or "window" or "doorway";
                if (same && sameLayer)
                    return Fail("That socket is already occupied.", piece, draft);
                if (other.Id == piece.Parent)
                    continue;
                // The supplied kit deliberately overlaps trim, jambs, roof eaves and corner posts.
                if (definition.Role is "trim" or "frame" or "insert" or "roof" || od.Role is "trim" or "frame" or "insert" or "roof")
                    continue;
                if (od.Role is "wall" or "window" or "doorway" or "corner" && definition.Role is "wall" or "window" or "doorway" or "corner" && !same)
                    continue;
            }
            var s = state.Structures.Single(s => s.Id == other.StructureId);
            if (BuildingRules.Overlaps(mesh, BuildingGeometry.Mesh(s, other), .025f))
                return Fail("Space is occupied by another building piece.", piece, draft);
        }
        return new(draft with
        {
            Revision = state.Revision + 1,
            Pieces = state.Pieces.Append(piece).Concat(children).ToArray()
        }, piece, "", []);
    }
}
