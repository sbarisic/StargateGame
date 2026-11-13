using System.Numerics;
using System.Runtime.CompilerServices;
using StargateGame.Building;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed partial class PhysicsWorld
{
    readonly Dictionary<ChunkCoordinate,TerrainChunkData> committedNavigationChunks=[];
    readonly Dictionary<ChunkCoordinate,TerrainChunkData> preparedNavigationChunks=[];
    public long NavigationCommitVersion {get;private set;}
    public bool NpcStandingSurface(Vector3 point)=>!WaterSurfaceData.TrySample(terrainSource,point.X,point.Z,out var water)||water.Height-point.Y<.4f;
    public sealed record NavigationInput(long Revision, Func<Vector3[]> Triangles);
    public NavigationInput? CaptureNavigation(int x,int z,float size,float border)
    {
        var lo=new Vector3(x*size-border,-4096,z*size-border);
        var hi=new Vector3((x+1)*size+border,4096,(z+1)*size+border);
        var bounds=new BuildBounds(lo,hi);
        List<TerrainChunkData> chunks=[];
        if(terrainSource is not null)
            for(int cz=ChunkCoordinate.At(lo.X,lo.Z).Z;cz<=ChunkCoordinate.At(hi.X,hi.Z).Z;cz++)
            for(int cx=ChunkCoordinate.At(lo.X,lo.Z).X;cx<=ChunkCoordinate.At(hi.X,hi.Z).X;cx++)
            {
                var c=new ChunkCoordinate(cx,cz);
                if(!terrainBodies.ContainsKey(c)||!committedNavigationChunks.TryGetValue(c,out var chunk))return null;
                chunks.Add(chunk);
            }
        var state=Changes; var definition=World.Definition;
        // Query bounds include derived owner geometry. Door pose never changes the bake key.
        var pieces=BuildingQuery.Overlap(bounds).Where(p=>!BuildingPrefabs.IsDoor(p)).OrderBy(p=>p.Id).ToArray();
        var hash=new HashCode();
        foreach(var c in chunks)hash.Add(RuntimeHelpers.GetHashCode(c));
        foreach(var p in pieces){hash.Add(p);hash.Add(state.Structures.Single(s=>s.Id==p.StructureId));}
        foreach(var r in state.Removed.Where(r=>chunks.Any(c=>c.Coordinate==r.Chunk)))hash.Add(r);
        // Connections affect caps/extensions of owners, even when the neighbour is just outside the tile.
        foreach(var p in BuildingQuery.Overlap(new(lo-new Vector3(3,0,3),hi+new Vector3(3,0,3))).Where(p=>!BuildingPrefabs.IsDoor(p)).OrderBy(p=>p.Id))hash.Add(p);
        return new(hash.ToHashCode(),()=>
        {
            var output=new List<Vector3>();
            void Add(Vector3 a,Vector3 b,Vector3 c)
            {
                if(Vector3.Max(a,Vector3.Max(b,c)).X<lo.X||Vector3.Min(a,Vector3.Min(b,c)).X>hi.X||
                   Vector3.Max(a,Vector3.Max(b,c)).Z<lo.Z||Vector3.Min(a,Vector3.Min(b,c)).Z>hi.Z)return;
                output.Add(a);output.Add(b);output.Add(c);
            }
            foreach(var chunk in chunks)
            {
                foreach(var t in chunk.Triangles())
                {
                    var centre=(t.A+t.B+t.C)/3;
                    int ix=Math.Clamp((int)MathF.Floor(centre.X)-chunk.Coordinate.X*64,0,63);
                    int iz=Math.Clamp((int)MathF.Floor(centre.Z)-chunk.Coordinate.Z*64,0,63);
                    float water=chunk.WaterHeight(ix,iz);
                    if(!float.IsFinite(water)||water-centre.Y<.4f)Add(t.A,t.B,t.C);
                }
                foreach(var t in TreeTriangles(chunk,definition.Preset,state,definition.Fixture))Add(t.A,t.B,t.C);
                foreach(var t in RockTriangles(chunk,definition.Preset,state))Add(t.A,t.B,t.C);
            }
            if(terrainSource is null)foreach(var t in World.Triangles())Add(t.A,t.B,t.C);
            foreach(var p in pieces)
            {
                var mesh=StairGeometry.IsStair(p)?StairGeometry.PlayerMesh(state.Structures.Single(s=>s.Id==p.StructureId),p):BuildingGeometry.CollisionMesh(state,p);
                foreach(var t in mesh)Add(t.A,t.B,t.C);
            }
            foreach(var e in definition.Entities)
            {
                foreach(var t in ReadCollision(e.Model))Add(Vector3.Transform(t.A,e.Transform),Vector3.Transform(t.B,e.Transform),Vector3.Transform(t.C,e.Transform));
                // No NPC gate travel: close the aperture for routing, in every gate state.
                if(e.Model=="stargate")
                {
                    var a=Vector3.Transform(new(-3,0,0),e.Transform);var b=Vector3.Transform(new(3,0,0),e.Transform);
                    var c=Vector3.Transform(new(3,7,0),e.Transform);var d=Vector3.Transform(new(-3,7,0),e.Transform);
                    Add(a,b,c);Add(a,c,d);Add(c,b,a);Add(d,c,a);
                }
            }
            return output.ToArray();
        });
    }
    public bool NpcSight(Guid observer,Vector3 from,Vector3 to,int targetPlayer=0)
    {
        lock(NativeGate)
        {
            var delta=to-from;if(delta.LengthSquared()<.0001f)return true;
            interactionFilter ??= Native.Own(new InteractionFilter());
            var n=npcMotions.Values.FirstOrDefault(n=>n.Id==observer);
            interactionFilter.Ignore=n.Id!=Guid.Empty&&n.Id==observer?npcCharacters[n.Slot].InnerBodyID:JoltPhysicsSharp.BodyID.Invalid;
            interactionFilter.Player=characters.TryGetValue(targetPlayer,out var player)?player.InnerBodyID:JoltPhysicsSharp.BodyID.Invalid;interactionFilter.Buildings=null;
            return !system.NarrowPhaseQuery.CastRay(new JoltPhysicsSharp.Ray(from,delta* Math.Max(0,1-.2f/delta.Length())),out _,bodyFilter:interactionFilter);
        }
    }
}
