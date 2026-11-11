using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Building;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    WorldChanges? demolitionState;
    Guid[] demolitionIds=[];
    readonly List<(string Model,Matrix4x4 Transform)> demolitionModels=[];
    Mesh3D? demolitionMesh;
    long demolitionMeshBytes;
    Vector3 demolitionTint;
    RenderTarget? demolitionMask;
    ShaderProgram? demolitionMaskShader,demolitionCompositeShader;
    long DemolitionTargetBytes=>(long)(demolitionMask?.Width??0)*(demolitionMask?.Height??0);

    public void Demolition(WorldChanges? changes,Guid[] ids,bool valid)
    {
        demolitionTint=valid?new(.1f,.9f,.65f):new(1,.18f,.1f);
        if(ReferenceEquals(changes,demolitionState)&&ids.SequenceEqual(demolitionIds))return;
        demolitionState=changes; demolitionIds=ids;
        demolitionModels.Clear();demolitionMesh?.Dispose();demolitionMesh=null;demolitionMeshBytes=0;
        if(changes is null||ids.Length==0)return;
        var owners=ids.ToHashSet();List<BuildTriangle> procedural=[];
        foreach(var piece in changes.Pieces.Where(p=>owners.Contains(p.Id)).Concat(BuildingCorners.Pieces(changes).Where(p=>owners.Contains(p.Parent))))
        {
            var structure=changes.Structures.Single(s=>s.Id==piece.StructureId);
            if(piece.Kind==PieceKind.Prefab)
            {
                // The exact GPU meshes used for scene rendering, including glass,
                // ornaments and the current door pose. Never the collision proxy.
                if(!BuildingBaseCourses.ReplacesOwner(changes,piece.Id))
                    demolitionModels.Add((piece.Prefab,BuildingPrefabs.Transform(structure,piece)));
                procedural.AddRange(BuildingBaseCourses.OwnedMesh(changes,piece.Id));
            }
            else procedural.AddRange(BuildingGeometry.Mesh(structure,piece));
        }
        if(procedural.Count>0){demolitionMesh=BuildingMesh(procedural.ToArray());demolitionMeshBytes=(long)procedural.Count*3*32;}
    }
    void DrawDemolition(RenderFrame frame,Camera camera)
    {
        if(demolitionIds.Length==0||waterTarget is null)return;
        if(demolitionMask is null||demolitionMask.Width!=waterTarget.Width||demolitionMask.Height!=waterTarget.Height)
        {
            demolitionMask?.Dispose();
            demolitionMask=graphics.CreateRenderTarget(new(waterTarget.Width,waterTarget.Height,[TextureFormat.R8Unorm],null));
        }
        demolitionMaskShader??=LoadProgram("world.vert","demolition-mask.frag");
        demolitionCompositeShader??=LoadProgram("fullscreen.vert","demolition-composite.frag");
        demolitionMaskShader.SetUniform("uSceneDepth",1);
        using(waterTarget.DepthStencilAttachment.Bind(1))
        using(var pass=frame.BeginPass(demolitionMask,new RenderPassDescriptor{View=new(camera),State=OpaqueState with{
            DepthTestEnabled=false,DepthWriteEnabled=false},
            ColorLoadAction=RenderLoadAction.Clear,ClearColor=new Color(0,0,0,0)}))
        {
            demolitionMaskShader.SetUniform("uTreeVertices",0);
            demolitionMaskShader.SetUniform("uTreeMode",0);
            demolitionMaskShader.SetUniform("uCutoff",0f);
            if(demolitionMesh is not null)Mesh(pass,demolitionMesh,white,demolitionMaskShader);
            foreach(var (model,transform) in demolitionModels)
                if(models.TryGetValue(model,out var parts))
                    using(pass.PushModel(transform)) foreach(var part in parts)
                    {
                        demolitionMaskShader.SetUniform("uCutoff",part.AlphaCutoff);
                        Mesh(pass,part.mesh,part.texture,demolitionMaskShader);
                    }
        }
        using(var pass=frame.BeginPass(waterTarget,new RenderPassDescriptor{View=new(camera),State=ScreenState with{
            BlendEnabled=true,SourceBlend=BlendFactor.SourceAlpha,DestinationBlend=BlendFactor.OneMinusSourceAlpha},ColorLoadAction=RenderLoadAction.Load}))
        {
            demolitionCompositeShader.SetUniform("uTint",demolitionTint);
            Mesh(pass,screen,demolitionMask.ColorAttachments[0],demolitionCompositeShader);
        }
    }
    void ClearDemolition()
    {
        Demolition(null,[],false);demolitionMask?.Dispose();demolitionMask=null;
    }
}
