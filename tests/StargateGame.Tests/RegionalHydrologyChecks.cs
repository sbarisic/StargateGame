using StargateGame.WorldGen;
using StargateGame.Shared;

static class RegionalHydrologyChecks
{
    public static void Run()
    {
        const int n = 17, count = n * n;
        var height = new float[count];
        for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
            height[z*n+x] = Math.Max(0,n-6-z)*4 + Math.Abs(x-n/2)*2;
        var original = height.ToArray();
        var downstream = new int[count]; var flow = new int[count];
        var order = RegionalHydrology.Route(height,n,8,downstream,flow);
        var rank = new int[count]; for (int k=0;k<count;k++)rank[order[k]]=k;
        Verify.Check(downstream.Select((d,i)=>d<0||rank[d]<rank[i]).All(v=>v),"routing is acyclic");
        Verify.Check(downstream.Select((d,i)=>d<0?flow[i]:0).Sum()==count,"all catchment flow reaches boundary outlets");
        Verify.Check(Math.Abs(RegionalHydrology.Distance(n+1,2*n+2,n,8)-8*MathF.Sqrt(2))<.00001f,"diagonal slope uses physical cell spacing");
        var metric = height.Select(h=>h*2).ToArray(); var metricD = new int[count];var metricA = new int[count];
        RegionalHydrology.Route(metric,n,16,metricD,metricA);
        Verify.Check(metricD.SequenceEqual(downstream),"uniform metre scaling preserves drainage direction");
        var filled=new float[count];var runoff=new float[count];
        order=RegionalHydrology.Route(height,n,8,downstream,flow,drainageSurface:filled);
        RegionalErosion.DistributedRunoff(filled,n,8,order,runoff);
        Verify.Check(Math.Abs(downstream.Select((d,i)=>d<0?runoff[i]:0).Sum()-count)<.005,"distributed runoff conserves contributing area at outlets");
        int[] tree=[-1,0,0,1,1,2,2],treeOrder=[0,1,2,3,4,5,6],stream=new int[7];
        RegionalErosion.StreamOrders(tree,treeOrder,stream);
        Verify.Check(stream.SequenceEqual(new[]{3,2,2,1,1,1,1}),"Strahler order increases at equal tributary confluences");
        var sediment = new float[count];
        RegionalErosion.Apply(height,sediment,n,8,123,"temperate",false,0,downstream,flow);
        Verify.Check(height.All(float.IsFinite)&&sediment.All(s=>s>=0&&float.IsFinite(s))&&height.Where((h,i)=>h<original[i]).Any(),"bounded erosion produces finite terrain and nonnegative sediment");
        // A flat valley foot must receive sediment from the steeper upstream cells.
        Verify.Check(sediment.Any(s=>s>0),"USPED deposits material where sediment flux converges");
        var repeat = original.ToArray();var repeatSediment = new float[count];
        RegionalErosion.Apply(repeat,repeatSediment,n,8,123,"temperate",false,0,downstream,flow);
        Verify.Check(repeat.SequenceEqual(height)&&repeatSediment.SequenceEqual(sediment),"complete erosion is deterministic");
        bool cancelled=false;
        try { RegionalHydrology.Route(height,n,8,downstream,flow,new CancellationToken(true)); }
        catch(OperationCanceledException){cancelled=true;}
        Verify.Check(cancelled,"hydrology honors cancellation");
        using var cts=new CancellationTokenSource();cancelled=false;
        try { RegionalErosion.Apply(original.ToArray(),new float[count],n,8,123,"temperate",false,0,downstream,flow,cts.Token,p=>{if(p=="Erosion 8/160")cts.Cancel();}); }
        catch(OperationCanceledException){cancelled=true;}
        Verify.Check(cancelled,"active erosion honors cancellation between batches");
        // A valley eroded below a frozen perimeter must drain out, not become
        // a reservoir held up by the simulation's last row.
        var edge = Enumerable.Repeat(10f, count).ToArray();
        for (int z = 1; z < n - 1; z++)
            for (int x = 1; x < n - 1; x++) edge[z*n+x] = x;
        RegionalErosion.OpenBoundary(edge, n);
        RegionalHydrology.Route(edge,n,8,downstream,flow,drainageSurface:filled);
        Verify.Check(edge[n] == 0 && filled[n+1] == edge[n+1], "eroded boundary valley has an open outlet");
        var basin = Enumerable.Repeat(10f, count).ToArray();
        for (int z=5;z<=11;z++) for (int x=5;x<=11;x++) basin[z*n+x]=2;
        RegionalErosion.OpenBoundary(basin,n);
        RegionalHydrology.Route(basin,n,8,downstream,flow,drainageSurface:filled);
        Verify.Check(filled[8*n+8] >= 10 && basin[8*n+8] == 2,"open edges retain genuine enclosed lake basins");
        var beds = new int[TerrainChunkData.Count]; var water = new int[beds.Length];
        var bodies = new int[beds.Length]; var kinds = new byte[beds.Length];
        for(int z=-1;z<=65;z++) for(int x=-1;x<=65;x++)
        {
            int i=(z+1)*67+x+1; beds[i]=x*1000-31500;
            water[i]=beds[i]<0?0:int.MinValue; bodies[i]=beds[i]<0?2:0;
            kinds[i]=(byte)(beds[i]<0?WaterKind.Lake:WaterKind.None);
        }
        var shore = new TerrainChunkData(new(0,0),beds,water,new byte[beds.Length*4],[],bodies,waterKinds:kinds);
        var triangles = WaterSurfaceData.For(shore).Triangles;
        Verify.Check(triangles.Length>0 && triangles.SelectMany(t=>new[]{t.A,t.B,t.C}).All(p=>p.Y==0&&p.X<=31.5001f),"lake mesh clips at the terrain intersection without lowering the surface");
        Verify.Check(WaterSurfaceData.For(shore).TrySample(31.25f,20.5f,out _) && !WaterSurfaceData.For(shore).TrySample(31.75f,20.5f,out _),"rendered shoreline and water queries agree");
        Console.WriteLine("REGIONAL HYDROLOGY PASSED");
    }
}
