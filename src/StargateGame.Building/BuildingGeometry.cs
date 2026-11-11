using System.Numerics;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using StargateGame.Shared;

namespace StargateGame.Building;

public readonly record struct BuildTriangle(Vector3 A, Vector3 B, Vector3 C);
public readonly record struct BuildBounds(Vector3 Min, Vector3 Max)
{
    public bool Intersects(BuildBounds b, float tolerance = .015f) => Min.X < b.Max.X - tolerance && Max.X > b.Min.X + tolerance && Min.Y < b.Max.Y - tolerance && Max.Y > b.Min.Y + tolerance && Min.Z < b.Max.Z - tolerance && Max.Z > b.Min.Z + tolerance;
}
public static class BuildingGeometry
{
    public const int KitVersion = 1;
    // Cover the outer corner of a 60-degree join (0.1 / tan(30) metres).
    // Socket endpoints stay exactly 3 m apart; only the wall shell overlaps.
    public const float WallEndOverlap = .18f;
    public static bool Foundation(PieceKind k) => k is PieceKind.SquareFoundation or PieceKind.TriangleFoundation;
    public static bool Floor(PieceKind k) => k is PieceKind.SquareFloor or PieceKind.TriangleFloor;
    public static bool Wall(PieceKind k) => k is PieceKind.Wall or PieceKind.Window or PieceKind.Doorway;
    public static bool Triangle(PieceKind k) => k is PieceKind.TriangleFloor or PieceKind.TriangleFoundation;
    public static int Mod(int n) => (n % 12 + 12) % 12;
    // Exact algebraic lattice: rotated edges never accumulate rounding errors.
    public static BuildPoint Direction(int r) => Mod(r) switch { 0 => new(2, 0, 0, 0), 1 => new(0, 1, 1, 0), 2 => new(1, 0, 0, 1), 3 => new(0, 0, 2, 0), 4 => new(-1, 0, 0, 1), 5 => new(0, -1, 1, 0), 6 => new(-2, 0, 0, 0), 7 => new(0, -1, -1, 0), 8 => new(-1, 0, 0, -1), 9 => new(0, 0, -2, 0), 10 => new(1, 0, 0, -1), _ => new(0, 1, -1, 0) };
    public static BuildPoint Add(BuildPoint a, BuildPoint b) => new(a.XA + b.XA, a.XB + b.XB, a.ZA + b.ZA, a.ZB + b.ZB);
    public static BuildPoint[] Corners(BuildingPiece p)
    {
        var b = Add(p.Point, Direction(p.Rotation));
        return Triangle(p.Kind) ? [p.Point, b, Add(p.Point, Direction(p.Rotation + 2))] : [p.Point, b, Add(b, Direction(p.Rotation + 3)), Add(p.Point, Direction(p.Rotation + 3))];
    }
    public static (BuildPoint A, BuildPoint B, int Rotation) Edge(BuildingPiece p, int index)
    {
        var c = Corners(p);
        if (index < 0 || index >= c.Length)
            throw new ArgumentException("Invalid edge.");
        return (c[index], c[(index + 1) % c.Length], Mod(p.Rotation + index * (Triangle(p.Kind) ? 4 : 3)));
    }
    public static Matrix4x4 Transform(StructureRecord s) => Matrix4x4.CreateRotationY(-s.Rotation * MathF.PI / 6) * Matrix4x4.CreateTranslation(s.Origin);
    public static Vector3 WorldPoint(StructureRecord s, BuildPoint p, int level) => Vector3.Transform(p.Position + new Vector3(0, level * 3, 0), Transform(s));
    public static IEnumerable<ChunkCoordinate> Chunks(BuildBounds b)
    {
        var lo = ChunkCoordinate.At(b.Min.X, b.Min.Z);
        var hi = ChunkCoordinate.At(b.Max.X, b.Max.Z);
        for (int z = lo.Z; z <= hi.Z; z++)
            for (int x = lo.X; x <= hi.X; x++)
            {
                var c = new ChunkCoordinate(x, z);
                if (c.Valid)
                    yield return c;
            }
    }
    public static BuildBounds Bounds(IEnumerable<BuildTriangle> tris)
    {
        var points = tris.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
        return new(points.Aggregate(new Vector3(float.PositiveInfinity), Vector3.Min), points.Aggregate(new Vector3(float.NegativeInfinity), Vector3.Max));
    }
    static readonly ConditionalWeakTable<WorldChanges, ConcurrentDictionary<BuildingPiece, BuildTriangle[]>> stateMeshes = new();
    // Render/picking geometry retains the individual stones. Collision uses a
    // continuous corner shell, including for automatically derived trim.
    public static BuildTriangle[] CollisionMesh(WorldChanges state, BuildingPiece piece)
    {
        if (BuildingBaseCourses.ReplacesOwner(state,piece.Id))
            return BuildingBaseCourses.OwnedMesh(state,piece.Id);
        var structure = state.Structures.Single(s => s.Id == piece.StructureId);
        var original = piece.Kind == PieceKind.Prefab && piece.Prefab == BuildingCorners.Model
            ? CornerCollision.Mesh(structure,piece) : Mesh(structure,piece);
        var trim = BuildingCorners.Pieces(state).Where(p => p.Parent == piece.Id)
            .SelectMany(p => p.Prefab == BuildingCorners.Model
                ? CornerCollision.Mesh(structure,p) : BuildingPrefabs.Mesh(structure,p));
        return [.. original, .. trim, .. BuildingBaseCourses.OwnedMesh(state,piece.Id)];
    }
    public static BuildTriangle[] Mesh(WorldChanges state, BuildingPiece piece) =>
        stateMeshes.GetValue(state, _ => new()).GetOrAdd(piece, p => DerivedMesh(state,p));
    static BuildTriangle[] DerivedMesh(WorldChanges state, BuildingPiece piece)
    {
        if (BuildingBaseCourses.ReplacesOwner(state,piece.Id)) return BuildingBaseCourses.OwnedMesh(state,piece.Id);
        var original = Mesh(state.Structures.Single(s => s.Id == piece.StructureId), piece);
        var corners = BuildingCorners.OwnedMesh(state, piece.Id);
        var bases = BuildingBaseCourses.OwnedMesh(state, piece.Id);
        return corners.Length == 0 && bases.Length == 0 ? original : [.. original, .. corners, .. bases];
    }
    public static BuildTriangle[] Mesh(StructureRecord s, BuildingPiece p)
    {
        if (p.Kind == PieceKind.Prefab)
            return BuildingPrefabs.Mesh(s, p);
        List<BuildTriangle> list = [];
        List<BuildBounds> stairBoxes = [];
        var rotate = Matrix4x4.CreateRotationY(-p.Rotation * MathF.PI / 6) * Matrix4x4.CreateTranslation(p.Point.Position + new Vector3(0, p.Level * 3, 0)) * Transform(s);
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            list.Add(new(a, b, c));
            list.Add(new(a, c, d));
        }
        void Box(float x, float y, float z, float w, float h, float d)
        {
            if (p.Kind == PieceKind.Stairs) { stairBoxes.Add(new(new(x,y,z),new(x+w,y+h,z+d))); return; }
            Vector3 a = new(x, y, z), b = new(x + w, y, z), c = new(x + w, y, z + d), e = new(x, y, z + d), up = new(0, h, 0);
            Quad(a, b, c, e);
            Quad(a + up, e + up, c + up, b + up);
            Quad(a, a + up, b + up, b);
            Quad(b, b + up, c + up, c);
            Quad(c, c + up, e + up, e);
            Quad(e, e + up, a + up, a);
        }
        if (Foundation(p.Kind) || Floor(p.Kind))
        {
            float bottom = Foundation(p.Kind) ? p.FoundationBottom - s.Origin.Y : -.2f;
            if (Triangle(p.Kind))
            {
                Vector3 a = new(0, 0, 0), b = new(3, 0, 0), c = new(1.5f, 0, MathF.Sqrt(3) * 1.5f), down = new(0, bottom, 0);
                list.Add(new(a, c, b));
                list.Add(new(a + down, b + down, c + down));
                Quad(a, b, b + down, a + down);
                Quad(b, c, c + down, b + down);
                Quad(c, a, a + down, c + down);
            }
            else
                Box(0, bottom, 0, 3, -bottom, 3);
        }
        else if (p.Kind == PieceKind.Wall)
            Box(-WallEndOverlap, 0, -.1f, 3 + 2 * WallEndOverlap, 2.8f, .2f);
        else if (p.Kind is PieceKind.Window or PieceKind.Doorway)
        {
            float side = p.Kind == PieceKind.Window ? .5f : .95f, baseY = p.Kind == PieceKind.Window ? 1 : 0;
            Box(-WallEndOverlap, 0, -.1f, side + WallEndOverlap, 2.8f, .2f);
            Box(3 - side, 0, -.1f, side + WallEndOverlap, 2.8f, .2f);
            if (baseY > 0)
                Box(side, 0, -.1f, 3 - 2 * side, baseY, .2f);
            Box(side, 2.3f, -.1f, 3 - 2 * side, .5f, .2f);
        }
        else if (p.Kind == PieceKind.Door)
        {
            Box(0, 0, -.045f, 1.08f, 2.28f, .09f);
            var pivot = new Vector3(0, 0, -.045f);
            var hinge = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateRotationY(p.Open ? MathF.PI / 2 : 0) * Matrix4x4.CreateTranslation(pivot + new Vector3(.96f, 0, 0));
            list = list.Select(t => new BuildTriangle(Vector3.Transform(t.A, hinge), Vector3.Transform(t.B, hinge), Vector3.Transform(t.C, hinge))).ToList();
        }
        else if (p.Kind == PieceKind.Stairs)
        {
            if (p.FoundationBottom > 0)
            {
                Box(.1f, -.3f, -.6f, 1.2f, .15f, .3f);
                Box(.1f, -.3f, -.3f, 1.2f, .3f, .3f);
            }
            for (int i = 0; i < 10; i++)
                Box(.1f, 0, i * .25f, 1.2f, (i + 1) * .15f, .25f);
            Box(.1f, 1.3f, 2.5f, 2.8f, .2f, .5f);
            // Thin suspended treads keep headroom below the return flight.
            for (int i = 0; i < 10; i++)
                Box(1.7f, 1.5f + i * .15f, 2.25f - i * .25f, 1.2f, .15f, .25f);
            Box(1.7f, 2.8f, -.0f, 1.2f, .2f, .25f);
        }
        if (stairBoxes.Count > 0) list = SolidGeometry.Union(stairBoxes).ToList();
        return list.Select(t => new BuildTriangle(Vector3.Transform(t.A, rotate), Vector3.Transform(t.B, rotate), Vector3.Transform(t.C, rotate))).ToArray();
    }
    public static float? Ray(Vector3 origin, Vector3 direction, IEnumerable<BuildTriangle> mesh, float limit = 8)
    {
        float best = limit;
        bool found = false;
        foreach (var t in mesh)
        {
            var e = t.B - t.A;
            var f = t.C - t.A;
            var h = Vector3.Cross(direction, f);
            float det = Vector3.Dot(e, h);
            if (Math.Abs(det) < 1e-7f)
                continue;
            var s = origin - t.A;
            float u = Vector3.Dot(s, h) / det;
            if (u < 0 || u > 1)
                continue;
            var q = Vector3.Cross(s, e);
            float v = Vector3.Dot(direction, q) / det;
            if (v < 0 || u + v > 1)
                continue;
            float d = Vector3.Dot(f, q) / det;
            if (d >= 0 && d <= best)
            {
                best = d;
                found = true;
            }
        }
        return found ? best : null;
    }
}
