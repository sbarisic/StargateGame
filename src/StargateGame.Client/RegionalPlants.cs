using System.Numerics;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    // Original small decorative meshes; share existing foliage batching and wind.
    static FoliageMesh RegionalPlant(bool wood)
    {
        List<Vector3> p=[],n=[];List<Vector2> uv=[];
        void Tri(Vector3 a,Vector3 b,Vector3 c,float part)
        {
            var normal=Vector3.Normalize(Vector3.Cross(b-a,c-a));
            p.AddRange([a,b,c]);n.AddRange([normal,normal,normal]);uv.AddRange([new(part,0),new(part,.5f),new(part,1)]);
        }
        if(wood)
        {
            for(int i=0;i<8;i++)
            {
                float a=i*MathF.Tau/8,b=(i+1)*MathF.Tau/8;
                var u=new Vector3(-1.5f,.12f+MathF.Sin(a)*.12f,MathF.Cos(a)*.12f);var v=new Vector3(-1.5f,.12f+MathF.Sin(b)*.12f,MathF.Cos(b)*.12f);
                Tri(u,v,u+Vector3.UnitX*3,0);Tri(v,v+Vector3.UnitX*3,u+Vector3.UnitX*3,0);
            }
        }
        else
        {
            Tri(new(-.009f,0,0),new(.009f,0,0),new(0,.95f,0),0);
            Tri(new(0,0,-.009f),new(0,0,.009f),new(0,.95f,0),0);
            for(int i=0;i<5;i++)
            {
                float a=i*MathF.Tau/5,b=a+.42f,c=a-.42f;
                var center=new Vector3(0,.94f,0);var tip=new Vector3(MathF.Cos(a)*.13f,1,MathF.Sin(a)*.13f);
                Tri(center,new(MathF.Cos(b)*.095f,.98f,MathF.Sin(b)*.095f),tip,1);
                Tri(center,tip,new(MathF.Cos(c)*.095f,.98f,MathF.Sin(c)*.095f),1);
            }
        }
        return new(p.ToArray(),n.ToArray(),uv.ToArray());
    }
}
