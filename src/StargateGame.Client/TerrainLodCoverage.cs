using System.Diagnostics;
using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    Texture? terrainCoverage;
    readonly Vector4[] terrainCoveragePixels = new Vector4[64*64];
    readonly Stopwatch terrainCoverageClock = Stopwatch.StartNew();
    const float TerrainFadeSeconds = .35f;
    static int TerrainLevel(Resident r) => r.Far ? 2 + (int)Math.Log2(r.Step/8) : 1;
    static FarTileKey ResidentTile(ChunkCoordinate c, Resident r) => r.Far
        ? new(new(c.X-(TerrainLevel(r)-1)*128,c.Z),TerrainLevel(r)-2) : new(c,0);
    IDisposable? BindTerrainCoverage(ShaderProgram program)
    {
        program.SetUniform("uTerrainCoverage",31);
        program.SetUniform("uTerrainCoverageTime",(float)terrainCoverageClock.Elapsed.TotalSeconds);
        program.SetUniform("uTerrainCoverageLevel",0);
        return terrainCoverage?.Bind(31);
    }
    void UpdateTerrainCoverage(Vector3 eye)
    {
        var desired=FarTileKey.Cover(eye,detailRadius).ToHashSet();
        byte[] wants=new byte[4096], ready=new byte[4096], fallback=new byte[4096];
        foreach(var k in desired)for(int z=k.Origin.Z;z<k.Origin.Z+k.Span;z++)for(int x=k.Origin.X;x<k.Origin.X+k.Span;x++)wants[(z+32)*64+x+32]=(byte)(k.Level+2);
        foreach(var (c,r) in resident)
        {
            var k=ResidentTile(c,r);byte level=(byte)TerrainLevel(r);
            for(int z=k.Origin.Z;z<k.Origin.Z+k.Span;z++)for(int x=k.Origin.X;x<k.Origin.X+k.Span;x++)
            {
                int i=(z+32)*64+x+32;
                if(wants[i]==level || wants[i]==0&&level==1)ready[i]=level;
                if(fallback[i]==0||level<fallback[i])fallback[i]=level;
            }
        }
        float now=(float)terrainCoverageClock.Elapsed.TotalSeconds;bool changed=terrainCoverage is null;
        for(int i=0;i<4096;i++)
        {
            float target=ready[i]!=0?ready[i]:fallback[i];var old=terrainCoveragePixels[i];
            if(target==0 || target==old.Y || old.X!=old.Y&&now-old.Z<TerrainFadeSeconds)continue;
            terrainCoveragePixels[i]=new(old.Y==0?target:old.Y,target,now,0);changed=true;
        }
        if(terrainCoverage is null)terrainCoverage=Own(graphics.CreateTexture(new TextureDescriptor(64,64,TextureFormat.RGBA32Float,sampling:new(TextureFilter.Nearest,TextureFilter.Nearest,TextureWrap.ClampToEdge,TextureWrap.ClampToEdge))));
        if(changed)terrainCoverage.Write<Vector4>(terrainCoveragePixels,TextureDataFormat.RGBA32Float);
        foreach(var (c,r) in resident.ToArray())
        {
            var k=ResidentTile(c,r);int level=TerrainLevel(r);bool used=false;
            for(int z=k.Origin.Z;z<k.Origin.Z+k.Span&&!used;z++)for(int x=k.Origin.X;x<k.Origin.X+k.Span;x++)
            {
                var v=terrainCoveragePixels[(z+32)*64+x+32];
                if(v.Y==level || v.X==level&&now-v.Z<TerrainFadeSeconds){used=true;break;}
            }
            if(used || (r.Far?desired.Contains(k):StreamingSettings.Distance(c,eye)<=detailRadius+64))continue;
            resident.Remove(c);r.Dispose();if(!r.Far)meshGeneration?.ReleaseMesh(c,r.Step);
        }
    }
}
