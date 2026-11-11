using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

/// <summary>Bounded template placement; never depends on chunk requests or rendering.</summary>
public sealed class SettlementPlan
{
    public SettlementLayout Layout
    {
        get;
    }
    readonly record struct Pad(Vector2 Centre, Vector2 Half, float Height);
    readonly Pad[] pads;
    readonly Dictionary<(int, int), (int Height, int Water)> samples = [];
    readonly Func<int, int, (int Height, int Water)> source;
    readonly Vector3 gate;
    readonly Dictionary<string, int> rejected = [];
    readonly List<SettlementPlot> plots = [];
    readonly List<SettlementRoad> roads = [];
    readonly List<Vector3> rejectedSites = [];
    (int Height, int Water) Sample(int x, int z)
    {
        if (!samples.TryGetValue((x, z), out var s))
            samples[(x, z)] = s = source(x, z);
        return s;
    }
    void Reject(string reason) => rejected[reason] = rejected.GetValueOrDefault(reason) + 1;
    static float Smooth(float x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
    public static Vector2 Half(SettlementPlot p) => Half(SettlementTemplates.Get(p.Template), p.Rotation);
    static Vector2 Half(SettlementTemplate t, int rotation)
    {
        float angle = rotation * MathF.PI / 6, c = MathF.Abs(MathF.Cos(angle)), s = MathF.Abs(MathF.Sin(angle));
        return new((t.Width * c + t.Depth * s) * .5f, (t.Width * s + t.Depth * c) * .5f);
    }
    public SettlementPlan(int seed, string preset, Vector3 gate, Func<int, int, (int Height, int Water)> source, SettlementSettings settings)
    {
        settings.Validate();
        this.source = source;
        this.gate = gate;
        ulong hash = PlanetPlan.Hash(seed, 0, 0, 800);
        int roll = (int)(hash % 100);
        string kind = settings.Type != "auto" ? settings.Type : roll < 35 ? "wilderness" : roll < 65 ? "ruins" : roll < 90 ? "village" : "city";
        int culture = (int)((hash >> 16) % 100);
        string palette = settings.Style != "auto" ? settings.Style : preset == "desert" ? (culture < 80 ? "desert" : "modern") : culture < 35 ? "mega" : culture < 65 ? "seahill" : culture < 85 ? "desert" : "modern";
        List<SettlementSite> sites = [];
        if (kind != "wilderness")
        {
            int siteCount = kind == "ruins" ? 1 + (int)((hash >> 24) % 3) : 1;
            for (int site = 0; site < siteCount; site++)
            {
                int desired = kind == "ruins" ? 1 + (int)((hash >> (28 + site)) % 3) : kind == "village" ? 8 + (int)((hash >> 32) % 13) : 30 + (int)((hash >> 32) % 31);
                PlaceSite(seed, site, kind, palette, desired, sites);
            }
            if (kind is "village" or "city" && (hash >> 40) % 3 == 0)
                PlaceSite(seed, 1, "ruins", palette, 1, sites);
        }
        Layout = new(1, kind, palette, sites.ToArray(), plots.ToArray(), roads.ToArray(), rejected)
        {
            RejectedSites = rejectedSites.ToArray()
        };
        Layout.Validate();
        pads = plots.Select(p => new Pad(new(p.Position.X, p.Position.Z), Half(p) + new Vector2(1), p.Position.Y)).ToArray();
        samples.Clear();
    }
    void PlaceSite(int seed, int site, string kind, string palette, int desired, List<SettlementSite> sites)
    {
        var pool = SettlementTemplates.All.Where(t => t.Palette == palette && (kind == "ruins" ? t.Kind == "ruin" : t.Kind is "building" or "industrial") && t.Width <= 48 && t.Depth <= 48).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
        if (pool.Length == 0)
        {
            Reject("no compatible templates");
            return;
        }
        // Compact blocks use the smaller complete buildings; large source campuses
        // remain available as standalone templates instead of stretching every lot.
        if (kind != "ruins" && palette == "modern")
            pool = pool.Where(t => t.Width <= 14 && t.Depth <= 26 && t.Width >= 7 && t.Depth >= 7).ToArray();
        if (kind == "city" && palette == "desert")
            pool = pool.Where(t => t.Width <= 27).ToArray();
        bool variedHouses = palette is "mega" or "seahill";
        float pitchX = pool.Max(t => variedHouses ? Math.Max(t.Width, Half(t, 1).X * 2) : t.Width) + 6;
        float pitchZ = pool.Max(t => variedHouses ? Math.Max(t.Depth, Half(t, 1).Y * 2) : t.Depth) + 8;
        int attempts = kind == "ruins" ? desired : (int)MathF.Ceiling(desired * 1.6f);
        int columns = kind == "ruins" ? 2 : (int)MathF.Ceiling(MathF.Sqrt(attempts));
        int rows = (attempts + columns - 1) / columns;
        float radius = Math.Max(columns * pitchX, rows * pitchZ) * .5f;
        var candidates = new List<(Vector2 P, float Score, ulong Tie)>();
        for (int z = -WorldDomain.HalfSize+160; z <= WorldDomain.HalfSize-160; z += 64)
            for (int x = -WorldDomain.HalfSize+160; x <= WorldDomain.HalfSize-160; x += 64)
            {
                if (Math.Abs(x) + radius > WorldDomain.HalfSize-22 || Math.Abs(z) + radius > WorldDomain.HalfSize-22 || Vector2.Distance(new(x, z), new(gate.X, gate.Z)) < 48)
                    continue;
                var s = Sample(x, z);
                if (s.Water != int.MinValue)
                    continue;
                float relief = 0;
                foreach (var p in new[] { new Vector2(-12, 0), new(12, 0), new(0, -12), new(0, 12) })
                    relief += Math.Abs(Sample(x + (int)p.X, z + (int)p.Y).Height - s.Height) / 1000f;
                candidates.Add((new(x, z), relief + Vector2.Distance(new(x,z),new(gate.X,gate.Z))*.001f, PlanetPlan.Hash(seed, x, z, 900 + site)));
            }
        foreach (var candidate in candidates.OrderBy(p => p.Score).ThenBy(p => p.Tie).Take(96))
        {
            int before = plots.Count, roadBefore = roads.Count;
            var centre = candidate.P;
            float elevation = Sample((int)centre.X, (int)centre.Y).Height / 1000f;
            if (sites.Any(s => Vector2.Distance(new(s.Centre.X, s.Centre.Z), centre) < radius + 65))
                continue;
            for (int k = 0; k < attempts && plots.Count - before < desired && plots.Count < 90; k++)
            {
                int row = k / columns, col = k % columns;
                var t = pool[(int)(PlanetPlan.Hash(seed, k, site, 901) % (ulong)pool.Length)];
                // Industrial buildings occupy the final rows of a modern city.
                if (kind == "city" && palette == "modern")
                {
                    var district = pool.Where(p => (row >= rows - 2) == (p.Kind == "industrial")).ToArray();
                    if (district.Length > 0)
                        t = district[k % district.Length];
                }
                var pos = centre + new Vector2((col < columns / 2 ? col - columns / 2 : col - columns / 2 + 1) * pitchX, (row - (rows - 1) * .5f) * pitchZ);
                ulong orientation = PlanetPlan.Hash(seed, k, site, 912);
                int rotation = (int)(orientation % 2) * 6;
                if (variedHouses)
                    rotation = (rotation + 12 + (int)((orientation >> 8) % 3) - 1) % 12;
                var half = Half(t, rotation) + Vector2.One;
                if (!PlotSafe(pos, half, out float height))
                    continue;
                var transform = Matrix4x4.CreateRotationY(-rotation * MathF.PI / 6);
                var entrance = t.Entrances[0];
                var offset = Vector3.Transform(new(entrance[0], 0, entrance[2]), transform);
                var outward = Vector3.Transform(-Vector3.UnitZ, transform);
                var door = pos + new Vector2(offset.X, offset.Z);
                float laneZ = pos.Y + MathF.Sign(outward.Z) * (pitchZ / 2);
                // Continue straight out of the rotated doorway before joining its row lane.
                float approach = (laneZ - door.Y) / outward.Z;
                var lane = door + new Vector2(outward.X, outward.Z) * approach;
                var junction = new Vector2(centre.X, lane.Y);
                var main = new Vector2(centre.X, centre.Y - radius + 2);
                var proposed = new[] { (door, lane), (lane, junction), (junction, main) };
                float width = kind == "city" ? 4 : 2;
                List<(Vector2,Vector2)> segments=[];
                bool connected=true;
                foreach(var (a,b) in proposed)
                {
                    var route=Route(a,b,width);
                    if(route is null){connected=false;break;}
                    segments.AddRange(route);
                }
                if (kind != "ruins" && (!connected || segments.Count+roads.Count>256))
                {
                    Reject("road blocked by water or steep terrain");
                    continue;
                }
                var p = new SettlementPlot(site * 100 + k, site, t.Id, new(pos.X, height, pos.Y), rotation);
                plots.Add(p);
                if (kind != "ruins")
                    foreach (var (a, b) in segments)
                    {
                        if (Vector2.DistanceSquared(a, b) < .01f)
                            continue;
                        float ah = Vector2.DistanceSquared(a, door) < .01f ? height : Sample((int)MathF.Round(a.X), (int)MathF.Round(a.Y)).Height / 1000f;
                        float bh = Sample((int)MathF.Round(b.X), (int)MathF.Round(b.Y)).Height / 1000f;
                        var road = new SettlementRoad(new(a.X, ah, a.Y), new(b.X, bh, b.Y), width, kind == "city");
                        if (!roads.Contains(road) && roads.Count < 256)
                            roads.Add(road);
                    }
            }
            int count = plots.Count - before;
            if (count >= Math.Min(desired, kind == "ruins" ? 1 : kind == "village" ? 8 : 20))
            {
                // A short wide road segment forms the square and joins the main spine.
                if (kind != "ruins")
                {
                    var pa = centre - new Vector2(Math.Min(8, pitchX * .35f), 0);
                    var pb = centre + new Vector2(Math.Min(8, pitchX * .35f), 0);
                    if (RoadSafe(pa, pb, 8))
                        roads.Add(new(new(pa.X, Sample((int)pa.X, (int)pa.Y).Height / 1000f, pa.Y), new(pb.X, Sample((int)pb.X, (int)pb.Y).Height / 1000f, pb.Y), 8, kind == "city"));
                }
                // Small civic marker beside the square, outside the through route.
                var landmarks = SettlementTemplates.All.Where(t => t.Kind == "landmark" && t.Palette == palette && t.Width < 8 && t.Depth < 8).OrderBy(t => t.Id).ToArray();
                if (kind != "ruins" && landmarks.Length > 0)
                {
                    var landmark = landmarks[(int)(PlanetPlan.Hash(seed, site, 0, 914) % (ulong)landmarks.Length)];
                    var lp = centre + new Vector2(pitchX * .5f, 0);
                    if (PlotSafe(lp, new(landmark.Width * .5f + 1, landmark.Depth * .5f + 1), out var lh) && roads.All(r => RoadDistance(r, lp, out _) > r.Width * .5f + Math.Max(landmark.Width, landmark.Depth) * .5f + 1))
                        plots.Add(new(site * 100 + 99, site, landmark.Id, new(lp.X, lh, lp.Y), 0));
                }
                sites.Add(new(site, new(centre.X, elevation, centre.Y), kind, desired, count));
                return;
            }
            plots.RemoveRange(before, count);
            roads.RemoveRange(roadBefore, roads.Count - roadBefore);
            Reject("insufficient connected dry plots");
            if (rejectedSites.Count < 32)
                rejectedSites.Add(new(centre.X, elevation, centre.Y));
        }
        Reject("site search exhausted");
    }
    bool PlotSafe(Vector2 p, Vector2 half, out float height)
    {
        height = 0;
        if (Math.Abs(p.X) + half.X + 6 > WorldDomain.HalfSize-7 || Math.Abs(p.Y) + half.Y + 6 > WorldDomain.HalfSize-7)
        {
            Reject("map boundary");
            return false;
        }
        if (Vector2.Distance(p, new(gate.X, gate.Z)) - half.Length() < 40)
        {
            Reject("gate reservation");
            return false;
        }
        foreach (var old in plots)
        {
            var h = Half(old) + half + new Vector2(3);
            if (Math.Abs(p.X - old.Position.X) < h.X && Math.Abs(p.Y - old.Position.Z) < h.Y)
            {
                Reject("overlap");
                return false;
            }
        }
        float lo = float.MaxValue, hi = float.MinValue;
        for (int z = (int)MathF.Floor(p.Y - half.Y - 16); z <= p.Y + half.Y + 16; z++)
            for (int x = (int)MathF.Floor(p.X - half.X - 16); x <= p.X + half.X + 16; x++)
            {
                var s = Sample(x, z);
                if (s.Water != int.MinValue)
                {
                    Reject("water buffer");
                    return false;
                }
                if (Math.Abs(x - p.X) <= half.X + 1 && Math.Abs(z - p.Y) <= half.Y + 1)
                {
                    lo = Math.Min(lo, s.Height / 1000f);
                    hi = Math.Max(hi, s.Height / 1000f);
                }
            }
        if (hi - lo > 4)
        {
            Reject("cut/fill exceeds 2 m");
            return false;
        }
        height = (hi + lo) * .5f;
        return true;
    }
    bool RoadSafe(Vector2 a, Vector2 b, float width)
    {
        var direction = b - a;
        float length = direction.Length();
        if (length < .01f)
            return true;
        var n = new Vector2(-direction.Y, direction.X) / length;
        float previous = Sample((int)a.X, (int)a.Y).Height / 1000f;
        for (float distance = 0; distance <= length + 1; distance += 1)
        {
            var p = Vector2.Lerp(a, b, Math.Min(1, distance / length));
            if (Vector2.Distance(p, new(gate.X, gate.Z)) < 40)
                return false;
            for (float side = -width / 2 - 16; side <= width / 2 + 16; side += 1)
            {
                var q = p + n * side;
                if (Sample((int)MathF.Round(q.X), (int)MathF.Round(q.Y)).Water != int.MinValue)
                    return false;
            }
            float h = Sample((int)MathF.Round(p.X), (int)MathF.Round(p.Y)).Height / 1000f;
            if (Math.Abs(h - previous) > .6f)
                return false;
            previous = h;
        }
        return true;
    }
    List<(Vector2,Vector2)>? Route(Vector2 a,Vector2 b,float width)
    {
        if(RoadSafe(a,b,width))return [(a,b)];
        if(Vector2.DistanceSquared(a,b)<4)return null;
        var d=b-a;var side=Vector2.Normalize(new Vector2(-d.Y,d.X));
        // Deterministic bounded alternatives preserve the local street network while
        // bending around a wet depression or a steep shoulder. Never grade a corridor.
        foreach(int offset in new[]{16,-16,32,-32,64,-64})
        {
            var u=Vector2.Lerp(a,b,.25f)+side*offset;var v=Vector2.Lerp(a,b,.75f)+side*offset;
            if(Math.Abs(u.X)>WorldDomain.HalfSize-24||Math.Abs(u.Y)>WorldDomain.HalfSize-24||Math.Abs(v.X)>WorldDomain.HalfSize-24||Math.Abs(v.Y)>WorldDomain.HalfSize-24)continue;
            if(plots.Any(p=> RoadDistance(new(new(u.X,0,u.Y),new(v.X,0,v.Y),width,false),new(p.Position.X,p.Position.Z),out _) < Half(p).Length()+width))continue;
            if(RoadSafe(a,u,width)&&RoadSafe(u,v,width)&&RoadSafe(v,b,width))return [(a,u),(u,v),(v,b)];
        }
        return null;
    }
    static float RoadDistance(SettlementRoad r, Vector2 p, out float t)
    {
        var a = new Vector2(r.A.X, r.A.Z);
        var d = new Vector2(r.B.X - r.A.X, r.B.Z - r.A.Z);
        t = Math.Clamp(Vector2.Dot(p - a, d) / Math.Max(.001f, d.LengthSquared()), 0, 1);
        return Vector2.Distance(p, a + d * t);
    }
    public float Height(float x, float z, float original)
    {
        var p = new Vector2(x, z);
        float result = original;
        // A neighbouring plot's feather must never lift an already-flat floor.
        foreach (var pad in pads)
            if (Math.Abs(x - pad.Centre.X) <= pad.Half.X && Math.Abs(z - pad.Centre.Y) <= pad.Half.Y)
                return Math.Clamp(pad.Height, original - 2, original + 2);
        foreach (var road in Layout.Roads)
        {
            float d = RoadDistance(road, p, out float t);
            float weight = 1 - Smooth((d - road.Width / 2) / 3);
            if (weight > 0)
                result += Math.Clamp(float.Lerp(road.A.Y, road.B.Y, t) - result, -2, 2) * weight;
        }
        foreach (var pad in pads)
        {
            var q = Vector2.Max(Vector2.Abs(p - pad.Centre) - pad.Half, Vector2.Zero);
            float w = 1 - Smooth(q.Length() / 6);
            if (w > 0)
                result = float.Lerp(result, pad.Height, w);
        }
        return Math.Clamp(result, original - 2, original + 2);
    }
    // Continuous coverage stays linear through vertex interpolation. The retained
    // planet outcome selects dirt lanes versus city paving for shading/footsteps.
    public byte Road(float x, float z)
    {
        float coverage = 0;
        foreach (var r in Layout.Roads)
        {
            float d = RoadDistance(r, new(x, z), out _);
            coverage = Math.Max(coverage, 1 - Smooth((d - r.Width / 2) / .65f));
        }
        return (byte)(coverage * 255);
    }
    public bool PlotCoverage(float x, float z) => pads.Any(p => Math.Abs(x - p.Centre.X) <= p.Half.X + 1 && Math.Abs(z - p.Centre.Y) <= p.Half.Y + 1);
    public bool Excludes(float x, float z, float margin = 0) => pads.Any(p => Math.Abs(x - p.Centre.X) < p.Half.X + margin && Math.Abs(z - p.Centre.Y) < p.Half.Y + margin) || Layout.Roads.Any(r => RoadDistance(r, new(x, z), out _) < r.Width / 2 + margin);
}
