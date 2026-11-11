using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool PhysicsGunEquipped { get; set; }
    public Vector3? PhysicsGunBeam { get; set; }
    Mesh3D? physicsGunBeamMesh;
    readonly Vertex3[] physicsGunBeamVertices = new Vertex3[12];

    void DrawPhysicsGunBeam(RenderPass pass, Camera camera)
    {
        if (!PhysicsGunEquipped || PhysicsGunBeam is not { } end) return;
        Matrix4x4.Invert(camera.View, out var cameraWorld);
        var start = Vector3.Transform(new Vector3(.19f, -.16f, -.65f), cameraWorld);
        var side = Vector3.Cross(end - start, camera.WorldForwardNormal);
        side = side.LengthSquared() < .00001f ? Vector3.TransformNormal(Vector3.UnitX, cameraWorld) : Vector3.Normalize(side);
        side *= .008f;
        var color = new Color(70, 225, 255);
        physicsGunBeamVertices[0] = new(start - side, color);
        physicsGunBeamVertices[1] = new(end - side, color);
        physicsGunBeamVertices[2] = new(end + side, color);
        physicsGunBeamVertices[3] = new(start - side, color);
        physicsGunBeamVertices[4] = new(end + side, color);
        physicsGunBeamVertices[5] = new(start + side, color);
        for (int i = 0; i < 6; i += 3)
        {
            physicsGunBeamVertices[6+i] = physicsGunBeamVertices[i+2];
            physicsGunBeamVertices[7+i] = physicsGunBeamVertices[i+1];
            physicsGunBeamVertices[8+i] = physicsGunBeamVertices[i];
        }
        physicsGunBeamMesh ??= Own(graphics.CreateMesh3D());
        physicsGunBeamMesh.SetVertices(physicsGunBeamVertices, 12, hasUvs: false);
        pass.DrawMesh(physicsGunBeamMesh, white);
    }

    void DrawPhysicsGun(RenderFrame frame, Camera camera)
    {
        if (!RequestModel("physics-gun", 0)) return;
        PrepareViewmodelTarget();
        var viewCamera = new Camera { Position = camera.Position };
        viewCamera.LookAt(camera.Position + camera.WorldForwardNormal);
        viewCamera.SetPerspective(viewmodelTarget!.Width, viewmodelTarget.Height,
            2 * MathF.Atan(MathF.Tan(MathF.PI / 6) * viewmodelTarget.Width / viewmodelTarget.Height), .015f, 20);
        Matrix4x4.Invert(viewCamera.View, out var cameraWorld);
        using (var pass = frame.BeginPass(viewmodelMultisampleTarget ?? viewmodelTarget,
            new RenderPassDescriptor { View = new(viewCamera), State = OpaqueState,
                ColorLoadAction = RenderLoadAction.Clear, ClearColor = new Color(0,0,0,0), DepthLoadAction = RenderLoadAction.Clear }))
        using (pass.PushModel(Matrix4x4.CreateTranslation(.19f, -.16f, -.47f) * cameraWorld))
        using (shadows.CurrentFrame.Bind(shader, 9))
        using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
        {
            Lighting(shader);
            GateLightingUniforms(shader);
            shader.SetUniform("uTerrain", 0);
            shader.SetUniform("uSkinning", 0);
            shader.SetUniform("uTreeVertices", 0);
            shader.SetUniform("uTreeMode", 0);
            shader.SetUniform("uModelNormal", 5);
            shader.SetUniform("uRifleFinish", 0);
            shader.SetUniform("uArmSkin", 0);
            shader.SetUniform("uTint", Vector3.One);
            shader.SetUniform("uFogDeferred", 1);
            shader.SetUniform("uDhdMatte", 0);
            shader.SetUniform("uDhdLightCount", 0);
            shader.SetUniform("uRockSnow", 0);
            shader.SetUniform("uFoliageCutoff", 0f);
            shader.SetUniform("uTravelPlane", Vector4.Zero);
            shader.SetUniform("uViewmodelShadowEnabled", 0);
            shader.SetUniform("uRoughness", .5f);
            foreach (var part in models["physics-gun"])
            {
                shader.SetUniform("uHasModelNormal", part.normal is null ? 0 : 1);
                shader.SetUniform("uHasMetallicRoughness", part.MetallicRoughness is null ? 0 : 1);
                shader.SetUniform("uMetallicRoughness", 6);
                shader.SetUniform("uModelHeightScale", 0f);
                using var normal = part.normal?.Bind(5);
                using var mr = part.MetallicRoughness?.Bind(6);
                Mesh(pass, part.mesh, part.texture, shader);
            }
            shader.SetUniform("uHasModelNormal", 0);
            shader.SetUniform("uHasMetallicRoughness", 0);
        }
        CompositeViewmodel(frame, camera);
    }
}
