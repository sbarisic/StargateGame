using System.Numerics;
using FishGfx;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public AmbientOcclusionSettings AoSettings { get; set; } = new();
    RenderTarget? aoRaw, aoFiltered, resolvedOpaque;
    ShaderProgram? aoHorizonShader, aoFilterShader, aoResolveShader, aoPresentShader;
    public double AoGpuMilliseconds => gpuTimer.AoMilliseconds;
    public long AoTargetBytes => (long)(resolvedOpaque?.Width ?? 0) * (resolvedOpaque?.Height ?? 0) * 20
        + (long)(aoRaw?.Width ?? 0) * (aoRaw?.Height ?? 0) * 2;
    public long CanopyAmbientBytes => prototypeVolumes.Values.Sum(v => v.AmbientBytes);

    void DisposeAmbientOcclusion()
    {
        aoRaw?.Dispose(); aoFiltered?.Dispose(); resolvedOpaque?.Dispose();
        aoRaw = aoFiltered = resolvedOpaque = null;
    }
    void ResolveAmbientOcclusion(RenderFrame frame, Camera camera, RenderView view, Matrix4x4 inverse)
    {
        int width = opaqueTarget!.Width, height = opaqueTarget.Height;
        var quality = AoSettings.Resolve(Settings.Quality);
        bool enabled = quality != AmbientOcclusionQuality.Off;
        int divisor = quality == AmbientOcclusionQuality.High ? 1 : 2;
        int w = Math.Max(1, (width + divisor - 1) / divisor), h = Math.Max(1, (height + divisor - 1) / divisor);
        if (aoRaw?.Width != w || aoRaw.Height != h)
        {
            aoRaw?.Dispose(); aoFiltered?.Dispose();
            aoRaw = graphics.CreateRenderTarget(new(w, h, [TextureFormat.R8Unorm], null));
            aoFiltered = graphics.CreateRenderTarget(new(w, h, [TextureFormat.R8Unorm], null));
        }
        resolvedOpaque ??= graphics.CreateRenderTarget(new(width, height, [TextureFormat.RGBA16Float], null));
        aoHorizonShader ??= RequestOptionalProgram("fullscreen.vert", "ao_horizon.frag");
        aoFilterShader ??= RequestOptionalProgram("fullscreen.vert", "ao_filter.frag");
        aoResolveShader ??= RequestOptionalProgram("fullscreen.vert", "ao_resolve.frag");
        if (aoHorizonShader is null || aoFilterShader is null || aoResolveShader is null)
        {
            opaqueTarget.ColorAttachments[0].CopyTo(resolvedOpaque.ColorAttachments[0]);
            return;
        }
        void Common(ShaderProgram program)
        {
            program.SetUniform("uDepth", 1); program.SetUniform("uNormals", 2);
            program.SetUniform("uInverseViewProjection", inverse); program.SetUniform("uEye", camera.Position);
        }
        using var depth = opaqueTarget.DepthStencilAttachment.Bind(1);
        using var normal = opaqueTarget.ColorAttachments[2].Bind(2);
        if (enabled)
        {
            using (var pass = frame.BeginPass(aoRaw, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
            using (gpuTimer.Measure(pass, "ao"))
            {
                Common(aoHorizonShader);
                aoHorizonShader.SetUniform("uRadius", AoSettings.Radius);
                aoHorizonShader.SetUniform("uProjectionScale", Math.Abs(camera.Projection.M22) * height * .5f);
                aoHorizonShader.SetUniform("uSlices", quality == AmbientOcclusionQuality.Low ? 2 : 3);
                aoHorizonShader.SetUniform("uSteps", quality == AmbientOcclusionQuality.Low ? 3 : quality == AmbientOcclusionQuality.High ? 6 : 4);
                Mesh(pass, screen, white, aoHorizonShader);
            }
            using (var pass = frame.BeginPass(aoFiltered!, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
            using (gpuTimer.Measure(pass, "ao"))
            {
                Common(aoFilterShader); aoFilterShader.SetUniform("uAo", 0);
                Mesh(pass, screen, aoRaw.ColorAttachments[0], aoFilterShader);
            }
        }
        using (var pass = frame.BeginPass(resolvedOpaque, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, "ao"))
        using (opaqueTarget.ColorAttachments[1].Bind(3))
        using ((enabled ? aoFiltered!.ColorAttachments[0] : white).Bind(4))
        using ((enabled ? aoRaw.ColorAttachments[0] : white).Bind(5))
        {
            Common(aoResolveShader);
            aoResolveShader.SetUniform("uColor", 0); aoResolveShader.SetUniform("uAmbient", 3);
            aoResolveShader.SetUniform("uAo", 4); aoResolveShader.SetUniform("uRawAo", 5);
            aoResolveShader.SetUniform("uEnabled", enabled ? 1 : 0);
            aoResolveShader.SetUniform("uStrength", AoSettings.Strength);
            aoResolveShader.SetUniform("uInspection", AoSettings.Inspection switch {
                "lit" => 0, "raw" => 1, "filtered" => 2, "normals" => 3, "ambient" => 4,
                _ => throw new ArgumentException("--ao-view expects lit, raw, filtered, normals or ambient.") });
            Mesh(pass, screen, opaqueTarget.ColorAttachments[0], aoResolveShader);
        }
        // Opaque depth stays authoritative; the colour resolve does not change geometry.
    }
}
