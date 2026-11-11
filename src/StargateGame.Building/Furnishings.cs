using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using StargateGame.Shared;

namespace StargateGame.Building;

public sealed record FurnishingSurface(float Height, float[] Bounds);
public sealed record FurnishingAsset(string Id, string Category, bool Outdoor, float Width, float Depth, float Height, float[] Facing, string Access, FurnishingSurface[] Surfaces);
public sealed record FurnishingInterior(string Template, float[] Floor, float FloorHeight, bool Outdoor, float[][] Entrances, float[][] DoorSweeps, float[][] Openings, float[][] Exclusions);
public sealed record FurnishingPlacement(string Asset, Vector3 Position, int Rotation);
public sealed record FurnishedRoom(string Role, FurnishingPlacement[] Pieces);

/// <summary>Metre-space furnishing data, separate from building source imports.</summary>
public static class FurnishingCatalog
{
    static readonly byte[] assets = Read("furnishing-catalog.json"), interiors = Read("furnishing-interiors.json");
    public static string Hash
    {
        get;
    } = Convert.ToHexString(SHA256.HashData(assets.Concat(interiors).ToArray()));
    static readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    public static IReadOnlyList<FurnishingAsset> Assets
    {
        get;
    } = JsonSerializer.Deserialize<FurnishingAsset[]>(assets, json)!;
    public static IReadOnlyList<FurnishingInterior> Interiors
    {
        get;
    } = JsonSerializer.Deserialize<FurnishingInterior[]>(interiors, json)!;
    static readonly Dictionary<string, FurnishingAsset> byId = Assets.ToDictionary(a => a.Id);
    static readonly Dictionary<string, FurnishingInterior> byTemplate = Interiors.ToDictionary(a => a.Template);
    internal static byte[] Read(string name)
    {
        using var stream = typeof(FurnishingCatalog).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidDataException("Missing furnishing content. Run tools/import_furnishings.py before building.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
    public static FurnishingAsset Get(string id) => byId[id];
    public static FurnishingInterior? Interior(string template) => byTemplate.GetValueOrDefault(template);
}

/// <summary>Deterministic, bounded group placement with a one-metre circulation test.</summary>
public static class FurnishingLayouts
{
    public const int Revision = 1, MaxPerPlot = 24;
    const float AccessClearance = .70f, RouteRadius = .5f, Cell = .2f;
    public readonly record struct Rect(float X, float Z, float Right, float Back)
    {
        public static Rect From(float[] a) => new(a[0], a[1], a[2], a[3]);
        public Rect Inflate(float margin) => new(X - margin, Z - margin, Right + margin, Back + margin);
        public bool Contains(Vector2 p) => p.X >= X && p.X <= Right && p.Y >= Z && p.Y <= Back;
        public bool Contains(Rect r) => r.X >= X && r.Right <= Right && r.Z >= Z && r.Back <= Back;
        public bool Overlaps(Rect r) => X < r.Right && Right > r.X && Z < r.Back && Back > r.Z;
    }
    // Explicit integer mixer: no runtime-randomized string hash or global RNG.
    static ulong Mix(ulong x)
    {
        x += 0x9e3779b97f4a7c15UL;
        x = (x ^ (x >> 30)) * 0xbf58476d1ce4e5b9UL;
        x = (x ^ (x >> 27)) * 0x94d049bb133111ebUL;
        return x ^ (x >> 31);
    }
    static ulong Seed(int seed, int plot) => Mix((uint)seed ^ ((ulong)(uint)plot << 32) ^ Revision);
    static Vector2 Rotate(Vector2 p, int rotation) => rotation switch { 0 => p, 3 => new(-p.Y, p.X), 6 => -p, 9 => new(p.Y, -p.X), _ => throw new ArgumentOutOfRangeException(nameof(rotation)) };
    public static Rect Footprint(FurnishingPlacement p)
    {
        var a = FurnishingCatalog.Get(p.Asset);
        bool turn = p.Rotation % 6 != 0;
        float w = (turn ? a.Depth : a.Width) / 2, d = (turn ? a.Width : a.Depth) / 2;
        return new(p.Position.X - w, p.Position.Z - d, p.Position.X + w, p.Position.Z + d);
    }
    public static Vector2 Access(FurnishingPlacement p)
    {
        var a = FurnishingCatalog.Get(p.Asset);
        var local = a.Access switch
        {
            "side" => new Vector2(a.Width / 2 + AccessClearance, 0),
            "back" => new Vector2(0, a.Depth / 2 + AccessClearance),
            _ => new Vector2(0, -a.Depth / 2 - AccessClearance)
        };
        return Rotate(local, p.Rotation) + new Vector2(p.Position.X, p.Position.Z);
    }
    public static IReadOnlyDictionary<int, string> AssignRoles(SettlementLayout layout, int seed)
    {
        Dictionary<int, string> result = [];
        foreach (var group in layout.Plots.Where(p => FurnishingCatalog.Interior(p.Template) is not null).GroupBy(p => p.Site))
        {
            var houses = group.Where(p => !FurnishingCatalog.Interior(p.Template)!.Outdoor)
                .OrderByDescending(p =>
                {
                    var f = FurnishingCatalog.Interior(p.Template)!.Floor;
                    return (f[2] - f[0]) * (f[3] - f[1]);
                })
                .ThenBy(p => Seed(seed, p.Id)).ThenBy(p => p.Id).ToArray();
            foreach (var p in group)
                result[p.Id] = FurnishingCatalog.Interior(p.Template)!.Outdoor ? "courtyard" : "home";
            if (houses.Length >= 4)
                result[houses[0].Id] = "dining";
            if (houses.Length >= 6)
                result[houses[1].Id] = "shop";
            if (houses.Length >= 8)
                result[houses[2].Id] = "storage";
        }
        return result;
    }
    public static FurnishedRoom Create(SettlementPlot plot, int seed, string role)
    {
        var interior = FurnishingCatalog.Interior(plot.Template);
        if (interior is null)
            return new(role, []);
        var floor = Rect.From(interior.Floor);
        List<FurnishingPlacement> placed = [];
        ulong random = Seed(seed, plot.Id);
        FurnishingPlacement Piece(string id, float x, float z, int r = 0) => new("furnishing-" + id, new(x, interior.FloorHeight, z), r);
        bool Add(FurnishingPlacement[] group)
        {
            if (placed.Count + group.Length > MaxPerPlot)
                return false;
            var all = placed.Concat(group).ToArray();
            if (!Fits(interior, all) || !Reachable(interior, all))
                return false;
            placed.AddRange(group);
            return true;
        }
        // Prefer back and side walls. The front wall contains the entrance/windows.
        bool AlongWall(string id, int salt, Func<bool>? complete = null)
        {
            var asset = FurnishingCatalog.Get("furnishing-" + id);
            int[] walls = [0, 1, 2];
            if ((Mix(random + (uint)salt) & 1) != 0)
                walls = [0, 2, 1];
            foreach (int wall in walls)
            {
                int r = wall == 0 ? 0 : wall == 1 ? 3 : 9;
                bool turn = r % 6 != 0;
                float hx = (turn ? asset.Depth : asset.Width) / 2, hz = (turn ? asset.Width : asset.Depth) / 2;
                float[] fractions = [0, 1, .2f, .8f, .5f];
                if ((Mix(random + (uint)salt + 15) & 1) != 0)
                    fractions = [1, 0, .8f, .2f, .5f];
                foreach (float f in fractions)
                {
                    float x = wall == 0 ? float.Lerp(floor.X + hx + .12f, floor.Right - hx - .12f, f) : wall == 1 ? floor.X + hx + .12f : floor.Right - hx - .12f;
                    float z = wall == 0 ? floor.Back - hz - .12f : float.Lerp(floor.Z + hz + .12f, floor.Back - hz - .12f, f);
                    int before = placed.Count;
                    if (Add([Piece(id, x, z, r)]))
                    {
                        if (complete is null || complete())
                            return true;
                        placed.RemoveRange(before, placed.Count - before);
                    }
                }
            }
            return false;
        }
        bool Dining(bool large, int salt)
        {
            string table = large ? "table-large-set" : "table-small-set";
            var a = FurnishingCatalog.Get("furnishing-" + table);
            var chair = FurnishingCatalog.Get("furnishing-chair");
            List<Vector2> candidates = [];
            foreach (float fx in new[] { .25f, .75f, .5f })
                foreach (float fz in new[] { .25f, .75f, .35f, .65f, .5f })
                    candidates.Add(new(float.Lerp(floor.X, floor.Right, fx), float.Lerp(floor.Z, floor.Back, fz)));
            int start = (int)(Mix(random + (uint)salt) % (ulong)candidates.Count);
            foreach (int rotation in new[] { 0, 3, 6, 9 })
                for (int k = 0; k < candidates.Count; k++)
                {
                    var p = candidates[(k + start) % candidates.Count];
                    List<FurnishingPlacement> group = [Piece(table, p.X, p.Y, rotation)];
                    foreach (int side in new[] { -1, 1 })
                    {
                        if (large && side == 1)
                        {
                            var bench = FurnishingCatalog.Get("furnishing-bench");
                            var b = Rotate(new(0, a.Depth / 2 + bench.Depth / 2 + .15f), rotation) + p;
                            group.Add(Piece("bench", b.X, b.Y, rotation));
                        }
                        else
                            foreach (float x in large ? new[] { -.55f, .55f } : new[] { 0f })
                            {
                                var c = Rotate(new(x, side * (a.Depth / 2 + chair.Depth / 2 + .15f)), rotation) + p;
                                group.Add(Piece("chair", c.X, c.Y, (rotation + (side < 0 ? 6 : 0)) % 12));
                            }
                    }
                    if (Add(group.ToArray()))
                        return true;
                }
            return false;
        }
        switch (role)
        {
            case "home":
                // The essential living groups must fit together. Backtrack the bed
                // when its first valid position prevents a usable dining area.
                if (!AlongWall((random & 1) == 0 ? "bed-single" : "bed-double", 1, () => Dining(false, 3)))
                    AlongWall("bed-single", 2, () => Dining(false, 3));
                AlongWall("chest", 4);
                AlongWall("cupboard", 5);
                break;
            case "dining":
                if (!Dining(true, 6))
                    Dining(false, 7);
                Dining(false, 8);
                AlongWall("shelf", 9);
                AlongWall("barrel", 10);
                break;
            case "shop":
                AlongWall("counter", 11);
                AlongWall("shelf", 12);
                AlongWall("display", 13);
                AlongWall("chest", 14);
                break;
            case "storage":
                for (int i = 0; i < 3; i++)
                {
                    AlongWall("shelf", 20 + i);
                    AlongWall("chest", 30 + i);
                    AlongWall("barrel", 40 + i);
                }
                break;
            case "courtyard":
                AlongWall("counter", 50);
                AlongWall("display", 51);
                Dining(true, 52);
                AlongWall("shelf", 53);
                AlongWall("chest", 54);
                AlongWall("barrel", 55);
                AlongWall("barrel", 56);
                break;
            default:
                throw new ArgumentException("Unknown furnishing role.", nameof(role));
        }
        return new(role, placed.ToArray());
    }
    public static bool Fits(FurnishingInterior interior, IReadOnlyList<FurnishingPlacement> pieces)
    {
        var floor = Rect.From(interior.Floor).Inflate(-.08f);
        for (int i = 0; i < pieces.Count; i++)
        {
            var p = pieces[i];
            var asset = FurnishingCatalog.Get(p.Asset);
            var rect = Footprint(p);
            if (interior.Outdoor && !asset.Outdoor || Math.Abs(p.Position.Y - interior.FloorHeight) > .001f || !floor.Contains(rect))
                return false;
            if (interior.DoorSweeps.Concat(interior.Exclusions).Any(e => rect.Inflate(.06f).Overlaps(Rect.From(e))))
                return false;
            if (asset.Height > .9f && interior.Openings.Any(e => rect.Overlaps(Rect.From(e))))
                return false;
            for (int j = 0; j < i; j++)
                if (rect.Inflate(.06f).Overlaps(Footprint(pieces[j])))
                    return false;
        }
        return true;
    }
    public static bool Reachable(FurnishingInterior interior, IReadOnlyList<FurnishingPlacement> pieces)
    {
        var floor = Rect.From(interior.Floor).Inflate(-RouteRadius);
        int width = (int)MathF.Floor((floor.Right - floor.X) / Cell) + 1, depth = (int)MathF.Floor((floor.Back - floor.Z) / Cell) + 1;
        if (width < 1 || depth < 1 || width * depth > 100000)
            return false;
        var blocked = pieces.Select(p => Footprint(p).Inflate(RouteRadius)).Concat(interior.Exclusions.Select(e => Rect.From(e).Inflate(RouteRadius))).ToArray();
        var open = new bool[width * depth];
        var seen = new bool[open.Length];
        Queue<int> queue = new();
        Vector2 Point(int i) => new(floor.X + (i % width) * Cell, floor.Z + (i / width) * Cell);
        for (int i = 0; i < open.Length; i++)
            open[i] = !blocked.Any(r => r.Contains(Point(i)));
        int Nearest(Vector2 p)
        {
            if (!floor.Contains(p) || blocked.Any(r => r.Contains(p)))
                return -1;
            int best = -1;
            float distance = .25f * .25f;
            for (int i = 0; i < open.Length; i++)
                if (open[i])
                {
                    float d = Vector2.DistanceSquared(Point(i), p);
                    if (d < distance)
                    {
                        distance = d;
                        best = i;
                    }
                }
            return best;
        }
        var entrances = interior.Entrances.Select(e => new Vector2(Math.Clamp(e[0], floor.X, floor.Right), Math.Clamp(e[2], floor.Z, floor.Back))).ToArray();
        if (entrances.Length == 0)
            return false;
        int first = Nearest(entrances[0]);
        if (first < 0)
            return false;
        queue.Enqueue(first);
        seen[first] = true;
        while (queue.TryDequeue(out int cell))
        {
            int x = cell % width, z = cell / width;
            foreach (var (dx, dz) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) })
            {
                int nx = x + dx, nz = z + dz;
                if (nx < 0 || nx >= width || nz < 0 || nz >= depth)
                    continue;
                int n = nz * width + nx;
                if (open[n] && !seen[n])
                {
                    seen[n] = true;
                    queue.Enqueue(n);
                }
            }
        }
        foreach (var target in entrances.Concat(pieces.Select(Access)))
        {
            int cell = Nearest(target);
            if (cell < 0 || !seen[cell])
                return false;
        }
        // Keep the main room/courtyard centre connected to the entrance as well.
        int middle = Nearest(new((floor.X + floor.Right) / 2, (floor.Z + floor.Back) / 2));
        return middle >= 0 && seen[middle];
    }
}
