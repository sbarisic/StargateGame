using System.Numerics;
using FishGfx;
using StargateGame.WorldGen;
namespace StargateGame.Client;

enum LandscapeView { None, Elevation, Drainage, Sediment, Geology, Habitats }
sealed partial class SceneRenderer
{
    public LandscapeView LandscapeView { get; private set; }
    public void SetLandscapeView(LandscapeView view)
    {
        if(view==LandscapeView)return;
        LandscapeView=view;
        terrainUpload?.Dispose();terrainUpload=null;
        foreach(var (c,r) in resident){r.Dispose();if(!r.Far)meshGeneration?.ReleaseMesh(c,r.Step);}
        resident.Clear();Array.Clear(terrainCoveragePixels);streamingCount=-1;ResetVisibility();
    }
    Color[] TerrainColors(TerrainMeshData data)
    {
        Color C(Vector3 v)=>new((byte)(Math.Clamp(v.X,0,1)*255),(byte)(Math.Clamp(v.Y,0,1)*255),(byte)(Math.Clamp(v.Z,0,1)*255));
        Vector3[] habitats=[new(.35f,.65f,.2f),new(.05f,.25f,.08f),new(.2f,.45f,.1f),new(.1f,.6f,.8f),new(.55f,.5f,.5f),new(.9f,.95f,1),new(.9f,.8f,.5f),new(.8f,.4f,.12f)];
        return Enumerable.Range(0,data.Positions.Length).Select(i=>
        {
            var m=data.Materials[i]; if(LandscapeView==LandscapeView.None)return new Color((byte)(m.X*255),(byte)(m.Y*255),(byte)(m.Z*255),(byte)(m.W*255));
            var e=data.Landscape.Length==data.Positions.Length?data.Landscape[i]:Vector4.Zero;
            return C(LandscapeView switch
            {
                LandscapeView.Elevation=>Vector3.Lerp(new(.03f,.13f,.25f),new(1,.9f,.65f),Math.Clamp((data.Positions[i].Y+60)/760,0,1)),
                LandscapeView.Drainage=>Vector3.Lerp(new(.1f,.08f,.06f),new(.05f,.65f,1),e.X),
                LandscapeView.Sediment=>Vector3.Lerp(new(.12f,.12f,.15f),new(1,.72f,.2f),Math.Clamp(e.Y/8,0,1)),
                LandscapeView.Geology=>(int)e.Z switch {1=>new(.75f,.32f,.12f),2=>new(.7f,.75f,.65f),_=>new(.3f,.35f,.45f)},
                _=>habitats[Math.Clamp((int)e.W,0,7)]
            });
        }).ToArray();
    }
}
