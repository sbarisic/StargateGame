using System.Numerics;
using System.Diagnostics;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using DotRecast.Detour;
using StargateGame.Shared;
namespace StargateGame.Physics;

public sealed record NavigationTileInfo(int X,int Z,long Revision,string State,double Milliseconds,long Bytes,Vector3[] Edges);
public sealed record NavigationRoute(NavigationOutcome Outcome,Vector3[] Points,long Revision,string Reason);
public sealed class NpcNavigation : IDisposable
{
    sealed class Tile
    {
        public long Revision,Ref; public Task<Built>? Job; public long BuildingRevision;
        public bool Ready; public Built? Data; public string Error="";public long Used;
    }
    sealed record Built(DtMeshData? Mesh,double Milliseconds,long Bytes,Vector3[] Edges);
    readonly Dictionary<(int X,int Z),Tile> tiles=[];
    readonly DtNavMesh mesh=new();
    readonly DtNavMeshQuery query;
    readonly DtQueryDefaultFilter filter=new();
    readonly CharacterDefinition agent=CharacterDefinitions.Soldier;
    readonly SemaphoreSlim jobLimit;
    readonly CancellationTokenSource stop=new();
    public float CellSize {get;}
    public float TileSize {get;}
    public long Revision {get;private set;}
    long clock;
    bool disposed;
    public NpcNavigation(float cellSize=NpcRules.NavigationCellSize):this(cellSize,new SemaphoreSlim(2,2)) { }
    NpcNavigation(float cellSize,SemaphoreSlim jobLimit)
    {
        this.jobLimit=jobLimit;
        if(cellSize is not (.1f or .15f or .2f))throw new ArgumentOutOfRangeException(nameof(cellSize));
        CellSize=cellSize;TileSize=MathF.Round(32/cellSize)*cellSize;
        var status=mesh.Init(new DtNavMeshParams{orig=new(0,0,0),tileWidth=TileSize,tileHeight=TileSize,maxTiles=256,maxPolys=32768},6);
        if(status.Failed())throw new InvalidOperationException("Navigation mesh initialization failed.");
        query=new(mesh);
    }
    public NpcNavigation Replacement(float cellSize=NpcRules.NavigationCellSize)=>new(cellSize,jobLimit);
    public (int X,int Z) Coordinate(Vector3 p)=>((int)MathF.Floor(p.X/TileSize),(int)MathF.Floor(p.Z/TileSize));
    public NavigationTileInfo[] Inspect()=>tiles.Select(k=>new NavigationTileInfo(k.Key.X,k.Key.Z,k.Value.Revision,
        k.Value.Ready?"Ready":k.Value.Error.Length>0?k.Value.Error:k.Value.Job is null?"Dirty":"Pending",k.Value.Data?.Milliseconds??0,k.Value.Data?.Bytes??0,k.Value.Data?.Edges??[])).ToArray();
    public void Pump(PhysicsWorld world)
    {
        if(disposed)return;clock++;
        foreach(var (key,tile) in tiles.ToArray())
        {
            var input=world.CaptureNavigation(key.X,key.Z,TileSize,RcConfig.CalcBorder(agent.Radius,CellSize)*CellSize);
            if(input is null || tile.Revision!=input.Revision)
            {
                if(tile.Ready){tile.Ready=false;if(tile.Ref!=0)mesh.RemoveTile(tile.Ref);tile.Ref=0;Revision++;}
                if(input is not null){if(tile.Revision!=input.Revision)tile.Error="";tile.Revision=input.Revision;}
            }
            if(tile.Job is {IsCompleted:true} job)
            {
                tile.Job=null;
                if(job.IsFaulted){tile.Error="Navigation build failed";Console.Error.WriteLine("NPC navigation: "+job.Exception?.GetBaseException().Message);}
                else if(input is not null && tile.BuildingRevision==input.Revision)
                {
                    var data=job.Result;tile.Data=data;
                    if(data.Mesh is not null&&mesh.AddTile(data.Mesh,0,0,out tile.Ref).Failed()){tile.Error="Navigation tile capacity";continue;}
                    tile.Ready=true;Revision++;
                }
            }
            if(!tile.Ready&&input is not null&&tile.Job is null&&tile.Error.Length==0&&tiles.Values.Count(t=>t.Job is not null)<2)
            {
                tile.BuildingRevision=input.Revision;
                tile.Job=Task.Run(async()=>
                {
                    await jobLimit.WaitAsync(stop.Token);
                    try{stop.Token.ThrowIfCancellationRequested();return Build(input.Triangles,key.X,key.Z);}
                    finally{jobLimit.Release();}
                });
            }
        }
        foreach(var pair in tiles.Where(p=>clock-p.Value.Used>600&&p.Value.Job is null).ToArray())
        {if(pair.Value.Ref!=0)mesh.RemoveTile(pair.Value.Ref);tiles.Remove(pair.Key);Revision++;}
    }
    public bool Current(Vector3 p)=>tiles.TryGetValue(Coordinate(p),out var t)&&t.Ready;
    // Unlike a distance-only corner test, this cannot cut across an obstacle.
    // Check the end height as Detour's raycast alone is only an XZ query.
    public bool CanTraverse(Vector3 from,Vector3 to)
        =>TrySurfacePath(from,to,out var surface)&&Math.Abs(surface.Y-to.Y)<=agent.StepHeight+.1f;
    public bool TrySurfacePath(Vector3 from,Vector3 to,out Vector3 surface)
    {
        surface=to;
        if(!Current(from)||!Current(to))return false;
        query.FindNearestPoly(V(from),new RcVec3f(.8f,.6f,.8f),filter,out long start,out var nearest,out _);
        if(start==0)return false;
        Span<long> crossed=stackalloc long[128];
        var status=query.Raycast(start,nearest,V(to),filter,out float fraction,out _,crossed,out int count,crossed.Length);
        if(status.Failed()||count==0)return false;
        // Straight-path corners lie on polygon edges. Floating-point rounding
        // may report a hit just before that endpoint or put it outside the last
        // polygon's height query. Permit only a 5 mm endpoint tolerance, never
        // enough to skip a wall corner, and obtain height on the visited polygon.
        if(fraction<1&&(1-fraction)*Vector3.Distance(N(nearest),to)>.005f)return false;
        var hit=Vector3.Lerp(N(nearest),to,Math.Min(1,fraction));
        if(query.ClosestPointOnPoly(crossed[count-1],V(hit),out var point,out _).Failed())return false;
        surface.Y=point.Y;return true;
    }
    public Vector3 ConstrainSteering(Vector3 position,Vector3 delta)
    {
        if(!Current(position)||!Current(position+delta))return Vector3.Zero;
        query.FindNearestPoly(V(position),new RcVec3f(.8f,.6f,.8f),filter,out long start,out var nearest,out _);
        if(start==0)return Vector3.Zero;
        Span<long> visited=stackalloc long[16];
        if(query.MoveAlongSurface(start,nearest,V(position+delta),filter,out var end,visited,out _,visited.Length).Failed())return Vector3.Zero;
        var allowed=N(end)-position;allowed.Y=0;
        if(allowed.LengthSquared()>delta.LengthSquared()&&allowed.LengthSquared()>.0001f)allowed=Vector3.Normalize(allowed)*delta.Length();
        return allowed;
    }
    public long CorridorRevision(Vector3[] points)
    {
        var visited=new HashSet<(int,int)>();var hash=new HashCode();
        for(int i=0;i<points.Length;i++)
        {
            var end=points[Math.Min(i+1,points.Length-1)];int samples=Math.Max(1,(int)MathF.Ceiling(Vector3.Distance(points[i],end)/4));
            for(int j=0;j<=samples;j++)
            {
                var key=Coordinate(Vector3.Lerp(points[i],end,(float)j/samples));
                if(!tiles.TryGetValue(key,out var t)||!t.Ready)return long.MinValue;
                t.Used=clock;if(visited.Add(key)){hash.Add(key);hash.Add(t.Revision);}
            }
        }
        return hash.ToHashCode();
    }
    public NavigationRoute Route(Vector3 from,Vector3 to)
    {
        var a=Coordinate(Vector3.Min(from,to));var b=Coordinate(Vector3.Max(from,to));bool pending=false;
        // One surrounding tile permits detours; the requested region is bounded by the 100 m order limit.
        for(int z=a.Z-1;z<=b.Z+1;z++)for(int x=a.X-1;x<=b.X+1;x++)
        {
            if(!tiles.TryGetValue((x,z),out var tile))
            {if(tiles.Count>=240)return new(NavigationOutcome.PendingNavigation,[],Revision,"Navigation tile budget busy");tiles[(x,z)]=tile=new();}
            tile.Used=clock;pending|=!tile.Ready;
        }
        if(!Current(from)||!Current(to))return new(NavigationOutcome.PendingNavigation,[],Revision,"Preparing navigation");
        var ext=new RcVec3f(.8f,1.5f,.8f);
        query.FindNearestPoly(V(from),ext,filter,out long start,out var startPoint,out _);
        query.FindNearestPoly(V(to),ext,filter,out long end,out var endPoint,out _);
        if(start==0||end==0)return new(pending?NavigationOutcome.PendingNavigation:NavigationOutcome.Unreachable,[],Revision,"No current walkable destination or start surface");
        Span<long> corridor=stackalloc long[512];
        var status=query.FindPath(start,end,startPoint,endPoint,filter,corridor,out int count,corridor.Length);
        if(status.Failed()||count==0||corridor[count-1]!=end)return new(pending?NavigationOutcome.PendingNavigation:NavigationOutcome.Unreachable,[],Revision,pending?"Preparing detour navigation":"No connected route in current navigation");
        Span<DtStraightPath> corners=stackalloc DtStraightPath[128];
        status=query.FindStraightPath(startPoint,endPoint,corridor,count,corners,out int nc,corners.Length,0);
        if(status.Failed()||nc==0)return new(NavigationOutcome.Unreachable,[],Revision,"Invalid path corridor");
        var points=new Vector3[nc];for(int i=0;i<nc;i++)points[i]=N(corners[i].pos);
        return new(NavigationOutcome.Ready,points,Revision,"");
    }
    Built Build(Func<Vector3[]> generate,int x,int z)
    {
        var watch=Stopwatch.StartNew();var soup=generate();stop.Token.ThrowIfCancellationRequested();if(soup.Length==0)return new(null,watch.Elapsed.TotalMilliseconds,0,[]);
        var vertices=new float[soup.Length*3];for(int i=0;i<soup.Length;i++){vertices[i*3]=soup[i].X;vertices[i*3+1]=soup[i].Y;vertices[i*3+2]=soup[i].Z;}
        var geom=new RcSampleInputGeomProvider(vertices,Enumerable.Range(0,soup.Length).ToArray());
        int cells=(int)MathF.Round(TileSize/CellSize);
        var cfg=new RcConfig(true,cells,cells,RcConfig.CalcBorder(agent.Radius,CellSize),RcPartition.LAYERS,
            CellSize,.05f,agent.SlopeDegrees,agent.Height,agent.Radius,agent.StepHeight,1,4,12,1.3f,6,6,1,true,true,true,new RcAreaModification(1),true);
        // Common origin keeps all tile boundaries and portals on the same voxel grid.
        var result=new RcBuilder().Build(geom,new RcBuilderConfig(cfg,new(0,MathF.Floor((geom.GetMeshBoundsMin().Y-1)/.05f)*.05f,0),new(0,geom.GetMeshBoundsMax().Y+2,0),x,z),false);
        stop.Token.ThrowIfCancellationRequested();
        var p=result.Mesh;var d=result.MeshDetail;
        if(p.npolys==0)return new(null,watch.Elapsed.TotalMilliseconds,0,[]);
        Array.Fill(p.flags,1);
        var data=DtNavMeshBuilder.CreateNavMeshData(new DtNavMeshCreateParams {
            verts=p.verts,vertCount=p.nverts,polys=p.polys,polyAreas=p.areas,polyFlags=p.flags,polyCount=p.npolys,nvp=p.nvp,
            detailMeshes=d.meshes,detailVerts=d.verts,detailVertsCount=d.nverts,detailTris=d.tris,detailTriCount=d.ntris,
            bmin=p.bmin,bmax=p.bmax,cs=p.cs,ch=p.ch,walkableHeight=agent.Height,walkableRadius=agent.Radius,walkableClimb=agent.StepHeight,
            tileX=x,tileZ=z,buildBvTree=true });
        var edges=new List<Vector3>();
        Vector3 Vertex(int index)=>new(p.bmin.X+p.verts[index*3]*p.cs,p.bmin.Y+p.verts[index*3+1]*p.ch,p.bmin.Z+p.verts[index*3+2]*p.cs);
        for(int i=0;i<p.npolys;i++)
        {
            var ids=p.polys.Skip(i*p.nvp*2).Take(p.nvp).TakeWhile(v=>v!=0xffff).ToArray();
            for(int j=0;j<ids.Length;j++){edges.Add(Vertex(ids[j]));edges.Add(Vertex(ids[(j+1)%ids.Length]));}
        }
        return new(data,watch.Elapsed.TotalMilliseconds,p.verts.Length*4L+p.polys.Length*4L+d.verts.Length*4L+d.tris.Length*4L,edges.ToArray());
    }
    static RcVec3f V(Vector3 p)=>new(p.X,p.Y,p.Z);
    static Vector3 N(RcVec3f p)=>new(p.X,p.Y,p.Z);
    public void Dispose(){if(disposed)return;disposed=true;stop.Cancel();foreach(var t in tiles.Values)if(t.Job is {} job)_=job.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);tiles.Clear();}
}
