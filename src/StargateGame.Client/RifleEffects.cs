using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    sealed record ShotEffect(RifleShot Shot, double Started);
    readonly List<ShotEffect> rifleEffects = [];
    readonly List<PointLight> rifleLights = [];
    Mesh3D? rifleEffectMesh;
    ShaderProgram? rifleMuzzleShader;
    const double MuzzleDuration = .055;
    Vector3 localMuzzle, localMuzzleForward;
    bool localMuzzleReady;
    long rifleShotCount, rifleFlashFrames;
    double rifleEffectTime;
    int rifleLocal;
    public double RifleTime { get; set; }
    public object RifleDiagnostics => new { shots = rifleShotCount, flashFrames = rifleFlashFrames, lights = rifleLights.Count, effects = rifleEffects.Count,
        muzzle = localMuzzleReady ? (Vector3?)localMuzzle : null,
        lightSources = rifleLights.Select(l => new { l.Position, l.Intensity, shadows = l.Shadows && ShadowsEnabled }),
        shadowUpdates = PointShadowUpdates };
    public void RifleShot(RifleShot shot, double time)
    {
        rifleEffects.Add(new(shot,time)); rifleShotCount++;
        if (rifleEffects.Count > 32) rifleEffects.RemoveAt(0);
    }
    (Vector3 Position, Vector3 Forward) ShotMuzzle(RifleShot shot)
    {
        if (shot.Player == rifleLocal && localMuzzleReady) return (localMuzzle, localMuzzleForward);
        var direction = Vector3.Normalize(shot.End-shot.Origin);
        return (shot.Origin + direction*.65f - Vector3.UnitY*.12f, direction);
    }
    static Matrix4x4 CameraWorld(Camera camera) { Matrix4x4.Invert(camera.View, out var world); return world; }
    void PrepareRifleEffects(Camera camera, SceneFrame snapshot, double time)
    {
        time=RifleTime;
        rifleEffectTime=time; rifleLocal=snapshot.Local.Id;
        rifleEffects.RemoveAll(e => time-e.Started > .22 || time < e.Started);
        localMuzzleReady = false;
        if (riflePoseReady && rifle is not null && Array.IndexOf(rifle.Bones, "Main") is int main && main >= 0)
        {
            var socket = riflePose[main] * rifleWorldTransform;
            localMuzzle = Vector3.Transform(new Vector3(.079f,-.079f,-.759f), socket);
            localMuzzleForward = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, socket));
            localMuzzleReady = true;
        }
        rifleLights.Clear();
        foreach(var e in rifleEffects.Where(e => time-e.Started < MuzzleDuration)
            .GroupBy(e => e.Shot.Player).Select(g => g.Last()).TakeLast(8))
        {
            if (e.Shot.Player == rifleLocal && !localMuzzleReady) continue;
            var muzzle = ShotMuzzle(e.Shot);
            float fade = MuzzleEnvelope(time-e.Started);
            // Light originates in the hot gas just beyond the barrel, sharing
            // the rendered socket and this frame's recoil/aim/bob pose.
            rifleLights.Add(new(Guid.Empty,muzzle.Position+muzzle.Forward*.035f,
                new(1,.72f,.4f),3.5f*fade,4,.045f,true,e.Shot.Player));
        }
        if(rifleLights.Count>0)rifleFlashFrames++;
    }
    static float MuzzleEnvelope(double age) => MathF.Pow(Math.Clamp(1-(float)(age/MuzzleDuration),0,1),1.6f);
    void DrawMuzzleFlame(RenderPass pass, Camera camera, Vector3 position, Vector3 direction, ShotEffect effect, bool viewmodel)
    {
        rifleMuzzleShader ??= LoadProgram("rifle_flash.vert", "rifle_flash.frag");
        rifleMuzzleShader.SetUniform("uViewmodel", viewmodel ? 1 : 0);
        rifleMuzzleShader.SetUniform("uWeaponDepth", 1);
        rifleMuzzleShader.SetUniform("uFlashSeed", (float)(effect.Shot.Id%97));
        rifleMuzzleShader.SetUniform("uFlashEnergy", MuzzleEnvelope(rifleEffectTime-effect.Started));
        var side = Vector3.Normalize(Vector3.Cross(direction, Math.Abs(direction.Y)>.95f ? Vector3.UnitX : Vector3.UnitY));
        var up = Vector3.Normalize(Vector3.Cross(side,direction));
        float length = .18f + (effect.Shot.Id%5)*.012f;
        var vertices = new List<Vertex3>(24);
        void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,Color color)
        {
            vertices.AddRange([new(a,new(0,0),color),new(b,new(0,1),color),new(c,new(1,1),color),
                new(a,new(0,0),color),new(c,new(1,1),color),new(d,new(1,0),color)]);
        }
        // Intersecting soft gas plumes extend along the barrel instead of a
        // camera-facing star. A small hot core remains visible end-on.
        for(int i=0;i<3;i++)
        {
            float angle=i*MathF.PI/3+(effect.Shot.Id%7)*.17f;
            var width=(side*MathF.Cos(angle)+up*MathF.Sin(angle))*.045f;
            Quad(position-width,position+width,position+direction*length+width,position+direction*length-width,Color.White);
        }
        var world=CameraWorld(camera);
        var right=Vector3.TransformNormal(Vector3.UnitX,world)*.022f;
        var vertical=Vector3.TransformNormal(Vector3.UnitY,world)*.022f;
        var core=position+direction*.025f;
        Quad(core-right-vertical,core-right+vertical,core+right+vertical,core+right-vertical,new Color(255,255,255,0));
        rifleEffectMesh ??= Own(graphics.CreateMesh3D());
        rifleEffectMesh.SetVertices(vertices.ToArray(),vertices.Count,hasUvs:true);
        using var state=pass.PushState(pass.State with { CullMode=CullMode.None,DepthWriteEnabled=false,
            BlendEnabled=true,SourceBlend=BlendFactor.One,DestinationBlend=BlendFactor.One });
        Mesh(pass,rifleEffectMesh,white,rifleMuzzleShader);
    }
    void Flash(RenderPass pass, Camera camera, Vector3 position, float size, long seed, ShaderProgram? program = null)
    {
        var world=CameraWorld(camera);
        var right=Vector3.TransformNormal(Vector3.UnitX,world); var up=Vector3.TransformNormal(Vector3.UnitY,world);
        var vertices=new Vertex3[36];
        for(int i=0;i<12;i++)
        {
            Vector3 Rim(int n) { float angle=n*MathF.Tau/12+(seed%7)*.43f; return position+(right*MathF.Cos(angle)+up*MathF.Sin(angle))*size*(n%2==0?1:.28f); }
            vertices[i*3]=new(position,new Color(255,245,195));
            vertices[i*3+1]=new(Rim(i),new Color(255,110,15));
            vertices[i*3+2]=new(Rim(i+1),new Color(255,110,15));
        }
        rifleEffectMesh ??= Own(graphics.CreateMesh3D());
        rifleEffectMesh.SetVertices(vertices,vertices.Length,hasUvs:false);
        using var state=pass.PushState(pass.State with { CullMode=CullMode.None,DepthWriteEnabled=false,BlendEnabled=true,SourceBlend=BlendFactor.One,DestinationBlend=BlendFactor.One });
        if (program is null) pass.DrawMesh(rifleEffectMesh,white);
        else Mesh(pass, rifleEffectMesh, white, program);
    }
    void DrawRifleEffects(RenderPass pass, Camera camera)
    {
        foreach(var e in rifleEffects)
        {
            double age=rifleEffectTime-e.Started;
            if(age<MuzzleDuration && e.Shot.Player!=rifleLocal)
            {
                var muzzle=ShotMuzzle(e.Shot);
                DrawMuzzleFlame(pass,camera,muzzle.Position,muzzle.Forward,e,false);
            }
            if(e.Shot.Hit && age<.16)Flash(pass,camera,e.Shot.End+Vector3.Normalize(e.Shot.Origin-e.Shot.End)*.025f,.035f*(1-(float)(age/.16)),e.Shot.Id);
        }
    }
    void DrawRifleMuzzle(RenderFrame frame, Camera camera, Matrix4x4 transform)
    {
        var effect=rifleEffects.LastOrDefault(e=>e.Shot.Player==rifleLocal && rifleEffectTime-e.Started<MuzzleDuration);
        if(effect is null || !localMuzzleReady)return;
        // World depth uses a different projection. Compare against the rifle's
        // own resolved depth instead; never sample the destination attachment.
        using var depth = (viewmodelGuide?.DepthStencilAttachment ?? viewmodelGbuffer!.DepthStencilAttachment).Bind(1);
        using var pass=frame.BeginPass(SurfaceInspection ? resolvedOpaque! : waterTarget!,new RenderPassDescriptor { View=new(camera), State=OpaqueState with {DepthTestEnabled=false,DepthWriteEnabled=false},ColorLoadAction=RenderLoadAction.Load,DepthLoadAction=RenderLoadAction.Load });
        DrawMuzzleFlame(pass,camera,localMuzzle,localMuzzleForward,effect,true);
    }
}
