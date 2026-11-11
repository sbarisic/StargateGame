using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;
using StargateGame.WorldGen;

static class RegionalChecks
{
    public static string Output => Path.GetFullPath(Environment.GetEnvironmentVariable("STARGATEGAME_REGIONAL_REVIEW") ?? "artifacts/approved-terrain");
    public static string Cache => Path.Combine(Output,"cache");
    public static void Seams()
    {
        var plan=new PlanetPlan(7321,"temperate",settlements:new("wilderness"),generation:new(LandscapeMode:LandscapeMode.Coastal),cacheDirectory:Cache);
        var a=RegionalMesher.Build(plan,new(new(0,0),2));var b=RegionalMesher.Build(plan,new(new(-2,0),1));
        var edgeA=a.Positions.Where(p=>p.X==0&&p.Z<=128).Distinct().OrderBy(p=>p.Z).ToArray();
        var edgeB=b.Positions.Where(p=>p.X==0).Distinct().OrderBy(p=>p.Z).ToArray();
        Verify.Check(edgeA.SequenceEqual(edgeB),"mixed regional LOD edges share metre heights");
        var detail=plan.BuildChunk(new(0,0));
        Verify.Check(a.Positions.Where(p=>p.X==0&&p.Z<=64).All(p=>p.Y==detail.Height(0,(int)p.Z)),"canonical and regional boundary heights agree");
        var downhill=plan.Downstream.ToArray();var heights=plan.DrainageHeight.ToArray();
        Verify.Check(downhill.Select((d,i)=>d<0||heights[d]<=heights[i]).All(x=>x),"final drainage descends to outlets");
        Verify.Check(downhill.SequenceEqual(plan.Landscape.Downstream)&&plan.FlowAccumulation.Span.SequenceEqual(plan.Landscape.Accumulation),"rivers and habitat moisture share erosion drainage routes");
        var cover=FarTileKey.Cover(new(0,0,0),512).ToArray();
        Verify.Check(cover.All(k=>k.Valid)&&cover.Length<512,"bounded high quality regional cover");
        var distant=RegionalMesher.Build(plan,new(new(0,0),4));
        var interior=distant.Positions.Where(p=>p.X>0&&p.Z>0&&p.X<1024&&p.Z<1024).Select(p=>p.X).Distinct().Order().ToArray();
        Verify.Check(interior.Zip(interior.Skip(1),(a,b)=>b-a).Max()<=16,"coarsest streaming tile retains dune-scale geometry");
        var icy = new PlanetPlan(7322,"icy",settlements:new("wilderness"),generation:new(LandscapeMode:LandscapeMode.Inland),cacheDirectory:Cache);
        var shore = TerrainMesher.Build(icy.BuildChunk(new(-13,3)),4);
        Verify.Check(TerrainMesher.WaterAttributes(shore).All(v=>float.IsFinite(v.X)&&v.X>=0),"coarse shoreline water has canonical depth attributes");
        Console.WriteLine("REGIONAL SEAMS PASSED");
    }
    public static async Task Profile()
    {
        long baseline=GC.GetTotalMemory(true),peak=baseline;
        var work=Task.Run(()=>new RegionalLandscape(7319,"temperate",new(LandscapeMode:LandscapeMode.Inland)));
        while(!work.IsCompleted){peak=Math.Max(peak,GC.GetTotalMemory(false));await Task.Delay(10);}
        var plan=await work;peak=Math.Max(peak,GC.GetTotalMemory(false));
        var result=new{coldGenerationMs=plan.GenerationMilliseconds,retainedRegionalBytes=plan.Bytes,sampledManagedPeakAboveBaselineBytes=peak-baseline,samplingIntervalMs=10,processPeakBytes=Process.GetCurrentProcess().PeakWorkingSet64};
        string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});
        Directory.CreateDirectory(Output);File.WriteAllText(Path.Combine(Output,"profile.json"),json);Console.WriteLine(json);
    }
    public static async Task Run()
    {
        string output=Output; Directory.CreateDirectory(output);
        string cache=Path.Combine(output,"cache");
        var measurements=new List<object>(); var views=new List<object>();
        foreach(var (seed,preset,mode) in new[]{(7319,"temperate",LandscapeMode.Inland),(7320,"desert",LandscapeMode.Inland),(7321,"temperate",LandscapeMode.Coastal),(7322,"icy",LandscapeMode.Inland)})
        {
            var watch=Stopwatch.StartNew(); long allocated=GC.GetTotalAllocatedBytes();
            var settings=new PlanetGenerationSettings(LandscapeMode:mode);
            var plan=new PlanetPlan(seed,preset,settlements:new("wilderness"),generation:settings,cacheDirectory:cache,progress:p=>Console.WriteLine($"{preset} {p} {watch.Elapsed.TotalSeconds:F1}s"));
            var l=plan.Landscape;
            Verify.Check(plan.Definition.HalfSize==2048 && ChunkCoordinate.All.Count()==4096,"4 km domain");
            Verify.Check(l.Downstream.Select((j,i)=>j<0||l.Accumulation[j]>=l.Accumulation[i]).All(b=>b),"acyclic accumulated drainage");
            int peak=Array.IndexOf(l.Heights,l.Heights.Max()); var summit=RegionalLandscape.Position(peak);
            IEnumerable<int> candidates=Enumerable.Range(0,RegionalLandscape.Count).Where(i=>Math.Abs(RegionalLandscape.Position(i).X)<1700&&Math.Abs(RegionalLandscape.Position(i).Y)<1700);
            float Slope(int i) {
                int x=i%RegionalLandscape.Grid,z=i/RegionalLandscape.Grid;
                return new Vector2(l.Heights[z*RegionalLandscape.Grid+x+1]-l.Heights[z*RegionalLandscape.Grid+x-1],l.Heights[(z+1)*RegionalLandscape.Grid+x]-l.Heights[(z-1)*RegionalLandscape.Grid+x]).Length()/16;
            }
            void View(string name,Func<int,bool> filter,float cameraHeight)
            {
                int selected=candidates.Where(filter).OrderBy(i=>Math.Abs(Vector2.Distance(RegionalLandscape.Position(i),summit)-900)).First(i=>{var p=RegionalLandscape.Position(i);return plan.Sample((int)p.X,(int)p.Y).Water==int.MinValue;});
                var q=RegionalLandscape.Position(selected);var terrain=plan.Sample((int)q.X,(int)q.Y);
                float y=terrain.Height/1000f+cameraHeight;var aim=new Vector3(summit.X,y+140,summit.Y);
                if(name=="forest-interior")aim=new(q.X+30,y-.2f,q.Y-65);
                if(name=="coastline") { int water=candidates.Where(i=>l.Ocean[i]!=0).MinBy(i=>Vector2.DistanceSquared(RegionalLandscape.Position(i),q));var target=RegionalLandscape.Position(water);aim=new(target.X,0,target.Y); }
                views.Add(new{name,seed,preset,mode=mode.ToString(),view=FormattableString.Invariant($"region:{q.X},{y},{q.Y},{aim.X},{aim.Y},{aim.Z}")});
            }
            if(preset=="temperate"&&mode==LandscapeMode.Inland){View("mountain-meadow",i=>l.Forest[i]<.2f&&l.Heights[i]>65&&l.Heights[i]<220&&Slope(i)<.16f,1.8f);View("forest-interior",i=>l.Forest[i]>.65f&&l.Heights[i]<220&&Slope(i)<.15f,1.8f);}
            else if(preset=="desert")View("desert-mesa",i=>l.Sediment[i]>3&&l.Heights[i]<100,25f);
            else if(mode==LandscapeMode.Coastal)View("coastline",i=>l.Ocean[i]==0&&l.Heights[i]>3&&l.Heights[i]<9,5);
            else View("icy-highlands",i=>l.Heights[i]>180&&l.Heights[i]<300,4);
            var g=ChunkCoordinate.At(plan.Gate.X,plan.Gate.Z); var a=plan.BuildChunk(g); var b=plan.BuildChunk(new(g.X+1,g.Z));
            Verify.Check(Enumerable.Range(0,65).All(z=>a.Height(64,z)==b.Height(0,z)),"chunk seam");
            Verify.Check(!float.IsFinite(a.WaterHeight((int)plan.Gate.X-g.X*64,(int)plan.Gate.Z-g.Z*64)),"dry gate arrival");
            var decoded=ChunkCodec.Decode(ChunkCodec.Encode(a));Verify.Check(decoded.Environment.Span.SequenceEqual(a.Environment.Span),"environment round trip");
            var key=new FarTileKey(new(0,0),2);var far=new FarTile(2,key.Origin,RegionalMesher.Build(plan,key),[],[]){Level=key.Level};
            Verify.Check(FarTile.Decode(far.Encode(),key.Origin).Key==key,"regional far tile round trip");
            var reload=new RegionalLandscape(seed,preset,settings,cacheDirectory:cache);
            Verify.Check(reload.CacheHit&&reload.Heights.SequenceEqual(l.Heights),"regional cache reload");
            measurements.Add(new{seed,preset,mode=mode.ToString(),elapsedMs=watch.Elapsed.TotalMilliseconds,regionalMs=l.GenerationMilliseconds,regionalBytes=l.Bytes,allocatedBytes=GC.GetTotalAllocatedBytes()-allocated,processPeakBytes=Process.GetCurrentProcess().PeakWorkingSet64,min=l.Heights.Min(),max=l.Heights.Max(),gate=plan.Gate,forest=l.Forest.Average(),ocean=l.Ocean.Count(v=>v!=0)});
            // Portable diagnostic views, generated from the authoritative regional fields.
            float min=l.Heights.Min(), max=l.Heights.Max();
            foreach(string field in new[]{"elevation","drainage","sediment","geology","habitat"})
            {
                using var stream=File.Create(Path.Combine(output,$"{preset}-{mode}-{field}.ppm"));
                var header=System.Text.Encoding.ASCII.GetBytes($"P6\n{RegionalLandscape.Grid} {RegionalLandscape.Grid}\n255\n");stream.Write(header);
                for(int i=0;i<RegionalLandscape.Count;i++)
                {
                    float v=field switch{"elevation"=>(l.Heights[i]-min)/Math.Max(1,max-min),"drainage"=>MathF.Log2(l.Accumulation[i]+1)/18,"sediment"=>l.Sediment[i]/16,"geology"=>l.Rock[i]/2f,_=>l.Forest[i]};
                    byte c=(byte)Math.Clamp((int)(v*255),0,255);stream.WriteByte(field=="habitat"?(byte)(c/2):c);stream.WriteByte(c);stream.WriteByte(l.Ocean[i]!=0?(byte)255:c);
                }
            }
        }
        using(var cancel=new CancellationTokenSource())
        {
            cancel.Cancel();bool cancelled=false;
            try{_=new RegionalLandscape(8,"temperate",new(),cancel.Token);}catch(OperationCanceledException){cancelled=true;}
            Verify.Check(cancelled,"cancelled regional build does not publish");
        }
        // An uncached repeat checks seed determinism independently of the cache reader.
        var one=new RegionalLandscape(7319,"temperate",new(LandscapeMode:LandscapeMode.Inland));
        var two=new RegionalLandscape(7319,"temperate",new(LandscapeMode:LandscapeMode.Inland),cacheDirectory:cache);
        Verify.Check(one.Heights.SequenceEqual(two.Heights)&&one.Sediment.SequenceEqual(two.Sediment),"cold deterministic regional generation");
        File.WriteAllText(Path.Combine(output,"views.json"),JsonSerializer.Serialize(views,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"measurements.json"),JsonSerializer.Serialize(measurements,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        Console.WriteLine($"REGIONAL {Verify.Count} CHECKS PASSED");await Task.CompletedTask;
    }
}
