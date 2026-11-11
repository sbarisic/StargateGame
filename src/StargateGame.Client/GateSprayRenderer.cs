using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    Mesh3D? gateSprayMesh;
    ShaderProgram? gateSprayShader;
    Texture? gateSprayAtlas, gateSprayDetail, gateSpraySnow;
    public int GateSprayBudget => Settings.Quality switch { GraphicsQuality.Low => 32, GraphicsQuality.High => 96, _ => 64 };
    void ClearGateSpray()
    {
        gateSprayMesh?.Dispose();
        gateSprayMesh = null;
    }
    void PrepareGateSpray()
    {
        if (gateSprayMesh is null)
        {
            gateSprayMesh = graphics.CreateMesh3D();
            var points = new Vector3[96 * 6];
            var uv = new Vector2[points.Length];
            Vector2[] corners = [new(-1, -1), new(1, -1), new(1, 1), new(-1, -1), new(1, 1), new(-1, 1)];
            for (int i = 0; i < 96; i++)
                for (int j = 0; j < 6; j++)
                {
                    points[i * 6 + j] = new(i, 0, 0);
                    uv[i * 6 + j] = corners[j];
                }
            gateSprayMesh.SetVertices(points);
            gateSprayMesh.SetUVs(uv);
        }
        gateSprayShader ??= LoadProgram("gate_spray.vert", "gate_spray.frag");
        Texture Load(string name)
        {
            using var source = new MemoryStream(ReadRuntimeAsset("gate-effects/" + name), writable: false);
            using var bitmap = new System.Drawing.Bitmap(source);
            // The alpha channel is the smoke density; never use the opaque terrain loader.
            return Own(graphics.CreateTextureFromImage(bitmap, new TextureLoadOptions
            {
                MipLevels = 1 + (int)MathF.Log2(Math.Max(bitmap.Width, bitmap.Height)),
                Sampling = new(TextureFilter.LinearMipmapLinear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge)
            }));
        }
        gateSprayAtlas ??= Load("T_SmokeSubUV_8X8.png");
        gateSprayDetail ??= Load("T_Smoke_Tiled_D.png");
        gateSpraySnow ??= Load("T_Snow_fx.png");
    }
    void RenderGateSpray(RenderFrame frame, RenderView view, float time)
    {
        if (visibleHorizons.Length == 0)
            return;
        using var pass = frame.BeginPass(waterTarget!, new RenderPassDescriptor
        {
            View = view,
            Time = time,
            State = OpaqueState with { DepthWriteEnabled = false, BlendEnabled = true, SourceBlend = BlendFactor.One, DestinationBlend = BlendFactor.OneMinusSourceAlpha },
            ColorLoadAction = RenderLoadAction.Load,
            DepthLoadAction = RenderLoadAction.Load
        });
        using (gpuTimer.Measure(pass, "gate-spray"))
            DrawGateSpray(pass, view, false);
    }
    void DrawGateSpray(RenderPass pass, RenderView view, bool emission)
    {
        if (gateSprayShader is null || gateSprayMesh is null)
            return;
        foreach (var gate in visibleHorizons)
        {
            using var endpoint = SelectGate(gate);
            var effect = GateEffect;
            if (effect.SprayAge < GateEffectTimeline.SprayStart || effect.SprayFade <= 0)
                continue;
            var p = gateSprayShader;
            Lighting(p);
            GateTimelineUniforms(p);
            p.SetUniform("uKawooshBloom", KawooshBloomEnabled ? 1f : 0f);
            Matrix4x4.Invert(view.View, out var cameraWorld);
            Matrix4x4.Invert(view.View * view.Projection, out var inverse);
            p.SetUniform("uCameraRight", new Vector3(cameraWorld.M11, cameraWorld.M12, cameraWorld.M13));
            p.SetUniform("uCameraUp", new Vector3(cameraWorld.M21, cameraWorld.M22, cameraWorld.M23));
            p.SetUniform("uInverseViewProjection", inverse);
            p.SetUniform("uTargetSize", emission ? new Vector2(horizonMask!.Width, horizonMask.Height) : new Vector2(waterTarget!.Width, waterTarget.Height));
            p.SetUniform("uParticleAge", effect.SprayAge);
            p.SetUniform("uParticleFade", effect.SprayFade);
            p.SetUniform("uParticleBudget", GateSprayBudget);
            p.SetUniform("uSprayTimes", new Vector4(GateEffectTimeline.SprayStart, GateEffectTimeline.SprayEnd, GateEffectTimeline.SprayLifetimeMin, GateEffectTimeline.SprayLifetimeMax));
            p.SetUniform("uBirthCutoff", effect.ClosingAge >= 0 ? effect.OpeningAge : GateEffectTimeline.SprayEnd);
            p.SetUniform("uBackSheet", 0);
            p.SetUniform("uAtlas", 0);
            p.SetUniform("uSceneDepth", 1);
            p.SetUniform("uDetail", 4);
            p.SetUniform("uSnow", 5);
            p.SetUniform("uEmissiveOnly", emission ? 1 : 0);
            using var depth = opaqueTarget!.DepthStencilAttachment.Bind(1);
            using var detail = gateSprayDetail!.Bind(4);
            using var snow = gateSpraySnow!.Bind(5);
            using var blend = pass.PushState(pass.State with
            {
                DepthWriteEnabled = false,
                BlendEnabled = true,
                SourceBlend = BlendFactor.One,
                DestinationBlend = BlendFactor.OneMinusSourceAlpha
            });
            uint seed = 2166136261;
            foreach (byte value in (AuthoritativeGate?.ConnectionId ?? Guid.Empty).ToByteArray())
                seed = unchecked((seed ^ value) * 16777619);
            foreach (char value in gate.Id)
                seed = unchecked((seed ^ value) * 16777619);
            p.SetUniform("uSeed", (seed & 65535) / 65535f);
            p.SetUniform("uGateScale", gate.Scale);
            using var transform = pass.PushModel(gate.Transform);
            Mesh(pass, gateSprayMesh, gateSprayAtlas!, p);
        }
    }
}
