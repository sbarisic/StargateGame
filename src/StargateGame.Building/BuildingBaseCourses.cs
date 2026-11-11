using System.Numerics;
using System.Runtime.CompilerServices;
using StargateGame.Shared;
namespace StargateGame.Building;

// Foundation extensions are derived from supported wall spans, never saved pieces.
public static class BuildingBaseCourses
{
    sealed record Part(Guid Structure, Guid Owner, BuildBounds Box, Matrix4x4 Transform);
    sealed record Result(Dictionary<Guid, BuildTriangle[]> Owners, HashSet<Guid> Replaced);
    static readonly ConditionalWeakTable<WorldChanges,Result> cache = new();
    public static BuildTriangle[] OwnedMesh(WorldChanges state, Guid owner) => cache.GetValue(state,Build).Owners.GetValueOrDefault(owner) ?? [];
    public static bool ReplacesOwner(WorldChanges state, Guid owner) => cache.GetValue(state,Build).Replaced.Contains(owner);
    public static BuildTriangle[] Mesh(WorldChanges state) => cache.GetValue(state,Build).Owners.Values.SelectMany(x=>x).ToArray();
    static Matrix4x4 Local(BuildingPiece p) => Matrix4x4.CreateRotationY(-p.Rotation*MathF.PI/6)*Matrix4x4.CreateTranslation(p.Offset);
    static Vector3 V(float[] p) => new(p[0],p[1],p[2]);
    static bool Wall(BuildingPiece p) => p.Kind == PieceKind.Prefab && (BuildingPrefabs.Get(p.Prefab).Role is "wall" or "window" or "doorway" or "corner" || p.Prefab=="kit-corner-exterior-brick");
    static BuildingPrefab Shell(BuildingPiece p) => BuildingPrefabs.Get(p.Prefab switch
    {
        "kit-assembled-window" => "kit-wall-unevenbrick-window-wide-flat",
        "kit-assembled-door" => "kit-wall-unevenbrick-door-flat",
        _ => p.Prefab
    });

    // Clip the supported wall span to each foundation's actual footprint.
    static bool SupportedSpan(Vector3 a, Vector3 b, BuildingPrefab foundation, out float start, out float end)
    {
        start = 0; end = 1;
        for (int axis=0;axis<2;axis++)
        {
            float x = axis==0?a.X:a.Z, delta=axis==0?b.X-a.X:b.Z-a.Z;
            float min=foundation.Min[axis==0?0:2]-.001f, max=foundation.Max[axis==0?0:2]+.001f;
            if (Math.Abs(delta)<1e-6f) { if (x<min||x>max) return false; continue; }
            float u=(min-x)/delta,v=(max-x)/delta;
            start=Math.Max(start,Math.Min(u,v)); end=Math.Min(end,Math.Max(u,v));
        }
        return end-start>.001f;
    }

    static Result Build(WorldChanges state)
    {
        List<Part> parts=[];
        HashSet<Guid> replaced=[];
        foreach (var structure in state.Structures)
        {
            var pieces=state.Pieces.Where(p=>p.StructureId==structure.Id).ToArray();
            var walls=pieces.Where(Wall).ToArray();
            foreach (var house in pieces.Where(p=>p.Kind==PieceKind.Prefab && BuildingPrefabs.Get(p.Prefab).BaseCourses is not null))
                foreach (var box in BuildingPrefabs.Get(house.Prefab).BaseCourses!)
                    parts.Add(new(structure.Id,house.Id,new(V(box[0]),V(box[1])),Local(house)));
            foreach (var foundation in pieces.Where(p=>p.Kind==PieceKind.Prefab && BuildingPrefabs.Get(p.Prefab).Role=="foundation"))
            {
                var fd=BuildingPrefabs.Get(foundation.Prefab);
                var fm=Local(foundation);
                Matrix4x4.Invert(fm,out var inverse);
                int first=parts.Count;
                foreach (var wall in walls)
                {
                    // Only walls actually standing on this foundation plane.
                    float top=foundation.Offset.Y+fd.Max[1];
                    if (Math.Abs(wall.Offset.Y-top)>.03f) continue;
                    var shell=Shell(wall);
                    var wm=Local(wall);
                    float left=shell.Min[0],right=shell.Max[0];
                    var a=Vector3.Transform(Vector3.Transform(new(left,0,.04f),wm),inverse);
                    var b=Vector3.Transform(Vector3.Transform(new(right,0,.04f),wm),inverse);
                    if (!SupportedSpan(a,b,fd,out float start,out float end)) continue;
                    float lo=left+(right-left)*start,hi=left+(right-left)*end;
                    float Extension(float x)
                    {
                        var point=Vector3.Transform(new Vector3(x,0,0),wm);
                        return walls.Where(p=>p.Id!=wall.Id).Any(p=>
                        {
                            var other=Shell(p); var om=Local(p);
                            return Math.Abs(MathF.Sin((p.Rotation-wall.Rotation)*MathF.PI/6))>.99f &&
                                new[]{other.Min[0],other.Max[0]}.Any(e=>Vector3.DistanceSquared(point,Vector3.Transform(new Vector3(e,0,0),om))<.00001f);
                        }) ? Math.Max(Math.Abs(shell.Min[2]),Math.Abs(shell.Max[2])) : 0;
                    }
                    if (start<.001f) lo-=Extension(left);
                    if (end>.999f) hi+=Extension(right);
                    float bottom=foundation.Offset.Y+fd.Min[1]-wall.Offset.Y;
                    float upper=top-wall.Offset.Y;
                    void Add(float x,float y)
                    {
                        x=Math.Max(x,lo); y=Math.Min(y,hi);
                        if (y-x>.001f) parts.Add(new(structure.Id,foundation.Id,new(new(x,bottom,shell.Min[2]),new(y,upper,shell.Max[2])),wm));
                    }
                    // The footing continues under the opening at the existing
                    // foundation top; it must not leave a hollow threshold.
                    Add(lo,hi);
                }
                if (parts.Count>first || walls.Length>0)
                {
                    replaced.Add(foundation.Id);
                    parts.Add(new(structure.Id,foundation.Id,new(V(fd.Min),V(fd.Max)),fm));
                }
            }
        }
        var owners=new Dictionary<Guid,List<BuildTriangle>>();
        foreach (var group in parts.GroupBy(p=>p.Structure))
        {
            var local=group.ToArray();
            var solids=local.Select(p=>(p.Box,p.Transform)).ToArray();
            var world=BuildingGeometry.Transform(state.Structures.Single(s=>s.Id==group.Key));
            for (int i=0;i<local.Length;i++)
            {
                if (!owners.TryGetValue(local[i].Owner,out var mesh)) owners[local[i].Owner]=mesh=[];
                mesh.AddRange(SolidGeometry.ExposedBox(solids,i).Select(t=>new BuildTriangle(Vector3.Transform(t.A,world),Vector3.Transform(t.B,world),Vector3.Transform(t.C,world))));
            }
        }
        return new(owners.ToDictionary(p=>p.Key,p=>p.Value.ToArray()),replaced);
    }
}
