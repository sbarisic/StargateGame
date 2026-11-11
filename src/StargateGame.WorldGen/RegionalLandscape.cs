using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using StargateGame.Shared;

namespace StargateGame.WorldGen;

/// <summary>Immutable, deterministic regional geology. Detailed chunks only sample it.</summary>
public sealed class RegionalLandscape
{
    public const int Step = 8, Grid = WorldDomain.Size / Step + 1, Count = Grid * Grid;
    const int CacheVersion = 4;
    public readonly float[] Heights = new float[Count], Sediment = new float[Count], Moisture = new float[Count],
        Exposure = new float[Count], Forest = new float[Count];
    public readonly int[] Downstream = new int[Count], Accumulation = new int[Count];
    public readonly byte[] Ocean = new byte[Count], Rock = new byte[Count];
    public readonly int Seed;
    public readonly string Preset;
    public readonly bool Coastal;
    public readonly string LandformStyle;
    public readonly float SeaLevel, WindAngle;
    public double GenerationMilliseconds { get; private set; }
    public bool CacheHit { get; private set; }
    public long Bytes => Count * (5L * 4 + 2 * 4 + 2);
    readonly FastNoiseLite noise, detail;
    readonly PlanetGenerationSettings settings;
    readonly record struct Ridge(Vector2 A, Vector2 B, float Width, float Height);
    static readonly (int X, int Z)[] Neighbors = [(-1,-1),(0,-1),(1,-1),(-1,0),(1,0),(-1,1),(0,1),(1,1)];
    static float Smooth(float a, float b, float x) { float t = Math.Clamp((x-a)/(b-a),0,1); return t*t*(3-2*t); }
    public RegionalLandscape(int seed, string preset, PlanetGenerationSettings settings, CancellationToken cancellation = default,
        Action<string>? progress = null, string? cacheDirectory = null)
    {
        Seed = seed; Preset = preset; this.settings = settings;
        Coastal = settings.LandscapeMode == LandscapeMode.Coastal || settings.LandscapeMode == LandscapeMode.Auto && (PlanetPlan.Hash(seed,0,0,9101)&1)==0;
        SeaLevel = settings.WaterOffset;
        LandformStyle = preset == "desert" ? "Mesas" : Coastal ? "Coastal ranges" : (PlanetPlan.Hash(seed,0,0,9210)&3)==0 ? "Broad ridges" : "Branching ranges";
        WindAngle = (PlanetPlan.Hash(seed,0,0,9102)%65536)/65536f*MathF.Tau;
        noise = new(seed); noise.SetFrequency(.0012f); noise.SetFractalType(FastNoiseLite.FractalType.FBm); noise.SetFractalOctaves(4);
        detail = new(unchecked(seed+270419)); detail.SetFrequency(.009f); detail.SetFractalType(FastNoiseLite.FractalType.FBm); detail.SetFractalOctaves(3);
        string? path = cacheDirectory is null ? null : Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"regional-{CacheVersion}-{PlanetSettings.Version}-{seed}-{preset}-{settings.Fingerprint}"))) + ".rgn");
        var watch = Stopwatch.StartNew();
        cancellation.ThrowIfCancellationRequested();
        if (path is not null && TryLoad(path)) { CacheHit = true; progress?.Invoke("Water"); return; }
        foreach(var a in new[]{Heights,Sediment,Moisture,Exposure,Forest}) Array.Clear(a);
        Array.Clear(Ocean); Array.Clear(Rock);
        progress?.Invoke("Landforms");
        Landforms(cancellation);
        progress?.Invoke("Erosion");
        RegionalErosion.Apply(Heights,Sediment,Grid,Step,Seed,Preset,Coastal,SeaLevel,Downstream,Accumulation,cancellation,progress);
        if (preset == "desert") Dunes(cancellation);
        progress?.Invoke("Water");
        Drain(cancellation);
        FloodOcean(cancellation);
        
        Habitats(cancellation);
        GenerationMilliseconds = watch.Elapsed.TotalMilliseconds;
        cancellation.ThrowIfCancellationRequested();
        if (path is not null) Save(path, cancellation);
    }
    public static Vector2 Position(int i) => new(i % Grid * Step - WorldDomain.HalfSize, i / Grid * Step - WorldDomain.HalfSize);
    void Landforms(CancellationToken token)
    {
        if(LandformStyle=="Broad ridges"){RidgeLandforms(token);return;}
        var baseNoise=new RegionalTerrainNoise(Seed);
        var plateauNoise=new RegionalTerrainNoise(unchecked(Seed+31));
        var coastNoise=new RegionalTerrainNoise(unchecked(Seed+43));
        float angle=(PlanetPlan.Hash(Seed,0,0,9211)&65535)/65535f*MathF.Tau;
        var along=new Vector2(MathF.Cos(angle),MathF.Sin(angle));var across=new Vector2(-along.Y,along.X);
        float min=float.MaxValue,max=float.MinValue;
        for(int i=0;i<Count;i++)
        {
            if(i%Grid==0)token.ThrowIfCancellationRequested();
            var p=Position(i);float x=Vector2.Dot(p,along),z=Vector2.Dot(p,across);
            float n=Math.Clamp((baseNoise.Fbm(x,z,2800,7,.95)-.23f)/.48f,0,1);
            float envelope=.25f+.75f*MathF.Exp(-MathF.Pow((x-450)/1950,2)-MathF.Pow((z+450)/1900,2));
            float h=30+900*MathF.Pow(n,2.7f)*envelope+60*(z+2048)/4096;
            if(Preset=="desert")h=25+Smooth(.40f,.65f,n)*210+22*plateauNoise.Fbm(x,z,1300,5);
            Heights[i]=h;min=Math.Min(min,h);max=Math.Max(max,h);
            Rock[i]=(byte)(Preset=="desert"?Geology.Sandstone:n>.65f?Geology.Limestone:Geology.Granite);
        }
        for(int i=0;i<Count;i++)
        {
            if(i%Grid==0)token.ThrowIfCancellationRequested();
            var p=Position(i);float x=Vector2.Dot(p,along),z=Vector2.Dot(p,across);
            float h=Preset=="desert"?Heights[i]:(Heights[i]-min)/Math.Max(1,max-min)*660+24;
            h*=settings.Relief;
            if(Coastal)
            {
                float shape=(coastNoise.Fbm(x,z,1800,4)-.5f)*2300+210*MathF.Sin(z/640)+120*MathF.Sin(z/270);
                float coast=Smooth(0,1750,x+700+shape);
                h=h*(1-coast)-45*coast;
                h+=95*settings.Relief*MathF.Exp(-MathF.Pow((x-1650)/260,2)-MathF.Pow((z+950)/370,2));
            }
            Heights[i]=h;
        }
    }
    void RidgeLandforms(CancellationToken token)
    {
        float U(int i) => (PlanetPlan.Hash(Seed,i,0,9103)&65535)/65535f;
        float angle = U(0)*MathF.Tau; var along = new Vector2(MathF.Cos(angle),MathF.Sin(angle)); var across = new Vector2(-along.Y,along.X);
        var ridges = new List<Ridge>();
        for(int chain=0;chain<3;chain++)
        {
            var center = along*((U(2+chain)*2-1)*1100)+across*((chain-1)*750+(U(8+chain)-.5f)*300);
            var previous = center-along*(550+U(12+chain)*450);
            float height = (Preset=="desert"?70+U(18+chain)*180:250+U(18+chain)*450)*settings.Relief;
            for(int segment=0;segment<3;segment++)
            {
                var next = center + along*((segment-1)*550) + across*((U(30+chain*3+segment)-.5f)*450);
                ridges.Add(new(previous,next,280+U(44+chain)*280,height*(.75f+U(50+segment)*.25f)));
                previous=next;
            }
        }
        var coastDirection = new Vector2(MathF.Cos(WindAngle+.8f),MathF.Sin(WindAngle+.8f));
        for(int i=0;i<Count;i++)
        {
            if(i%Grid==0)token.ThrowIfCancellationRequested();
            var p=Position(i); float n=noise.GetNoise(p.X,p.Y), rockHeight=0;
            var warped=p+new Vector2(noise.GetNoise(p.X+1300,p.Y-600),noise.GetNoise(p.X-750,p.Y+1300))*130;
            foreach(var ridge in ridges)
            {
                var ab=ridge.B-ridge.A; float t=Math.Clamp(Vector2.Dot(warped-ridge.A,ab)/Math.Max(1,ab.LengthSquared()),0,1);
                float d=Vector2.Distance(warped,Vector2.Lerp(ridge.A,ridge.B,t));
                float shape=MathF.Exp(-MathF.Pow(d/ridge.Width,1.55f));
                if(Preset=="desert") shape=Smooth(.14f,.72f,shape)*(.91f+.09f*detail.GetNoise(p.X,p.Y));
                else shape*=.84f+.16f*(1-Math.Abs(detail.GetNoise(p.X,p.Y)));
                rockHeight=Math.Max(rockHeight,ridge.Height*shape);
            }
            float basin=1-Smooth(100,650,Math.Abs(Vector2.Dot(p,across)-noise.GetNoise(p.X+5000,p.Y)*180));
            float h=34+n*18*settings.Relief+rockHeight*(1-basin*.42f);
            if(Coastal)
            {
                float coast=Vector2.Dot(p,coastDirection)-350+n*260;
                h-=Smooth(0,1600,coast)*(h+60);
                float islands=MathF.Exp(-Vector2.DistanceSquared(p,coastDirection*1550+along*600)/(180*180));
                h+=islands*95;
            }
            Heights[i]=h;
            Rock[i]=(byte)(Preset=="desert"?Geology.Sandstone:n>.25f?Geology.Limestone:Geology.Granite);
        }
    }
    void Drain(CancellationToken token) =>
        RegionalHydrology.Route(Heights, Grid, Step, Downstream, Accumulation, token);

    void Dunes(CancellationToken token)
    {
        var warp = new RegionalTerrainNoise(unchecked(Seed+91));
        var amplitude = new RegionalTerrainNoise(unchecked(Seed+92));
        var cover = new float[Count]; var blurred = new float[Count]; var scratch = new float[Count];
        var sand = new float[Count];
        for(int i=0;i<Count;i++)
        {
            if(i%Grid==0)token.ThrowIfCancellationRequested();
            float dx=RegionalErosion.Derivative(Heights,i,Grid,Step,false),dz=RegionalErosion.Derivative(Heights,i,Grid,Step,true);
            cover[i]=Math.Clamp((115*settings.Relief-Heights[i])/(55*settings.Relief),0,1)*Math.Clamp(1-MathF.Sqrt(dx*dx+dz*dz)/.35f,0,1)*Smooth(SeaLevel,SeaLevel+6,Heights[i]);
        }
        RegionalErosion.Blur(cover,blurred,scratch,Grid,3,token);
        var wind=new Vector2(MathF.Cos(WindAngle),MathF.Sin(WindAngle));var cross=new Vector2(-wind.Y,wind.X);
        for(int i=0;i<Count;i++)
        {
            if(i%Grid==0)token.ThrowIfCancellationRequested();var p=Position(i);
            float u=Vector2.Dot(p,wind),v=Vector2.Dot(p,cross);
            float phase=(u+(warp.Fbm(p.X,p.Y,900,4)-.5f)*430+55*MathF.Sin(v/175))/185;
            float t=phase-MathF.Floor(phase),profile=MathF.Pow(t<.8f?t/.8f:(1-t)/.2f,1.4f);
            sand[i]=profile*Math.Clamp((amplitude.Fbm(p.X,p.Y,520,3)-.30f)/.30f,0,1)*13*settings.Relief*blurred[i];
        }
        RegionalErosion.Blur(sand,cover,scratch,Grid,.6f,token);
        for(int i=0;i<Count;i++){Heights[i]+=cover[i];Sediment[i]+=cover[i];}
    }
    void FloodOcean(CancellationToken token)
    {
        if(!Coastal)return;var queue=new Queue<int>();
        for(int z=0;z<Grid;z++)for(int x=0;x<Grid;x++)if(x==0||z==0||x==Grid-1||z==Grid-1)
        {int i=z*Grid+x;if(Heights[i]<SeaLevel){Ocean[i]=1;queue.Enqueue(i);}}
        int count=0;while(queue.TryDequeue(out int i))
        {if((count++&4095)==0)token.ThrowIfCancellationRequested();int x=i%Grid,z=i/Grid;
            foreach(var(dx,dz)in Neighbors){int nx=x+dx,nz=z+dz;if(nx<0||nz<0||nx>=Grid||nz>=Grid)continue;int j=nz*Grid+nx;
                if(Ocean[j]==0&&Heights[j]<SeaLevel){Ocean[j]=1;queue.Enqueue(j);}}}
    }
    void Habitats(CancellationToken token)
    {
        for(int z=0;z<Grid;z++)for(int x=0;x<Grid;x++)
        {
            if(x==0)token.ThrowIfCancellationRequested();int i=z*Grid+x;var p=Position(i);
            float dx=Heights[z*Grid+Math.Min(Grid-1,x+1)]-Heights[z*Grid+Math.Max(0,x-1)],dz=Heights[Math.Min(Grid-1,z+1)*Grid+x]-Heights[Math.Max(0,z-1)*Grid+x];
            float slope=MathF.Sqrt(dx*dx+dz*dz)/(2*Step);
            Exposure[i]=Math.Clamp(slope*.55f+Smooth(120,600,Heights[i])*.45f,0,1);
            Moisture[i]=Math.Clamp((Preset=="desert"?.08f:.32f)+MathF.Log2(1+Accumulation[i])*.035f+Sediment[i]*.03f-Exposure[i]*.3f,0,1);
            float patch=Smooth(-.32f,.38f,noise.GetNoise(p.X-7000,p.Y+8000));
            Forest[i]=Preset=="desert"?0:patch*(1-Smooth(.28f,.75f,slope))*(1-Smooth(Preset=="icy"?160:380,Preset=="icy"?340:650,Heights[i]))*Smooth(.18f,.48f,Moisture[i]);
        }
    }
    float Sample(float[] field,float x,float z)
    {
        float gx=Math.Clamp((x+WorldDomain.HalfSize)/Step,0,Grid-1),gz=Math.Clamp((z+WorldDomain.HalfSize)/Step,0,Grid-1);
        int ix=Math.Min(Grid-2,(int)gx),iz=Math.Min(Grid-2,(int)gz);float u=gx-ix,v=gz-iz;
        return float.Lerp(float.Lerp(field[iz*Grid+ix],field[iz*Grid+ix+1],u),float.Lerp(field[(iz+1)*Grid+ix],field[(iz+1)*Grid+ix+1],u),v);
    }
    int Index(float x,float z)=>Math.Clamp((int)MathF.Round((z+WorldDomain.HalfSize)/Step),0,Grid-1)*Grid+Math.Clamp((int)MathF.Round((x+WorldDomain.HalfSize)/Step),0,Grid-1);
    public float Drainage(float x,float z) => Math.Clamp(MathF.Log2(1+Accumulation[Index(x,z)])/18,0,1);
    public float Height(float x,float z)=>Sample(Heights,x,z);
    public bool OceanAt(float x,float z)
    {
        int gx=Math.Clamp((int)MathF.Floor((x+WorldDomain.HalfSize)/Step),0,Grid-2),gz=Math.Clamp((int)MathF.Floor((z+WorldDomain.HalfSize)/Step),0,Grid-2);
        return Ocean[gz*Grid+gx]!=0||Ocean[gz*Grid+gx+1]!=0||Ocean[(gz+1)*Grid+gx]!=0||Ocean[(gz+1)*Grid+gx+1]!=0;
    }
    public LandscapeEnvironment Environment(float x,float z,float slope=0,float wet=0)
    {
        float h=Height(x,z),sed=Math.Clamp(Sample(Sediment,x,z)/6,0,1),moist=Math.Max(wet,Sample(Moisture,x,z)),exposure=Sample(Exposure,x,z),forest=Sample(Forest,x,z);
        float soil=Math.Clamp(.7f+sed*.3f-exposure*.8f-slope*.5f,0,1),snow=Preset=="icy"?Smooth(10,130,h):Smooth(430,650,h)*(1-exposure*.35f);
        bool beach=Coastal&&h<SeaLevel+8&&h>SeaLevel-3&&(OceanAt(x,z)||OceanAt(x-16,z)||OceanAt(x+16,z)||OceanAt(x,z-16)||OceanAt(x,z+16));
        if(beach)forest=0;
        Habitat habitat=beach?Habitat.Beach:wet>.65f?Habitat.WetBank:slope>.65f||soil<.15f?Habitat.ExposedRock:snow>.3f?Habitat.Alpine:Preset=="desert"?Habitat.Arid:forest>.65f?Habitat.ForestInterior:forest>.28f?Habitat.ForestEdge:Habitat.Meadow;
        return new(moist,exposure,soil,Sample(Sediment,x,z),forest,(Geology)Rock[Index(x,z)],habitat,snow);
    }
    bool TryLoad(string path)
    {
        try
        {
            using var r=new BinaryReader(File.OpenRead(path));if(r.ReadInt32()!=0x314e4752||r.ReadInt32()!=Count)return false;
            foreach(var a in new[]{Heights,Sediment,Moisture,Exposure,Forest})for(int i=0;i<Count;i++){a[i]=r.ReadSingle();if(!float.IsFinite(a[i]))return false;}
            foreach(var a in new[]{Downstream,Accumulation})for(int i=0;i<Count;i++)a[i]=r.ReadInt32();
            r.ReadBytes(Count).CopyTo(Ocean,0);r.ReadBytes(Count).CopyTo(Rock,0);
            return r.BaseStream.Position==8+Bytes&&r.BaseStream.Length==8+Bytes && Downstream.All(i=>i>=-1&&i<Count) && Accumulation.All(i=>i>=0&&i<=Count) && Ocean.All(i=>i<=1) && Rock.All(i=>i<=2);
        }
        catch(IOException){return false;}
    }
    void Save(string path,CancellationToken token)
    {
        string directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var w=new BinaryWriter(File.Create(temp)))
            {w.Write(0x314e4752);w.Write(Count);foreach(var a in new[]{Heights,Sediment,Moisture,Exposure,Forest})foreach(float v in a)w.Write(v);
                foreach(var a in new[]{Downstream,Accumulation})foreach(int v in a)w.Write(v);w.Write(Ocean);w.Write(Rock);}
            token.ThrowIfCancellationRequested();File.Move(temp,path,true);
            foreach(var old in new DirectoryInfo(directory).GetFiles("*.rgn").OrderByDescending(f=>f.LastWriteTimeUtc).Skip(32))try{old.Delete();}catch(IOException){}
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
