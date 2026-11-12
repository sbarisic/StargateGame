using System.Numerics;
using FishGfx;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public AmbientOcclusionSettings AoSettings { get; set; } = new();
    RenderTarget? aoRaw, aoFiltered, resolvedOpaque;
    ShaderProgram? aoHorizonShader, aoFilterShader, aoPresentShader, deferredShader, deferredMsShader, viewmodelGuideShader;
    Matrix4x4 deferredSurfaceViewProjection;
    readonly Dictionary<string, int> deferredDhdReceivers = [];
    bool SurfaceInspection => AoSettings.Inspection is not ("lit" or "hdr");
    public long GbufferTargetBytes => TargetSize(opaqueTarget, 40);
    public long WeaponDeferredTargetBytes => ViewmodelTargetBytes;
    public double WeaponGpuMilliseconds => gpuTimer.WeaponMilliseconds;
    public double CompositionGpuMilliseconds => gpuTimer.CompositionMilliseconds;
    public double AoGpuMilliseconds => gpuTimer.AoMilliseconds;
    public double DeferredGpuMilliseconds => gpuTimer.DeferredMilliseconds;
    public double GbufferGpuMilliseconds => gpuTimer.GbufferMilliseconds;
    public long AoTargetBytes => TargetSize(aoRaw, 1) + TargetSize(aoFiltered, 1);
    public long DeferredLightingTargetBytes => TargetSize(resolvedOpaque, 12);
    public bool DeferredCaptureReady => deferredShader is not null && optionalPrograms.Count == 0
        && terrainSize == PaletteSize && !buildingModelsPending && modelJobs.Count == 0 && textureRefinements.Count == 0
        && grassTiles.Values.All(tile => coverClock.Elapsed.TotalSeconds - tile.Born >= .4) && grassJobs.Count == 0 && meadowJobs.Count == 0 && surfaceJobs.Count == 0 && grassVolumeUpload is null
        && (!GroundCoverSettings.Enabled || World.Definition.Preset != "temperate"
            || (grassTiles.Count == 0 || (GroundCoverSettings.Hybrid ? grassVolumes is not null && hybridGrassShader is not null && hybridMeshShader is not null : grassShader is not null)));
    static long TargetSize(RenderTarget? target, int bytes) => target is null ? 0 : (long)target.Width * target.Height * bytes * target.SampleCount;
    RenderTarget CreateGbuffer(int width, int height, int samples)
    {
        if (graphics.Capabilities.MaximumColorAttachments < 5 || graphics.Capabilities.MaximumDrawBuffers < 5)
            throw new NotSupportedException($"Deferred rendering requires five color attachments and draw buffers; hardware reports {graphics.Capabilities.MaximumColorAttachments} attachments / {graphics.Capabilities.MaximumDrawBuffers} draw buffers.");
        var target = graphics.CreateRenderTarget(new(width, height,
            [TextureFormat.RGBA16Float, TextureFormat.RGBA16Float, TextureFormat.RGBA16Float, TextureFormat.RGBA16Float, TextureFormat.R32Float],
            TextureFormat.Depth32Float, sampleCount: samples));
        if (samples == 1) SetGuideSampling(target);
        return target;
    }
    static void SetGuideSampling(RenderTarget target)
    {
        var nearest = new TextureSamplingState(TextureFilter.Nearest, TextureFilter.Nearest, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
        foreach (var color in target.ColorAttachments) color.SetSampling(nearest);
        target.DepthStencilAttachment.SetSampling(nearest);
    }
    void DisposeAmbientOcclusion()
    {
        aoRaw?.Dispose(); aoFiltered?.Dispose(); resolvedOpaque?.Dispose();
        aoRaw = aoFiltered = resolvedOpaque = null;
    }
    void PrepareDeferredPrograms()
    {
        // Required programs: never present unlit G-buffer data while shaders are pending.
        aoHorizonShader ??= LoadProgram("fullscreen.vert", "ao_horizon.frag");
        aoFilterShader ??= LoadProgram("fullscreen.vert", "ao_filter.frag");
        deferredShader ??= LoadProgram("fullscreen.vert", "deferred.frag");
        deferredMsShader ??= LoadProgram("fullscreen.vert", "deferred_ms.frag");
        viewmodelGuideShader ??= LoadProgram("fullscreen.vert", "viewmodel_guide.frag");
    }
    void RenderSurfaceAo(RenderFrame frame, Camera camera, Texture depth, Texture normal, float radius,
        ref RenderTarget? raw, ref RenderTarget? filtered, bool weapon = false)
    {
        PrepareDeferredPrograms();
        var quality = AoSettings.Resolve(Settings.Quality);
        int divisor = weapon || quality == AmbientOcclusionQuality.High ? 1 : 2;
        int w = Math.Max(1, (opaqueTarget!.Width + divisor - 1) / divisor), h = Math.Max(1, (opaqueTarget.Height + divisor - 1) / divisor);
        if (raw?.Width != w || raw.Height != h)
        {
            raw?.Dispose(); filtered?.Dispose();
            raw = graphics.CreateRenderTarget(new(w, h, [TextureFormat.R8Unorm], null));
            filtered = graphics.CreateRenderTarget(new(w, h, [TextureFormat.R8Unorm], null));
        }
        if (quality == AmbientOcclusionQuality.Off) return;
        Matrix4x4.Invert(camera.View * camera.Projection, out var inverse);
        using var d = depth.Bind(1); using var n = normal.Bind(2);
        void Common(ShaderProgram p)
        {
            p.SetUniform("uRadius", radius);
            p.SetUniform("uDepth", 1); p.SetUniform("uNormals", 2);
            p.SetUniform("uInverseViewProjection", inverse); p.SetUniform("uEye", camera.Position);
        }
        using (var pass = frame.BeginPass(raw, new RenderPassDescriptor { View = new(camera), State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, weapon ? "weapon-ao" : "ao"))
        {
            Common(aoHorizonShader!);
            aoHorizonShader!.SetUniform("uRadius", radius);
            aoHorizonShader.SetUniform("uProjectionScale", Math.Abs(camera.Projection.M22) * opaqueTarget.Height * .5f);
            aoHorizonShader.SetUniform("uSlices", quality == AmbientOcclusionQuality.Low ? 2 : 3);
            aoHorizonShader.SetUniform("uSteps", quality == AmbientOcclusionQuality.Low ? 3 : quality == AmbientOcclusionQuality.High ? 6 : 4);
            Mesh(pass, screen, white, aoHorizonShader);
        }
        using (var pass = frame.BeginPass(filtered!, new RenderPassDescriptor { View = new(camera), State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, weapon ? "weapon-ao" : "ao"))
        {
            Common(aoFilterShader!); aoFilterShader!.SetUniform("uAo", 0);
            Mesh(pass, screen, raw.ColorAttachments[0], aoFilterShader);
        }
    }
    void LightGbuffer(RenderFrame frame, Camera camera, RenderTarget source, RenderTarget destination,
        Texture guideDepth, Texture guideNormal, RenderTarget? filtered, bool weapon)
    {
        var p = source.SampleCount > 1 ? deferredMsShader! : deferredShader!;
        Matrix4x4.Invert(camera.View * camera.Projection, out var inverse);
        bool enabled = AoSettings.Resolve(Settings.Quality) != AmbientOcclusionQuality.Off;
        using var a = source.ColorAttachments[0].Bind(0);
        using var s = source.ColorAttachments[1].Bind(1);
        using var g = source.ColorAttachments[2].Bind(2);
        using var e = source.ColorAttachments[3].Bind(3);
        using var b = source.ColorAttachments[4].Bind(4);
        using var d = source.DepthStencilAttachment.Bind(5);
        using var ao = (enabled ? filtered!.ColorAttachments[0] : white).Bind(6);
        using var gd = guideDepth.Bind(7); using var gn = guideNormal.Bind(8);
        using var shadow = shadows.CurrentFrame.Bind(p, 9);
        using var gate = gateShadowAtlas?.DepthStencilAttachment.Bind(15);
        using var hand = weapon ? viewmodelShadow?.DepthStencilAttachment.Bind(29) : null;
        Lighting(p); GateLightingUniforms(p); PointLightingUniforms(p);
        using var propShadow = propShadowAtlas?.DepthStencilAttachment.Bind(23);
        p.SetUniform("uPointOnly", 0);
        p.SetUniform("uFogDeferred", 0);
        p.SetUniform("uGroundFillWeight", Math.Clamp(VolumeTreeSettings.GroundFillWeight, 0, 1));
        if (source.SampleCount > 1)
            for (int i = 0; i < 4; i++) p.SetUniform($"uSamplePositions[{i}]", viewmodelSamplePositions[i]);
        p.SetUniform("uGAlbedo", 0); p.SetUniform("uGShading", 1); p.SetUniform("uGGeometry", 2);
        p.SetUniform("uGEmission", 3); p.SetUniform("uGBaseDepth", 4); p.SetUniform("uGDepth", 5);
        p.SetUniform("uAo", 6); p.SetUniform("uGuideDepth", 7); p.SetUniform("uGuideNormal", 8);
        p.SetUniform("uAoRadius", weapon ? .1f : AoSettings.Radius);
        p.SetUniform("uEnabled", enabled ? 1 : 0); p.SetUniform("uStrength", AoSettings.Strength);
        p.SetUniform("uInverseViewProjection", inverse); p.SetUniform("uWeapon", weapon ? 1 : 0);
        p.SetUniform("uInspection", AmbientOcclusionSettings.InspectionId(AoSettings.Inspection));
        p.SetUniform("uDhdReceiver", 0); p.SetUniform("uDhdLightCount", 0);
        p.SetUniform("uShadowSurfaceBias", weapon ? .015f : .025f);
        p.SetUniform("uViewmodelShadowEnabled", weapon && viewmodelShadow is not null && ShadowsEnabled && !HandShadowBaseline && !PhysicsGunEquipped ? 1 : 0);
        p.SetUniform("uViewmodelShadow", 29); p.SetUniform("uViewmodelShadowMatrix", viewmodelShadowMatrix);
        using (var pass = frame.BeginPass(destination, new RenderPassDescriptor { View = new(camera), State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, weapon ? "weapon" : "deferred"))
            // DrawMesh binds its texture to unit zero; it must be the matching sampler dimension.
            Mesh(pass, screen, source.ColorAttachments[0], p);
        if (propLights.Count > 0 && (!SurfaceInspection || AoSettings.Inspection is "specular" or "transmission"))
        {
            using var pass = frame.BeginPass(destination, new RenderPassDescriptor { View = new(camera), State = ScreenState with { BlendEnabled = true, SourceBlend = BlendFactor.One, DestinationBlend = BlendFactor.One }, ColorLoadAction = RenderLoadAction.Load });
            using var timing = gpuTimer.Measure(pass, "point-light");
            p.SetUniform("uPointOnly", 1);
            Mesh(pass, screen, source.ColorAttachments[0], p);
            p.SetUniform("uPointOnly", 0);
        }
        if (!weapon && (!SurfaceInspection || AoSettings.Inspection == "specular"))
        {
            using var pass = frame.BeginPass(destination, new RenderPassDescriptor { View = new(camera), State = ScreenState with { BlendEnabled = true, SourceBlend = BlendFactor.One, DestinationBlend = BlendFactor.One }, ColorLoadAction = RenderLoadAction.Load });
            using var timing = gpuTimer.Measure(pass, "deferred");
            foreach (var entity in World.Definition.Entities.Concat(testingInspection).Where(e => e.Model == "dhd"))
            {
                using var endpoint = SelectGate(entity);
                int count = DhdLightingUniforms(p, entity); if (count == 0) continue;
                p.SetUniform("uDhdLightCount", count); p.SetUniform("uDhdReceiver", deferredDhdReceivers[entity.Id]);
                Mesh(pass, screen, source.ColorAttachments[0], p);
            }
            p.SetUniform("uDhdReceiver", 0); p.SetUniform("uDhdLightCount", 0);
        }
    }
    void RenderDeferredWorld(RenderFrame frame, Camera camera)
    {
        RenderSurfaceAo(frame, camera, opaqueTarget!.DepthStencilAttachment, opaqueTarget.ColorAttachments[2], AoSettings.Radius, ref aoRaw, ref aoFiltered);
        resolvedOpaque ??= graphics.CreateRenderTarget(new(opaqueTarget.Width, opaqueTarget.Height, [TextureFormat.RGBA16Float], TextureFormat.Depth32Float));
        LightGbuffer(frame, camera, opaqueTarget, resolvedOpaque, opaqueTarget.DepthStencilAttachment, opaqueTarget.ColorAttachments[2], aoFiltered, false);
    }
}
