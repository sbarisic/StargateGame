using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    ShaderProgram? propGlassShader, lanternEmissionShader;
    bool HasLanternEmission => lanternEmissionVisible;
    void DrawPropGlass(RenderFrame frame, Camera camera, SceneFrame snapshot, float time)
    {
        if (SurfaceInspection) return;
        var frustum = ViewFrustum.FromCamera(camera);
        var draws = new List<(ModelPart Part, Matrix4x4 Transform, Vector4 Clip, float Distance)>();
        foreach (var body in snapshot.Bodies)
        {
            var definition = World.Definition.Props.FirstOrDefault(p => p.Id == body.Id);
            if (definition is null || !models.TryGetValue(definition.Model, out var parts)) continue;
            if (!frustum.Intersects(new(body.Position-new Vector3(definition.HalfExtent.Length()),body.Position+new Vector3(definition.HalfExtent.Length())))) continue;
            var transform = Matrix4x4.CreateFromQuaternion(body.Rotation) * Matrix4x4.CreateTranslation(body.Position);
            var clip = snapshot.Clips?.FirstOrDefault(c => c.Body == body.Id);
            var plane = clip is null ? Vector4.Zero : new Vector4(clip.Gate.Normal,-Vector3.Dot(clip.Gate.Normal,clip.Gate.Centre));
            foreach (var part in parts.Where(p => p.Transparent))
                draws.Add((part,transform,plane,Vector3.DistanceSquared(body.Position,camera.Position)));
        }
        if (draws.Count == 0) return;
        propGlassShader ??= LoadProgram("world.vert", "prop_glass.frag");
        var p = propGlassShader;
        Lighting(p); GateLightingUniforms(p); PointLightingUniforms(p);
        p.SetUniform("uModelNormal",5); p.SetUniform("uMetallicRoughness",6);
        p.SetUniform("uInspection",0); p.SetUniform("uWeapon",0); p.SetUniform("uPointOnly",0);
        p.SetUniform("uEnabled",0); p.SetUniform("uDhdReceiver",0); p.SetUniform("uDhdLightCount",0);
        p.SetUniform("uFogDeferred",0); p.SetUniform("uTreeMode",0); p.SetUniform("uTreeVertices",0);
        p.SetUniform("uShadowSurfaceBias",.015f); p.SetUniform("uViewmodelShadowEnabled",0);
        Matrix4x4.Invert(camera.View*camera.Projection,out var inverse);
        p.SetUniform("uInverseViewProjection",inverse);
        p.SetUniform("uTargetSize",new Vector2(waterTarget!.Width,waterTarget.Height));
        using var pass = frame.BeginPass(waterTarget,new RenderPassDescriptor {View=new(camera),Time=time,ColorLoadAction=RenderLoadAction.Load,DepthLoadAction=RenderLoadAction.Load,
            State=OpaqueState with {DepthWriteEnabled=false,DepthCompare=CompareFunction.LessOrEqual,BlendEnabled=true,SourceBlend=BlendFactor.SourceAlpha,DestinationBlend=BlendFactor.OneMinusSourceAlpha}});
        using var timing=gpuTimer.Measure(pass,"composition");
        using var sun=shadows.CurrentFrame.Bind(p,9);
        using var gate=gateShadowAtlas?.DepthStencilAttachment.Bind(15);
        using var lamps=propShadowAtlas?.DepthStencilAttachment.Bind(23);
        foreach (var draw in draws.OrderByDescending(d=>d.Distance))
        {
            var part=draw.Part;
            p.SetUniform("uTravelPlane",draw.Clip);
            p.SetUniform("uHasModelNormal",part.normal is null?0:1);
            p.SetUniform("uHasMetallicRoughness",part.MetallicRoughness is null?0:1);
            using var transform=pass.PushModel(draw.Transform);
            using var normal=part.normal?.Bind(5);
            using var mr=part.MetallicRoughness?.Bind(6);
            Mesh(pass,part.mesh,part.texture,p);
        }
    }
    void DrawLanternEmission(RenderPass pass)
    {
        if (lanternFrame is null) return;
        lanternEmissionShader ??= LoadProgram("world.vert","gate_emission.frag");
        var p=lanternEmissionShader;
        Lighting(p);p.SetUniform("uSceneDepth",1);p.SetUniform("uEmissiveOnly",1);
        p.SetUniform("uMaskSize",new Vector2(horizonMask!.Width,horizonMask.Height));
        foreach (var body in lanternFrame.Bodies)
        {
            var definition=World.Definition.Props.FirstOrDefault(d=>d.Id==body.Id);
            if (definition?.Kind!=PropKind.Lantern || !models.TryGetValue(definition.Model,out var parts))continue;
            var clip=lanternFrame.Clips?.FirstOrDefault(c=>c.Body==body.Id);
            p.SetUniform("uTravelPlane",clip is null?Vector4.Zero:new Vector4(clip.Gate.Normal,-Vector3.Dot(clip.Gate.Normal,clip.Gate.Centre)));
            using var transform=pass.PushModel(Matrix4x4.CreateFromQuaternion(body.Rotation)*Matrix4x4.CreateTranslation(body.Position));
            foreach(var part in parts.Where(part=>part.Emission is not null))
            {
                p.SetUniform("uEmission",part.EmissionColor*part.EmissionIntensity);
                Mesh(pass,part.mesh,part.Emission!,p);
            }
        }
    }
}
