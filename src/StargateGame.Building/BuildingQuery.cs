using System.Collections.Concurrent;
using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Building;

// Immutable local BVHs are shared by every instance of a prefab. Only edited
// structures replace their world entries; queries never assemble triangle soups.
public sealed class BuildingQuery
{
    sealed class Tree
    {
        public readonly BuildBounds Bounds;
        readonly Tree? left, right;
        readonly BuildTriangle[] triangles = [];
        public Tree(BuildTriangle[] source)
        {
            Bounds = BuildingGeometry.Bounds(source);
            if (source.Length <= 12) { triangles = source; return; }
            var size = Bounds.Max - Bounds.Min;
            int axis = size.X > size.Y ? (size.X > size.Z ? 0 : 2) : (size.Y > size.Z ? 1 : 2);
            var sorted = source.OrderBy(t => Component(t.A + t.B + t.C, axis)).ToArray();
            left = new(sorted[..(sorted.Length / 2)]); right = new(sorted[(sorted.Length / 2)..]);
        }
        public bool Ray(Vector3 origin, Vector3 direction, ref float best, ref int tested)
        {
            if (!RayBounds(origin, direction, Bounds, best, out _)) return false;
            bool hit = false;
            if (left is not null && right is not null)
            {
                RayBounds(origin, direction, left.Bounds, best, out float a);
                RayBounds(origin, direction, right.Bounds, best, out float b);
                var first = a <= b ? left : right; var second = a <= b ? right : left;
                hit = first.Ray(origin, direction, ref best, ref tested);
                return second.Ray(origin, direction, ref best, ref tested) || hit;
            }
            foreach (var t in triangles)
            {
                tested++;
                var e1 = t.B-t.A; var e2 = t.C-t.A; var h = Vector3.Cross(direction,e2);
                float det = Vector3.Dot(e1,h);
                if (Math.Abs(det)<1e-8f) continue; // Two-sided, including the door leaf.
                var s = origin-t.A; float u=Vector3.Dot(s,h)/det;
                if(u<0||u>1) continue;
                var q=Vector3.Cross(s,e1); float v=Vector3.Dot(direction,q)/det;
                if(v<0||u+v>1) continue;
                float d=Vector3.Dot(e2,q)/det;
                if(d>=0&&d<best) { best=d; hit=true; }
            }
            return hit;
        }
    }
    sealed record Entry(BuildingPiece Piece, Tree Tree, Matrix4x4 Transform, Matrix4x4 Inverse, BuildBounds Bounds);
    static readonly ConcurrentDictionary<string, Tree> prefabs = new();
    readonly Dictionary<Guid,List<Entry>> entries = [];
    readonly Dictionary<(int,int,int),List<Entry>> cells = [];
    readonly HashSet<Entry> seen = [];
    readonly List<(Entry Entry,float Distance)> candidates = [];
    WorldChanges? state;
    public int Candidates { get; private set; }
    public int Triangles { get; private set; }
    static float Component(Vector3 p,int axis) => axis==0?p.X:axis==1?p.Y:p.Z;
    static int Cell(float v) => (int)MathF.Floor(v/8);
    static IEnumerable<(int,int,int)> Cells(BuildBounds b)
    {
        for(int x=Cell(b.Min.X);x<=Cell(b.Max.X);x++)
        for(int y=Cell(b.Min.Y);y<=Cell(b.Max.Y);y++)
        for(int z=Cell(b.Min.Z);z<=Cell(b.Max.Z);z++) yield return (x,y,z);
    }
    public static BuildBounds TransformBounds(BuildBounds b,Matrix4x4 m)
    {
        var lo=new Vector3(float.PositiveInfinity); var hi=new Vector3(float.NegativeInfinity);
        for(int i=0;i<8;i++) { var p=Vector3.Transform(new((i&1)==0?b.Min.X:b.Max.X,(i&2)==0?b.Min.Y:b.Max.Y,(i&4)==0?b.Min.Z:b.Max.Z),m); lo=Vector3.Min(lo,p);hi=Vector3.Max(hi,p); }
        return new(lo,hi);
    }
    public static bool RayBounds(Vector3 o,Vector3 d,BuildBounds b,float limit,out float near)
    {
        near=0; float far=limit;
        for(int i=0;i<3;i++)
        {
            float v=Component(d,i), p=Component(o,i), lo=Component(b.Min,i),hi=Component(b.Max,i);
            if(Math.Abs(v)<1e-8f) { if(p<lo||p>hi) { near=float.PositiveInfinity;return false; } continue; }
            float a=(lo-p)/v,c=(hi-p)/v; if(a>c)(a,c)=(c,a);
            near=Math.Max(near,a);far=Math.Min(far,c);
            if(near>far) {near=float.PositiveInfinity;return false;}
        }
        return true;
    }
    public void Update(WorldChanges next)
    {
        if(ReferenceEquals(state,next))return;
        var changed=state is null||state.LocationId!=next.LocationId ? next.Structures.Select(s=>s.Id).Concat(entries.Keys).ToHashSet()
            : next.Pieces.Except(state.Pieces).Concat(state.Pieces.Except(next.Pieces)).Select(p=>p.StructureId)
                .Concat(next.Structures.Except(state.Structures).Concat(state.Structures.Except(next.Structures)).Select(s=>s.Id)).ToHashSet();
        foreach(var id in changed)
        {
            if(entries.Remove(id,out var old)) foreach(var e in old) foreach(var cell in Cells(e.Bounds))
                if(cells.TryGetValue(cell,out var list)) {list.Remove(e);if(list.Count==0)cells.Remove(cell);}
            var s=next.Structures.FirstOrDefault(s=>s.Id==id); if(s is null)continue;
            var added=new List<Entry>(); entries[id]=added;
            void Add(BuildingPiece p,Tree tree,Matrix4x4 transform)
            {
                if(!float.IsFinite(tree.Bounds.Min.X))return;
                Matrix4x4.Invert(transform,out var inv);
                var e=new Entry(p,tree,transform,inv,TransformBounds(tree.Bounds,transform)); added.Add(e);
                foreach(var cell in Cells(e.Bounds)) {if(!cells.TryGetValue(cell,out var list))cells[cell]=list=[];list.Add(e);}
            }
            foreach(var p in next.Pieces.Where(p=>p.StructureId==id))
            {
                if(p.Kind!=PieceKind.Prefab||BuildingBaseCourses.ReplacesOwner(next,p.Id))
                    Add(p,new(BuildingGeometry.Mesh(next,p)),Matrix4x4.Identity);
                else
                {
                    var tree=prefabs.GetOrAdd(p.Prefab,key=>new(BuildingPrefabs.Get(key).Triangles.Select(t=>new BuildTriangle(new(t[0][0],t[0][1],t[0][2]),new(t[1][0],t[1][1],t[1][2]),new(t[2][0],t[2][1],t[2][2]))).ToArray()));
                    Add(p,tree,BuildingPrefabs.Transform(s,p));
                    var derived=BuildingCorners.OwnedMesh(next,p.Id).Concat(BuildingBaseCourses.OwnedMesh(next,p.Id)).ToArray();
                    if(derived.Length>0)Add(p,new(derived),Matrix4x4.Identity);
                }
            }
        }
        state=next;
    }
    public (BuildingPiece? Piece,Vector3 Point) Ray(Vector3 origin,Vector3 direction,float distance=8,Guid ignore=default)
    {
        seen.Clear(); candidates.Clear(); Triangles=0;
        var end=origin+direction*distance;
        foreach(var cell in Cells(new(Vector3.Min(origin,end),Vector3.Max(origin,end))))
            if(cells.TryGetValue(cell,out var list))foreach(var e in list)
                if(e.Piece.Id!=ignore&&seen.Add(e)&&RayBounds(origin,direction,e.Bounds,distance,out float near))candidates.Add((e,near));
        candidates.Sort((a,b)=>a.Distance.CompareTo(b.Distance)); Candidates=candidates.Count;
        BuildingPiece? found=null; int tested=0;
        foreach(var (e,near) in candidates)
        {
            if(near>distance)break;
            if(e.Tree.Ray(Vector3.Transform(origin,e.Inverse),Vector3.TransformNormal(direction,e.Inverse),ref distance,ref tested))found=e.Piece;
        }
        Triangles=tested; return(found,origin+direction*distance);
    }
    public BuildingPiece[] Overlap(BuildBounds bounds)
    {
        HashSet<Guid> ids=[]; List<BuildingPiece> result=[];
        foreach(var cell in Cells(bounds)) if(cells.TryGetValue(cell,out var list)) foreach(var e in list)
            if(e.Bounds.Intersects(bounds,-.001f)&&ids.Add(e.Piece.Id))result.Add(e.Piece);
        return result.ToArray();
    }
}
