using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;

namespace StargateGame.Building;

public sealed record PrefabChild(string Prefab, float[] Offset, int Rotation = 0);
public sealed record BuildingPrefab(string Id, string Title, string Group, string Role, float Width, float Depth, float Height, bool Ground, float[] Min, float[] Max, float[][][] Triangles, PrefabChild[]? Children = null, bool Selectable = true, float[][][]? BaseCourses = null, float[][][]? DoorSections = null, float[][]? DoorSeat = null, float[]? DoorPivot = null)
{
    readonly Lazy<float[][][]> triangles = new(() => Triangles.Length == 0 ? BuildingPrefabs.LoadTriangles(Id) : Triangles);
    public float[][][] Triangles => triangles.Value;
}
public readonly record struct PrefabSocket(Vector3 Position, int Rotation, string Name);
public static class BuildingPrefabs
{
    public const int Version = 1;
    static readonly Dictionary<string, ReadOnlyMemory<byte>> collisionSources = [];
    static readonly ConcurrentDictionary<string, Lazy<BuildTriangle[]>> meshes = new();
    internal static float[][][] LoadTriangles(string id) => collisionSources.TryGetValue(id, out var bytes)
        ? JsonSerializer.Deserialize<float[][][]>(bytes.Span)! : [];
    static BuildingPrefab[] Metadata(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        List<BuildingPrefab> result = [];
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new InvalidDataException("Invalid prefab catalog.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Invalid prefab descriptor.");
            using var small = new MemoryStream();
            ReadOnlyMemory<byte> triangles = default;
            using (var writer = new Utf8JsonWriter(small))
            {
                writer.WriteStartObject();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    string name = reader.GetString()!;
                    reader.Read();
                    long begin = reader.TokenStartIndex;
                    reader.Skip();
                    int length = checked((int)(reader.BytesConsumed - begin));
                    writer.WritePropertyName(name);
                    if (name.Equals("triangles", StringComparison.OrdinalIgnoreCase))
                    {
                        triangles = bytes.AsMemory(checked((int)begin), length);
                        writer.WriteStartArray(); writer.WriteEndArray();
                    }
                    else writer.WriteRawValue(bytes.AsSpan(checked((int)begin), length), skipInputValidation: true);
                }
                writer.WriteEndObject();
            }
            var prefab = JsonSerializer.Deserialize<BuildingPrefab>(small.ToArray(), options)!;
            if (!triangles.IsEmpty) collisionSources[prefab.Id] = triangles;
            result.Add(prefab);
        }
        return result.ToArray();
    }
    static readonly byte[] data = Read(), generatedData = ReadGenerated(), furnishingData = FurnishingCatalog.Read("furnishing-prefabs.json");
    public static string Hash
    {
        get;
    } = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.Concat(generatedData).Concat(furnishingData).Concat(System.Text.Encoding.UTF8.GetBytes(FurnishingCatalog.Hash + ":foundation-footprints-player-stair-ramps-placement-volumes-v2-door-sections-targeting-removal-v1")).ToArray()));
    public static IReadOnlyList<BuildingPrefab> All
    {
        get;
    } = Metadata(data);
    public static IReadOnlyList<BuildingPrefab> Palette
    {
        get;
    } = All.Where(p => p.Selectable).ToArray();
    static IReadOnlyList<BuildingPrefab> Generated
    {
        get;
    } = LoadGenerated();
    static byte[] ReadGenerated()
    {
        using var stream = typeof(BuildingPrefabs).Assembly.GetManifestResourceStream("settlement-prefabs.json")!;
        using var m = new MemoryStream();
        stream.CopyTo(m);
        return m.ToArray();
    }
    static IReadOnlyList<BuildingPrefab> LoadGenerated() => Metadata(generatedData)
        .Concat(Metadata(furnishingData)).ToArray();
    static readonly Dictionary<string, BuildingPrefab> byId = All.Concat(Generated).ToDictionary(p => p.Id);
    static byte[] Read()
    {
        using var stream = typeof(BuildingPrefabs).Assembly.GetManifestResourceStream("prefabs.json")!;
        using var m = new MemoryStream();
        stream.CopyTo(m);
        return m.ToArray();
    }
    static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    public static BuildingPrefab Get(string id) => byId.TryGetValue(id, out var p) ? p : throw new ArgumentException("Unknown building prefab.");
    public static bool Known(string id) => id is not null && byId.ContainsKey(id);
    public static bool IsDoor(BuildingPiece p) => p.Kind == PieceKind.Door || p.Kind == PieceKind.Prefab && Get(p.Prefab).Role == "door";
    public static Matrix4x4 Transform(StructureRecord s, BuildingPiece p)
    {
        var pivot=p.Kind==PieceKind.Prefab && IsDoor(p) && Get(p.Prefab).DoorPivot is { } point ? V(point) : Vector3.Zero;
        var swing=IsDoor(p) ? Matrix4x4.CreateTranslation(-pivot)*Matrix4x4.CreateRotationY(p.DoorAngle ?? (p.Open ? MathF.PI/2 : 0))*Matrix4x4.CreateTranslation(pivot) : Matrix4x4.Identity;
        return swing*Matrix4x4.CreateRotationY(-p.Rotation*MathF.PI/6)*Matrix4x4.CreateTranslation(p.Offset)*BuildingGeometry.Transform(s);
    }
    public static BuildTriangle[] Mesh(StructureRecord s, BuildingPiece p)
    {
        var m = Transform(s, p);
        return meshes.GetOrAdd(p.Prefab, id => new(() => Get(id).Triangles.Select(t => new BuildTriangle(V(t[0]), V(t[1]), V(t[2]))).ToArray())).Value.Select(t => new BuildTriangle(Vector3.Transform(t.A, m), Vector3.Transform(t.B, m), Vector3.Transform(t.C, m))).ToArray();
    }
    public static BuildingPiece[] CreateChildren(BuildingPiece root) => (Get(root.Prefab).Children ?? []).Select(c =>
    {
        var (offset, rotation) = Attach(root, new(new(c.Offset[0], c.Offset[1], c.Offset[2]), c.Rotation, "assembly"));
        return new BuildingPiece(Guid.NewGuid(), root.StructureId, PieceKind.Prefab, default, rotation, root.Level, Prefab: c.Prefab, Offset: offset, Parent: root.Id);
    }).ToArray();
    public static bool IsAssemblyChild(WorldChanges state, BuildingPiece child) => child.Parent != Guid.Empty && state.Pieces.FirstOrDefault(p => p.Id == child.Parent) is { } root && root.Kind == PieceKind.Prefab && (Get(root.Prefab).Children ?? []).Any(c => c.Prefab == child.Prefab);
    // Attachment sockets are in the target's native metre basis, independent of camera/LOD.
    public static PrefabSocket[] Sockets(BuildingPiece target, BuildingPrefab next)
    {
        var t = Get(target.Prefab);
        List<PrefabSocket> sockets = [];
        if (next.Role == "door")
        {
            if (t.Role == "doorway")
                sockets.Add(new(new(-.53f, 0, 0), 0, "hinge"));
        }
        else if (next.Role is "insert" or "frame")
        {
            if (t.Role == (next.Role == "frame" ? "doorway" : "window"))
                sockets.Add(new(Vector3.Zero, 0, "opening"));
        }
        else if (t.Role is "foundation" or "floor" or "landing")
        {
            if (next.Role is "floor" or "roof" && t.Role == "foundation")
                sockets.Add(new(Vector3.Zero, 0, "surface"));
            else if (next.Role == "roof")
                sockets.Add(new(Vector3.Zero, 0, "roof"));
            else
            {
                for (int edge = 0; edge < 4; edge++)
                {
                    float angle = edge * MathF.PI / 2;
                    var normal = new Vector3(MathF.Sin(angle), 0, -MathF.Cos(angle));
                    float half = edge % 2 == 0 ? t.Depth / 2 : t.Width / 2;
                    bool wall = next.Role is "wall" or "window" or "doorway" or "parapet";
                    float extend = wall ? 0 : next.Depth / 2;
                    sockets.Add(new(normal * (half + extend), edge * 3, "edge " + (edge + 1)));
                }
            }
        }
        else if (t.Role is "wall" or "window" or "doorway" or "corner" or "parapet")
        {
            if (next.Role is "wall" or "window" or "doorway" or "corner" or "parapet")
            {
                sockets.Add(new(new(-(t.Width + next.Width) / 2, 0, 0), 0, "left"));
                sockets.Add(new(new((t.Width + next.Width) / 2, 0, 0), 0, "right"));
                sockets.Add(new(new(t.Width / 2, 0, next.Width / 2), 3, "right corner"));
                sockets.Add(new(new(-t.Width / 2, 0, next.Width / 2), 9, "left corner"));
                if (next.Role == "parapet")
                    sockets.Add(new(new(0, t.Height, 0), 0, "top"));
            }
            else if (next.Role is "floor" or "roof" or "landing")
            {
                sockets.Add(new(new(0, t.Height, next.Depth / 2), 0, "top inside"));
                sockets.Add(new(new(0, t.Height, -next.Depth / 2), 6, "top outside"));
            }
        }
        else if (t.Role == "stairs" && next.Role is "floor" or "landing")
            sockets.Add(new(new(0, t.Height, t.Depth / 2 + next.Depth / 2), 0, "upper landing"));
        if (next.Id == "kit-roof-front-brick6")
        {
            if (t.Id == "kit-roof-roundtiles-6x6")
            {
                sockets.Add(new(new(0, 0, -3), 0, "front gable"));
                sockets.Add(new(new(0, 0, 3), 6, "rear gable"));
            }
        }
        else if (next.Role is "trim" or "decoration")
            sockets.Add(new(new(t.Width / 2, 0, 0), 0, "detail"));
        return sockets.ToArray();
    }
    public static (Vector3 Offset, int Rotation) Attach(BuildingPiece target, PrefabSocket socket)
    {
        var r = Matrix4x4.CreateRotationY(-target.Rotation * MathF.PI / 6);
        return (target.Offset + Vector3.Transform(socket.Position, r), BuildingGeometry.Mod(target.Rotation + socket.Rotation));
    }
    public static Guid[] Unsupported(WorldChanges state)
    {
        var pieces = state.Pieces.ToDictionary(p => p.Id);
        Dictionary<Guid, bool> known = [];
        foreach (var start in state.Pieces.Where(p => p.Kind == PieceKind.Prefab))
        {
            List<Guid> path = [];
            HashSet<Guid> visiting = [];
            var p = start;
            bool supported = false;
            while (true)
            {
                if (known.TryGetValue(p.Id, out supported))
                    break;
                if (p.Kind != PieceKind.Prefab || !visiting.Add(p.Id))
                {
                    supported = false;
                    break;
                }
                path.Add(p.Id);
                if (p.Parent == Guid.Empty)
                {
                    supported = Known(p.Prefab) && Get(p.Prefab).Ground;
                    break;
                }
                if (!pieces.TryGetValue(p.Parent, out var parent) || parent.StructureId != p.StructureId)
                {
                    supported = false;
                    break;
                }
                p = parent;
            }
            foreach (var id in path)
                known[id] = supported;
        }
        return known.Where(k => !k.Value).Select(k => k.Key).ToArray();
    }
}
