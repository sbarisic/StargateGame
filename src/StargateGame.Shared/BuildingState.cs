using System.Numerics;

namespace StargateGame.Shared;

public enum PieceKind
{
    SquareFoundation, TriangleFoundation, SquareFloor, TriangleFloor, Wall, Window, Doorway, Door, Stairs, Prefab
}
public sealed record StructureRecord(Guid Id, Vector3 Origin, int Rotation, bool Generated = false);
// X/Z are canonical coordinates in units of half an edge: (A + B*sqrt(3)).
public readonly record struct BuildPoint(int XA, int XB, int ZA, int ZB)
{
    public Vector3 Position => new((float)(1.5 * (XA + XB * Math.Sqrt(3))), 0, (float)(1.5 * (ZA + ZB * Math.Sqrt(3))));
}
public sealed record BuildingPiece(Guid Id, Guid StructureId, PieceKind Kind, BuildPoint Point, int Rotation, int Level, bool Open = false, float FoundationBottom = 0, string Prefab = "", Vector3 Offset = default, Guid Parent = default, [property: System.Text.Json.Serialization.JsonIgnore] float? DoorAngle = null);
public sealed record DoorMotion(Guid Id, float Angle, bool Blocked = false, bool Open = false);
public sealed record SceneryRemoval(string Category, ulong Id, ChunkCoordinate Chunk);
public sealed record WorldChanges(Guid UniverseId, Guid LocationId, long Revision, StructureRecord[] Structures, BuildingPiece[] Pieces, SceneryRemoval[] Removed)
{
    public static WorldChanges Empty
    {
        get;
    } = new(Guid.Empty, Guid.Empty, 0, [], [], []);
    public bool IsRemoved(string category, ulong id) => Removed.Any(r => r.Category == category && r.Id == id);
}
public sealed record BuildCommand(Guid RequestId, long Revision, string Action, PieceKind Kind = PieceKind.SquareFoundation, Guid Target = default, int Socket = 0, Vector3 Anchor = default, int Rotation = 0, string Category = "", ulong SceneryId = 0, ChunkCoordinate Chunk = default, string Prefab = "", Vector3? HitPoint = null);
public sealed record BuildingMetrics(int Pieces, long Revision, int PendingEdits, int RebuiltBatches, int SaveQueue, double SaveMilliseconds);
public sealed record BuildResult(Guid RequestId, bool Success, string Reason, long Revision, Guid[]? Dependents = null);
public sealed record WorldStatePart(Guid LocationId, long Revision, int Index, int Count, StructureRecord[] Structures, BuildingPiece[] Pieces, SceneryRemoval[] Removed, Guid UniverseId);

public static class WorldStateWire
{
    public static IEnumerable<WorldStatePart> Split(WorldChanges state)
    {
        int count = Math.Max(1, Math.Max((state.Pieces.Length + 31) / 32, Math.Max((state.Structures.Length + 31) / 32, (state.Removed.Length + 63) / 64)));
        for (int i = 0; i < count; i++)
            yield return new(state.LocationId, state.Revision, i, count, state.Structures.Skip(i * 32).Take(32).ToArray(), state.Pieces.Skip(i * 32).Take(32).ToArray(), state.Removed.Skip(i * 64).Take(64).ToArray(), state.UniverseId);
    }
}
public sealed class WorldStateReceiver
{
    readonly List<WorldStatePart> parts = [];
    public WorldChanges? State
    {
        get;
        private set;
    }
    public void Add(WorldStatePart p)
    {
        if (State is not null || p is null || p.Count is < 1 or > 1024 || p.Index < 0 || p.Index >= p.Count || p.Index != parts.Count || p.Structures is null || p.Pieces is null || p.Removed is null || p.Structures.Length > 32 || p.Pieces.Length > 32 || p.Removed.Length > 64 || p.Structures.Any(x => x is null) || p.Pieces.Any(x => x is null) || p.Removed.Any(x => x is null) || p.Revision < 0 ||
            parts.Count > 0 && (p.LocationId != parts[0].LocationId || p.UniverseId != parts[0].UniverseId || p.Revision != parts[0].Revision || p.Count != parts[0].Count))
            throw new InvalidDataException("Invalid world state fragment.");
        parts.Add(p);
        if (parts.Count == p.Count)
        {
            State = new(p.UniverseId, p.LocationId, p.Revision, parts.SelectMany(x => x.Structures).ToArray(), parts.SelectMany(x => x.Pieces).ToArray(), parts.SelectMany(x => x.Removed).ToArray());
            if (State.Pieces.Length > 4096 || State.Structures.Length > 4096 || State.Removed.Length > 65536 || State.Pieces.Select(x => x.Id).Distinct().Count() != State.Pieces.Length)
                throw new InvalidDataException("World state limit exceeded.");
        }
    }
}
