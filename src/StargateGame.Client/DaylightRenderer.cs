using System.Diagnostics;
using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using FishGfx.Graphics.Shadows;
using StargateGame.Shared;
using StargateGame.Physics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    readonly Dictionary<string, AxisAlignedBoundingBox> modelBounds = [];
    DirectionalShadowRenderer shadows = null!;
    Vector3 lastSunShadowDirection;
    ShaderProgram aerialShader = null!;
    bool deferOpaqueFog;
    ShaderProgram depthShader = null!, waterShader = null!, toneShader = null!, aaShader = null!;
    Texture[] normals = [];
    Mesh3D screen = null!;
    RenderTarget? opaqueTarget, waterTarget, toneTarget;
    GpuFrameTimer gpuTimer = null!;
    long frameNumber;
    SceneFrame? previousFrame;
    public GraphicsSettings Settings
    {
        get;
        private set;
    } = new(GraphicsQuality.Performance);
    public bool ShadowsEnabled
    {
        get;
        set;
    } = true;
    public int DrawCalls
    {
        get;
        private set;
    }
    public int ShadowUpdates
    {
        get;
        private set;
    }
    public double RenderCpuMilliseconds
    {
        get;
        private set;
    }
    public double RenderGpuMilliseconds => gpuTimer.Milliseconds;
    public double HorizonWaveGpuMilliseconds => gpuTimer.WaveMilliseconds;
    public double GateSprayGpuMilliseconds => gpuTimer.SprayMilliseconds;
    public long TargetBytes => DemolitionTargetBytes + AoTargetBytes + DeferredLightingTargetBytes + AtmosphereBytes + VolumeBytes + WeatherTargetBytes + PointLightTargetBytes + GateLightTargetBytes + HorizonTargetBytes + (long)(opaqueTarget?.Width ?? 0) * (opaqueTarget?.Height ?? 0) * 56
        + (long)Settings.Shadows.CascadeCount * (Settings.Shadows.Resolution * Settings.Shadows.Resolution + (Settings.Shadows.Resolution / 2) * (Settings.Shadows.Resolution / 2)) * 4
        + (long)(viewmodelShadow?.Width ?? 0) * (viewmodelShadow?.Height ?? 0) * 4 + ViewmodelTargetBytes + ExposureTargetBytes;
    public sealed record SceneFrame(PlayerState[] Remotes, BodyState[] Bodies, PlayerState Local, Guid LocationId = default, long PhysicsEpoch = 0, double SimulationTime = 0, double EnvironmentSeconds = 0, PropClip[]? Clips = null);

    ShaderProgram LoadProgram(string vertex, string fragment)
    {
        string Source(string file)
        {
            string source = System.Text.Encoding.UTF8.GetString(ReadRuntimeAsset("shaders/" + file));
            source = System.Text.RegularExpressions.Regex.Replace(source, @"^\s*#include ""([^""]+)""", m => Source(m.Groups[1].Value), System.Text.RegularExpressions.RegexOptions.Multiline);
            if (file == "bruneton/definitions.glsl")
                source = source.Replace("sampler3D", "sampler2D");
            if (file == "bruneton/functions.glsl")
                source = source.Replace("texture(", "atmosphereTexture(");
            return source;
        }
        var compileClock = System.Diagnostics.Stopwatch.StartNew();
        string vertexSource = Source(vertex), fragmentSource = Source(fragment);
        string cachePath = ShaderCachePath(vertexSource, fragmentSource);
        if (ReadShaderCache(cachePath) is { } cached)
        {
            StartupTrace.Mark("shader-cache-hit", compileClock.Elapsed.TotalMilliseconds, vertex + " / " + fragment);
            return cached;
        }
        ShaderProgram program;
        try
        {
            var vs = Own(graphics.CreateShaderStage(ShaderStageType.Vertex, vertexSource));
            var fs = Own(graphics.CreateShaderStage(ShaderStageType.Fragment, fragmentSource));
            program = Own(graphics.CreateShaderProgram(vs, fs));
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Failed to prepare shader program '{vertex}' / '{fragment}'.", ex);
        }
        SaveShaderCache(cachePath, program);
        StartupTrace.Mark("shader-compile-link", compileClock.Elapsed.TotalMilliseconds, vertex + " / " + fragment);
        return program;
    }
    IEnumerable<bool> InitializeLighting()
    {
        shadows = Own(new DirectionalShadowRenderer(graphics, Settings.Shadows));
        yield return false;
        depthShader = LoadProgram("depth.vert", "depth.frag");
        yield return false;
        aerialShader = LoadProgram("fullscreen.vert", "aerial_composite.frag");
        aerialShader.SetUniform("uOpaqueColor", 0);
        aerialShader.SetUniform("uOpaqueDepth", 1);
        yield return false;
        realisticWaterShader = LoadProgram("water.vert", "water.frag");
        yield return false;
        legacyWaterShader = LoadProgram("water.vert", "water_legacy.frag");
        waterShader = legacyWaterShader;
        yield return false;
        toneShader = LoadProgram("fullscreen.vert", "tonemap.frag");
        toneShader.SetUniform("uHorizonBloom", 1);
        toneShader.SetUniform("uExposure", 2);
        yield return false;
        foreach (bool step in InitializeExposure()) yield return step;
        aaShader = LoadProgram("fullscreen.vert", "fxaa.frag");
        yield return false;
        normals = new Texture[4];
        string[] maps = ["grass-normal.png", "cliff-normal.png", "sand-normal.png", "snow-normal.png"];
        for (int i = 0; i < maps.Length; i++)
        {
            int slot = i;
            foreach (bool step in PrepareLegacyTexture(maps[i], "terrain", t => normals[slot] = t))
                yield return step;
        }
        shader.SetUniform("uGrassNormal", 5);
        shader.SetUniform("uCliffNormal", 6);
        shader.SetUniform("uSandNormal", 7);
        shader.SetUniform("uSnowNormal", 8);
        shader.SetUniform("uTerrainColor", 26);
        shader.SetUniform("uTerrainNormal", 27);
        shader.SetUniform("uTerrainMasks", 28);
        waterShader.SetUniform("uOpaqueColor", 0);
        waterShader.SetUniform("uOpaqueDepth", 1);
        // Initialize unused array entries too: Low uses only two cascades, and
        // active samplers must never alias the albedo/depth samplers' units.
        shader.SetUniform("uShadowMaps", new int[] { 9, 9, 9 });
        shader.SetUniform("uDynamicShadowMaps", new int[] { 9, 9, 9 });
        waterShader.SetUniform("uShadowMaps", new int[] { 2, 2, 2 });
        waterShader.SetUniform("uDynamicShadowMaps", new int[] { 2, 2, 2 });
        realisticWaterShader.SetUniform("uOpaqueColor", 0);
        realisticWaterShader.SetUniform("uOpaqueDepth", 1);
        realisticWaterShader.SetUniform("uWaterNormals", 8);
        realisticWaterShader.SetUniform("uShadowMaps", new int[] { 2, 2, 2 });
        realisticWaterShader.SetUniform("uDynamicShadowMaps", new int[] { 2, 2, 2 });
        screen = Own(graphics.CreateMesh3D());
        screen.SetVertices([new(-1, -1, 0), new(3, -1, 0), new(-1, 3, 0)]);
        gpuTimer = Own(new GpuFrameTimer(graphics));
    }
    public void SetGraphics(GraphicsSettings settings)
    {
        if (settings == Settings)
            return;
        shadows.SetOptions(settings.Shadows);
        Settings = settings;
    }
    void DisposeTargets()
    {
        DisposeAmbientOcclusion();
        DisposeViewmodelTargets();
        ClearVolumes();
        DisposeHorizonTargets();
        DisposeGateLights();
        DisposePointLights();
        toneTarget?.Dispose();
        waterTarget?.Dispose();
        opaqueTarget?.Dispose();
        toneTarget = null;
        waterTarget = null;
        opaqueTarget = null;
    }
    void EnsureTargets(int width, int height)
    {
        if (opaqueTarget?.Width == width && opaqueTarget.Height == height)
            return;
        DisposeTargets();
        try
        {
            opaqueTarget = CreateGbuffer(width, height, 1);
            waterTarget = graphics.CreateRenderTarget(new(width, height, [TextureFormat.RGBA16Float], TextureFormat.Depth32Float));
            toneTarget = graphics.CreateRenderTarget(new(width, height, [TextureFormat.RGBA8Unorm], null));
        }
        catch
        {
            DisposeTargets();
            throw;
        }
    }
    public float DaylightDiagnosticScale
    {
        get;
        set;
    } = 1;
    void Lighting(ShaderProgram program)
    {
        program.SetUniform("uSurfaceViewProjection", deferredSurfaceViewProjection);
        program.SetUniform("uModelHeightScale", 0f);
        program.SetUniform("uModelEmission", 7);
        program.SetUniform("uModelEmissionGain", Vector3.Zero);
        program.SetUniform("uModelParallaxSteps", Settings.Quality switch
        {
            GraphicsQuality.Low => 16,
            GraphicsQuality.High => 48,
            _ => 32
        });
        program.SetUniform("uModelParallaxRange", Settings.Quality switch
        {
            GraphicsQuality.Low => 18f,
            GraphicsQuality.High => 40f,
            _ => 28f
        });
        AtmosphereUniforms(program);
        program.SetUniform("uFogDeferred", deferOpaqueFog ? 1 : 0);
        var light = Daylight.For(World.Definition.Preset);
        if (World.Definition.Preset == "desert")
        {
            light = light with
            {
                Fog = Vector3.Zero
            };
            InteriorUniforms(program, grassEye);
        }
        if (weatherFrame.Snow)
            light = light with
            {
                SunColor = light.SunColor * weatherFrame.SunScale,
                AmbientSky = light.AmbientSky * weatherFrame.AmbientScale,
                AmbientGround = light.AmbientGround * weatherFrame.AmbientScale,
                Zenith = new Vector3(.20f, .24f, .30f) * (1 - weatherFrame.Gust * .12f),
                Horizon = new Vector3(.43f, .48f, .54f) * (1 - weatherFrame.Gust * .10f),
                Fog = weatherFrame.Fog
            };
        if(World.Definition.Fixture is null && !weatherFrame.Snow)
            light=light with {Fog=light.Fog*new Vector3(World.Definition.Preset == "desert" ? .20f : .10f, .12f, 1)};
        program.SetUniform("uOvercast", weatherFrame.Snow ? 1f : 0f);
        program.SetUniform("uGust", weatherFrame.Gust);
        program.SetUniform("uWeatherCloudOffset", weatherCloudOffset);
        program.SetUniform("uSunDiscScale", weatherFrame.Snow ? .08f : 1f);
        program.SetUniform("uSunDirection", environmentFrame.SunDirection);
        program.SetUniform("uSunColor", AtmosphereIllumination.SunMultiplier(environmentFrame.SunDirection.Y, World.Definition.Preset) * (weatherFrame.Snow ? weatherFrame.SunScale : 1) * DaylightDiagnosticScale);
        program.SetUniform("uZenith", light.Zenith * DaylightDiagnosticScale);
        program.SetUniform("uHorizon", light.Horizon * DaylightDiagnosticScale);
        program.SetUniform("uAmbientSky", Vector3.One * (weatherFrame.Snow ? weatherFrame.AmbientScale : 1) * DaylightDiagnosticScale);
        program.SetUniform("uAmbientTint", AtmosphereIllumination.AmbientTint(World.Definition.Preset));
        program.SetUniform("uAmbientGround", AtmosphereIllumination.TintAmbient(light.AmbientGround, World.Definition.Preset) * Math.Clamp(environmentFrame.SunDirection.Y + .15f, .08f, 1) * DaylightDiagnosticScale);
        program.SetUniform("uSkyGroundBounce", AtmosphereIllumination.GroundBounce(environmentFrame.SunDirection.Y));
        program.SetUniform("uGroundLevel", World.Definition.Spawns[0].Y);
        program.SetUniform("uFog", light.Fog);
    }
    static readonly RenderState OpaqueState = RenderState.Default with { CullMode = CullMode.None, BlendEnabled = false };
    static readonly RenderState ScreenState = RenderState.Default with { CullMode = CullMode.None, BlendEnabled = false, DepthTestEnabled = false, DepthWriteEnabled = false };
    void Mesh(RenderPass pass, Mesh3D mesh, Texture texture, ShaderProgram program)
    {
        pass.DrawMesh(mesh, texture, program);
        DrawCalls++;
    }

    public Vector2? InspectionResolution { get; set; }
    public void RenderScene(RenderFrame frame, Camera camera, SceneFrame snapshot, float time, IEnumerable<DebugLine>? debug)
    {
        if (graphics.Backbuffer.Width <= 0 || graphics.Backbuffer.Height <= 0)
            return;
        PrepareTreePrototype();
        var watch = Stopwatch.StartNew();
        DrawCalls = 0;
        ShadowUpdates = 0;
        frameNumber++;
        treeCamera = camera.Position;
        int width = (int)(InspectionResolution?.X ?? graphics.Backbuffer.Width), height = (int)(InspectionResolution?.Y ?? graphics.Backbuffer.Height);
        treeFocalPixels = Math.Abs(camera.Projection.M22) * height * .5f;
        EnsureTargets(width, height);
        PrepareCharacters(snapshot, time);
        PrepareTerrainMaterials();
        if (GroundCoverSettings.Enabled && World.Definition.Preset == "temperate")
            LoadFoliage();
        using var surfaceBinding = BindSurface();
        ResetVisibility();
        gpuTimer.StartFrame(frameNumber);
        PrepareWeather(snapshot, time);
        PrepareAtmosphere(frame, camera, snapshot);
        static bool SamePlayers(PlayerState[] a, PlayerState[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.Position == p.Second.Position && p.First.Yaw == p.Second.Yaw);
        if (previousFrame is null || snapshot.SimulationTime != previousFrame.SimulationTime || !snapshot.Bodies.SequenceEqual(previousFrame.Bodies)
            || !SamePlayers(snapshot.Remotes, previousFrame.Remotes) || snapshot.Local.Position != previousFrame.Local.Position || snapshot.Local.Yaw != previousFrame.Local.Yaw)
            shadows.NotifyDynamicActorsChanged();
        previousFrame = snapshot;
        // FishGfx takes the direction travelled by light, while shaders take the direction to the sun.
        int resolution = Settings.Quality == GraphicsQuality.Low ? 1024 : Settings.Quality == GraphicsQuality.High ? 2048 : 1536;
        if (Vector3.DistanceSquared(lastSunShadowDirection, environmentFrame.SunDirection) > 1f / (resolution * resolution))
        {
            shadows.InvalidateAll();
            lastSunShadowDirection = environmentFrame.SunDirection;
        }
        shadows.Prepare(camera, -environmentFrame.SunDirection, ShadowsEnabled ? 1 : 0, frameNumber);
        foreach (int index in shadows.CascadesNeedingRender)
        {
            using (var pass = shadows.BeginCascadePass(frame, index))
            using (pass.PushState(pass.State with
            {
                CullMode = CullMode.None
            }))
            using (gpuTimer.Measure(pass))
                DrawStatic(pass, depthShader, shadows.GetPendingCascade(index).CasterFrustum, true);
            shadows.CompleteCascade(index);
            ShadowUpdates++;
        }
        foreach (int index in shadows.DynamicCascadesNeedingRender)
        {
            using (var pass = shadows.BeginDynamicCascadePass(frame, index))
            using (pass.PushState(pass.State with
            {
                CullMode = CullMode.None
            }))
            using (gpuTimer.Measure(pass))
            {
                DrawActors(pass, depthShader, shadows.GetDynamicCascade(index).CasterFrustum, snapshot, true);
                DrawCables(pass, depthShader, shadows.GetDynamicCascade(index).CasterFrustum, true);
            }
            shadows.CompleteDynamicCascade(index);
            ShadowUpdates++;
        }
        Matrix4x4.Invert(camera.View * camera.Projection, out var inverse);
        var view = new RenderView(camera);
        var frustum = ViewFrustum.FromCamera(camera);
        UpdateDialLights();
        PrepareHorizons(snapshot, time, frustum);
        CheckHorizonWaves(frame, view);
        AdvanceHorizonWaves(frame, view, time);
        PrepareGateLights(frame, camera, snapshot, time);
        PreparePointLights(frame, camera, snapshot, time);
        PrepareDhds(frustum);
        // The scattering volume samples this frame's shelter map too.
        PrepareSnow(frame, camera);
        PrepareGroundMist(frame, camera);
        PrepareVolumes(frame, camera, snapshot, time);
        using var atmosphereBindings = AtmosphereBindings();
        waterShader = RealisticWater ? realisticWaterShader : legacyWaterShader;
        GateLightingUniforms(shader);
        GateLightingUniforms(waterShader);
        PointLightingUniforms(waterShader);
        CheckWeatherShelter(frame, view);
        CheckLandscape(frame, view);
        deferredSurfaceViewProjection = camera.View * camera.Projection;
        deferredDhdReceivers.Clear();
        foreach (var entity in World.Definition.Entities.Concat(testingInspection).Where(e => e.Model == "dhd"))
            if (!deferredDhdReceivers.ContainsKey(entity.Id)) deferredDhdReceivers.Add(entity.Id, 10 + deferredDhdReceivers.Count);
        if (deferredDhdReceivers.Count > 2038) throw new InvalidOperationException("Deferred DHD receiver IDs exceed half-float precision.");
        deferOpaqueFog = true;
        using (var pass = frame.BeginPass(opaqueTarget!, new RenderPassDescriptor
        {
            View = view,
            State = OpaqueState,
            Time = time,
            ClearColor = new Color(0, 0, 0, 0),
            ColorLoadAction = RenderLoadAction.Clear,
            DepthLoadAction = RenderLoadAction.Clear
        }))
        {
            Lighting(shader);
            using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
            using (shadows.CurrentFrame.Bind(shader, 9))
            {
                DrawStatic(pass, shader, frustum, false);
                using (gpuTimer.Measure(pass, "gbuffer"))
                    DrawCables(pass, shader, frustum, false);
                using (gpuTimer.Measure(pass, "character"))
                    DrawActors(pass, shader, frustum, snapshot, false);
            }
            using (gpuTimer.Measure(pass, "canopy"))
                DrawPrototypeVolume(pass, camera, inverse);
        }
        // Separate depth snapshot avoids sampling an attached render target.
        opaqueTarget!.DepthStencilAttachment.CopyTo(waterTarget!.DepthStencilAttachment);
        using (var pass = frame.BeginPass(opaqueTarget!, new RenderPassDescriptor
        {
            View = view,
            State = OpaqueState,
            Time = time,
            ColorLoadAction = RenderLoadAction.Load,
            DepthLoadAction = RenderLoadAction.Load
        }))
        using (gpuTimer.Measure(pass, "grass"))
        {
            DrawGroundCover(pass, frustum, camera, inverse);
            DrawFoliage(pass);
        }
        RenderDeferredWorld(frame, camera);
        opaqueTarget!.DepthStencilAttachment.CopyTo(resolvedOpaque!.DepthStencilAttachment);
        // Specialized gate surfaces render against the surface depth, never into material buffers.
        if (!SurfaceInspection)
        using (var pass = frame.BeginPass(resolvedOpaque, new RenderPassDescriptor { View = view, State = OpaqueState, Time = time,
            ColorLoadAction = RenderLoadAction.Load, DepthLoadAction = RenderLoadAction.Load }))
        {
            using var timing = gpuTimer.Measure(pass, "composition");
            DrawHorizons(pass, false, time);
        }
        // Separate attachments: no sampled image is attached to the current draw framebuffer.
        deferOpaqueFog = false;
        bool hasWater = WaterSurfaceData.TrySample(terrainStore, camera.Position.X, camera.Position.Z, out var cameraWater);
        float immersion = hasWater ? Math.Clamp((cameraWater.Height - camera.Position.Y) / .12f, 0, 1) : 0;
        using (resolvedOpaque!.DepthStencilAttachment.Bind(1))
        using (var pass = frame.BeginPass(waterTarget!, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, "fog"))
        {
            Lighting(aerialShader);
            WaterUniforms(aerialShader);
            aerialShader.SetUniform("uInverseViewProjection", inverse);
            aerialShader.SetUniform("uUnderwater", immersion);
            aerialShader.SetUniform("uWaterHeight", cameraWater.Height);
            Mesh(pass, screen, resolvedOpaque!.ColorAttachments[0], aerialShader);
        }
        resolvedOpaque!.DepthStencilAttachment.CopyTo(waterTarget!.DepthStencilAttachment);
        // Water owns the visible surface depth, so subsequent snowflakes cannot
        // show through it. Transmission still reads the separate opaque depth.
        long waterCpuStart = Stopwatch.GetTimestamp(), waterAllocatedStart = GC.GetAllocatedBytesForCurrentThread();
        using (var pass = frame.BeginPass(waterTarget, new RenderPassDescriptor
        {
            View = view,
            State = OpaqueState,
            Time = time,
            ColorLoadAction = RenderLoadAction.Load,
            DepthLoadAction = RenderLoadAction.Load
        }))
        using (gpuTimer.Measure(pass, "water"))
        using (resolvedOpaque!.DepthStencilAttachment.Bind(1))
        using (gateShadowAtlas?.DepthStencilAttachment.Bind(15))
        using (propShadowAtlas?.DepthStencilAttachment.Bind(23))
        using (shadows.CurrentFrame.Bind(waterShader, 2))
        using ((waterNormals ?? buildingNormal).Bind(8))
        {
            Lighting(waterShader);
            WaterUniforms(waterShader);
            waterShader.SetUniform("uShadowSurfaceBias", .025f);
            waterShader.SetUniform("uInverseViewProjection", inverse);
            waterShader.SetUniform("uUnderwater", immersion);
            waterShader.SetUniform("uWaterReflections", WaterReflections && Settings.Quality != GraphicsQuality.Low ? 1 : 0);
            waterShader.SetUniform("uViewProjection", camera.View * camera.Projection);
            waterShader.SetUniform("uTargetSize", new Vector2(waterTarget.Width, waterTarget.Height));
            using var coverage=BindTerrainCoverage(waterShader);
            foreach (var r in resident.Values)
                if (r.Water is not null && frustum.Intersects(r.Bounds))
                { waterShader.SetUniform("uTerrainCoverageLevel",TerrainLevel(r)); Mesh(pass, r.Water, resolvedOpaque!.ColorAttachments[0], waterShader); }
            waterShader.SetUniform("uTerrainCoverageLevel",0);
        }
        WaterCpuMilliseconds = Stopwatch.GetElapsedTime(waterCpuStart).TotalMilliseconds;
        WaterAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - waterAllocatedStart;
        DrawPropGlass(frame, camera, snapshot, time);
        DrawSnow(frame, view, camera, inverse, time);
        DrawDesertDust(frame, view, camera, inverse, time);
        RenderGateSpray(frame, view, time);
        RenderExposure(frame, view, snapshot.LocationId);
        using (var pass = frame.BeginPass(waterTarget!, new RenderPassDescriptor { View = view, State = OpaqueState, Time = time, ColorLoadAction = RenderLoadAction.Load, DepthLoadAction = RenderLoadAction.Load }))
        {
            if (debug is not null)
                DrawPhysics(pass, debug);
            else if (DebugWorld is not null)
                DrawCachedPhysics(pass, camera);
            DrawBuildPreview(pass);
            DrawPhysicsGunBeam(pass, camera);
        }
        DrawDemolition(frame, camera);
        DrawViewmodel(frame, camera, snapshot, time);
        RenderHorizonBloom(frame, view, time);
        using (ExposureTexture.Bind(2))
        using (var pass = frame.BeginPass(toneTarget!, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, "tonemap"))
        using ((horizonMask?.ColorAttachments[0] ?? white).Bind(1))
        {
            toneShader.SetUniform("uBloomStrength", horizonMask is null ? 0 : HorizonSettings.BloomStrength);
            Mesh(pass, screen, waterTarget.ColorAttachments[0], toneShader);
        }
        RenderCpuMilliseconds = watch.Elapsed.TotalMilliseconds;
    }
    public void PresentScene(RenderPass pass)
    {
        using (pass.PushState(ScreenState))
        using (gpuTimer.Measure(pass))
        {
            if (AoSettings.Inspection != "lit" && resolvedOpaque is not null)
            {
                aoPresentShader ??= LoadProgram("fullscreen.vert", "ao_present.frag");
                aoPresentShader.SetUniform("uTexture", 0);
                Mesh(pass, screen, (SurfaceInspection ? resolvedOpaque : waterTarget!).ColorAttachments[0], aoPresentShader);
            }
            else Mesh(pass, screen, toneTarget!.ColorAttachments[0], aaShader);
        }
        gpuTimer.EndFrame();
    }
    void DrawStatic(RenderPass pass, ShaderProgram program, ViewFrustum frustum, bool depth)
    {
        if (!depth)
            program.SetUniform("uDhdLightCount", 0);
        program.SetUniform("uFoliageCutoff", 0f);
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uRockSnow", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
        program.SetUniform("uModelHeightScale", 0f);
        program.SetUniform("uShadowSurfaceBias", .025f);
        var terrainMeasurement = depth ? null : gpuTimer.Measure(pass, "terrain");
        program.SetUniform("uTerrain", 1);
        program.SetUniform("uTint", Vector3.One);
        program.SetUniform("uRoughness", .8f);
        if (terrain is not null)
            Mesh(pass, terrain, white, program);
        using var terrainLodBinding=BindTerrainCoverage(program);
        if (depth)
        {
            foreach (var r in Candidates(frustum).Chunks)
            { program.SetUniform("uTerrainCoverageLevel",TerrainLevel(r)); Mesh(pass, r.Terrain, white, program); }
        }
        else
        {
            MeadowUniforms(program);
            using var meadow = BindMeadow();
            using var pbr = BindTerrainPbr(program);
            using var t1 = grassTexture.Bind(1);
            using var t2 = cliffTexture.Bind(2);
            using var t3 = sandTexture.Bind(3);
            using var t4 = snowTexture.Bind(4);
            using var n1 = normals[0].Bind(5);
            using var n2 = normals[1].Bind(6);
            using var n3 = normals[2].Bind(7);
            using var n4 = normals[3].Bind(8);
            program.SetUniform("uTerrain", 2);
            foreach (var r in Candidates(frustum).Chunks)
            { program.SetUniform("uTerrainCoverageLevel",TerrainLevel(r)); Mesh(pass, r.Terrain, white, program); }
        }
        program.SetUniform("uTerrainCoverageLevel",0);
        terrainMeasurement?.Dispose();
        using var sceneryMeasurement = depth ? null : gpuTimer.Measure(pass, "gbuffer");
        program.SetUniform("uShadowSurfaceBias", .015f);
        // Weathered gate and DHD surfaces: broad, subdued highlights instead of polished metal.
        program.SetUniform("uTerrain", 0);
        program.SetUniform("uRoughness", .84f);
        foreach (var entity in World.Definition.Entities.Concat(testingInspection))
        {
            using var endpoint = SelectGate(entity);
            program.SetUniform("uModelEmissionGain", Vector3.Zero);
            if (!models.ContainsKey(entity.Model))
            {
                DrawModelProxy(pass, program, entity.Model, entity.Transform);
                continue;
            }
            var b = modelBounds[entity.Model];
            var bounds = AxisAlignedBoundingBox.FromPoints(Enumerable.Range(0, 8).Select(i => Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), entity.Transform)));
            if (!frustum.Intersects(bounds))
                continue;
            if (!depth) program.SetUniform("uDeferredReceiver", deferredDhdReceivers.GetValueOrDefault(entity.Id, 1));
            using var model = pass.PushModel(entity.Transform);
            foreach (var part in models[entity.Model])
            {
                bool matte = entity.Model == "dhd" && !part.DhdOrb;
                program.SetUniform("uRoughness", matte ? .98f : .84f);
                if (!depth)
                    program.SetUniform("uDhdMatte", matte ? 1 : 0);
                Vector3 emission = Vector3.Zero;
                if (!depth && part.Emission is not null)
                {
                    if (entity.Model == "dhd")
                    {
                        bool accepted = part.DhdOrb ? DhdGlyphs.IsOrbLit(entity.Id) : DhdGlyphs.IsLit(entity.Id, part.DhdKeyId);
                        float hint = part.DhdOrb || !DhdHintActive ? 0 : DhdHint.ForKey(part.DhdKeyId);
                        emission = accepted ? part.EmissionColor * part.EmissionIntensity * (part.DhdOrb ? OrbStrength : 1) : new Vector3(.045f) * hint;
                    }
                    else if (entity.Model == "stargate" && HorizonSettings.Enabled && ChevronOn(part))
                        emission = part.EmissionColor * part.EmissionIntensity * GateEmissionScale;
                }
                program.SetUniform("uModelEmissionGain", emission);
                using var emissive = !depth && part.Emission is not null ? part.Emission.Bind(7) : null;
                program.SetUniform("uHasModelNormal", !depth && part.normal is not null ? 1 : 0);
                program.SetUniform("uHasMetallicRoughness", !depth && part.MetallicRoughness is not null ? 1 : 0);
                program.SetUniform("uMetallicRoughness", 6);
                program.SetUniform("uModelHeightScale", depth ? 0 : part.HeightScale);
                using var normal = depth ? null : part.normal?.Bind(5);
                using var mr = depth ? null : part.MetallicRoughness?.Bind(6);
                using var press = pass.PushModel(PressTransform(part) * entity.Transform);
                Mesh(pass, part.mesh, depth ? white : part.texture, program);
            }
        }
        if (!depth)
        {
            program.SetUniform("uDhdLightCount", 0);
            program.SetUniform("uDhdMatte", 0);
            program.SetUniform("uModelEmissionGain", Vector3.Zero);
        }
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
        program.SetUniform("uModelHeightScale", 0f);
        program.SetUniform("uRoughness", .86f);
        program.SetUniform("uRockSnow", World.Definition.Preset == "icy" ? 1 : 0);
        using var rockSnow = depth ? null : snowTexture.Bind(4);
        using var rockSnowNormal = depth ? null : normals[3].Bind(8);
        foreach (var rock in Candidates(frustum).Rocks)
        {
            using var model = pass.PushModel(rock.Transform);
            foreach (var part in models.GetValueOrDefault(rock.Model, []))
            {
                program.SetUniform("uHasModelNormal", !depth && part.normal is not null ? 1 : 0);
                program.SetUniform("uHasMetallicRoughness", !depth && part.MetallicRoughness is not null ? 1 : 0);
                program.SetUniform("uMetallicRoughness", 6);
                program.SetUniform("uModelHeightScale", depth ? 0 : part.HeightScale);
                using var normal = depth ? null : part.normal?.Bind(5);
                using var mr = depth ? null : part.MetallicRoughness?.Bind(6);
                Mesh(pass, part.mesh, depth ? white : part.texture, program);
            }
        }
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uRockSnow", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
        program.SetUniform("uModelHeightScale", 0f);
        if (!InspectTree || !TreeForest)
            DrawTrees(pass, program, frustum, depth);
        DrawBuildings(pass, program, frustum, depth);
        DrawPrototypeMeshes(pass, program, frustum, depth);
    }
    readonly Dictionary<string, List<Matrix4x4>> visibleTrees = [];
    readonly Matrix4x4[] treeTransforms = new Matrix4x4[TreeBatchSize];
    void DrawActors(RenderPass pass, ShaderProgram program, ViewFrustum frustum, SceneFrame snapshot, bool depth, bool excludeLocal = false)
    {
        program.SetUniform("uFoliageCutoff", 0f);
        program.SetUniform("uShadowSurfaceBias", .015f);
        program.SetUniform("uTerrain", 0);
        program.SetUniform("uRoughness", .85f);
        program.SetUniform("uDeferredReceiver", 1);
        program.SetUniform("uDhdMatte", 0);
        foreach (var prop in snapshot.Bodies)
        {
            var definition = World.Definition.Props.FirstOrDefault(p => p.Id == prop.Id);
            if (definition is null || depth && !definition.CastsShadows)
                continue;
            var clip = snapshot.Clips?.FirstOrDefault(c => c.Body == prop.Id);
            program.SetUniform("uTravelPlane", clip is null ? Vector4.Zero : new Vector4(clip.Gate.Normal, -Vector3.Dot(clip.Gate.Normal, clip.Gate.Centre)));
            var transform = Matrix4x4.CreateFromQuaternion(prop.Rotation) * Matrix4x4.CreateTranslation(prop.Position);
            if (!models.ContainsKey(definition.Model))
            {
                RequestModel(definition.Model, 1);
                DrawModelProxy(pass, program, definition.Model, transform);
                continue;
            }
            var bounds = modelBounds[definition.Model];
            var corners = Enumerable.Range(0, 8).Select(i => Vector3.Transform(new((i & 1) == 0 ? bounds.Min.X : bounds.Max.X, (i & 2) == 0 ? bounds.Min.Y : bounds.Max.Y, (i & 4) == 0 ? bounds.Min.Z : bounds.Max.Z), transform));
            if (!frustum.Intersects(AxisAlignedBoundingBox.FromPoints(corners)))
                continue;
            program.SetUniform("uTint", Vector3.One);
            using var model = pass.PushModel(transform);
            foreach (var part in models[definition.Model])
            {
                if (part.Transparent) continue;
                program.SetUniform("uModelEmissionGain", depth ? Vector3.Zero : part.EmissionColor * part.EmissionIntensity);
                using var emission = depth ? null : part.Emission?.Bind(7);
                program.SetUniform("uHasModelNormal", !depth && part.normal is not null ? 1 : 0);
                program.SetUniform("uHasMetallicRoughness", !depth && part.MetallicRoughness is not null ? 1 : 0);
                program.SetUniform("uMetallicRoughness", 6);
                program.SetUniform("uModelHeightScale", depth ? 0 : part.HeightScale);
                using var normal = depth ? null : part.normal?.Bind(5);
                using var mr = depth ? null : part.MetallicRoughness?.Bind(6);
                Mesh(pass, part.mesh, part.texture, program);
            }
        }
        program.SetUniform("uHasModelNormal", 0);
        program.SetUniform("uHasMetallicRoughness", 0);
        program.SetUniform("uModelHeightScale", 0f);
        program.SetUniform("uTravelPlane", Vector4.Zero);
        program.SetUniform("uModelEmissionGain", Vector3.Zero);
        DrawCharacters(pass, program, frustum, snapshot, depth, excludeLocal);
    }
}

// Non-blocking GPU queries, grouped by complete frame so cached shadows contribute zero when not drawn.
sealed class GpuFrameTimer : IDisposable
{
    sealed class Sample
    {
        public long Frame;
        public int Remaining;
        public bool Complete, Valid = true;
        public double PointLight, PointShadow, Gbuffer, Deferred, Weapon, Composition, Ao, Total, Character, Wave, Spray, Grass, Weather, Atmosphere, Cloud, Fog, Water, MistMap, Terrain, Canopy, Exposure, ToneMap;
    }
    readonly GraphicsQuery[] queries;
    readonly Queue<int> free = [];
    readonly Queue<(int Index, Sample Sample, string Group)> pending = [];
    readonly Queue<Sample> frames = [];
    Sample current = new();
    public double Milliseconds
    {
        get;
        private set;
    }
    public double CanopyMilliseconds { get; private set; }
    public double ExposureMilliseconds { get; private set; }
    public double ToneMapMilliseconds { get; private set; }
    public double AoMilliseconds { get; private set; }
    public double GbufferMilliseconds { get; private set; }
    public double PointLightMilliseconds { get; private set; }
    public double PointShadowMilliseconds { get; private set; }
    public double DeferredMilliseconds { get; private set; }
    public double WeaponMilliseconds { get; private set; }
    public double CompositionMilliseconds { get; private set; }
    public double CharacterMilliseconds
    {
        get;
        private set;
    }
    public double WaveMilliseconds
    {
        get;
        private set;
    }
    public double SprayMilliseconds
    {
        get;
        private set;
    }
    public double GrassMilliseconds
    {
        get;
        private set;
    }
    public double TerrainMilliseconds
    {
        get;
        private set;
    }
    public double WeatherMilliseconds
    {
        get;
        private set;
    }
    public double AtmosphereMilliseconds
    {
        get;
        private set;
    }
    public double CloudMilliseconds
    {
        get;
        private set;
    }
    public double FogMilliseconds
    {
        get;
        private set;
    }
    public double MistMapMilliseconds
    {
        get;
        private set;
    }
    public double WaterMilliseconds
    {
        get;
        private set;
    }
    public GpuFrameTimer(GraphicsContext graphics)
    {
        queries = Enumerable.Range(0, 512).Select(_ => graphics.CreateQuery(GraphicsQueryType.TimeElapsed)).ToArray();
        for (int i = 0; i < queries.Length; i++)
            free.Enqueue(i);
    }
    public void StartFrame(long frame)
    {
        while (pending.TryPeek(out var p) && queries[p.Index].IsResultAvailable)
        {
            pending.Dequeue();
            double ms = queries[p.Index].GetResult() / 1_000_000.0;
            p.Sample.Total += ms;
            if (p.Group is "gbuffer" or "terrain" or "character" or "grass" or "canopy") p.Sample.Gbuffer += ms;
            if (p.Group == "deferred") p.Sample.Deferred += ms;
            if (p.Group is "weapon" or "weapon-ao") p.Sample.Weapon += ms;
            if (p.Group is "fog" or "water" or "tonemap" or "weather" or "gate-spray" or "composition") p.Sample.Composition += ms;
            if (p.Group is "ao" or "weapon-ao") p.Sample.Ao += ms;
            if (p.Group == "gate-spray")
                p.Sample.Spray += ms;
            if (p.Group == "horizon-wave")
                p.Sample.Wave += ms;
            if (p.Group == "character")
                p.Sample.Character += ms;
            if (p.Group == "grass")
                p.Sample.Grass += ms;
            if (p.Group == "weather")
                p.Sample.Weather += ms;
            if (p.Group == "atmosphere")
                p.Sample.Atmosphere += ms;
            if (p.Group == "cloud")
                p.Sample.Cloud += ms;
            if (p.Group == "fog")
                p.Sample.Fog += ms;
            if (p.Group == "water")
                p.Sample.Water += ms;
            if (p.Group == "mistmap")
                p.Sample.MistMap += ms;
            if (p.Group == "terrain")
                p.Sample.Terrain += ms;
            if (p.Group == "point-shadow") p.Sample.PointShadow += ms;
            if (p.Group == "point-light") p.Sample.PointLight += ms;
            if (p.Group == "canopy") p.Sample.Canopy += ms;
            if (p.Group == "exposure") p.Sample.Exposure += ms;
            if (p.Group == "tonemap") p.Sample.ToneMap += ms;
            p.Sample.Remaining--;
            free.Enqueue(p.Index);
        }
        while (frames.TryPeek(out var f) && f.Complete && f.Remaining == 0)
        {
            frames.Dequeue();
            if (f.Valid)
            {
                Milliseconds = f.Total;
                AoMilliseconds = f.Ao;
                PointLightMilliseconds = f.PointLight; PointShadowMilliseconds = f.PointShadow;
                GbufferMilliseconds = f.Gbuffer; DeferredMilliseconds = f.Deferred;
                WeaponMilliseconds = f.Weapon; CompositionMilliseconds = f.Composition;
                CharacterMilliseconds = f.Character;
                WaveMilliseconds = f.Wave;
                SprayMilliseconds = f.Spray;
                GrassMilliseconds = f.Grass;
                WeatherMilliseconds = f.Weather;
                AtmosphereMilliseconds = f.Atmosphere;
                CloudMilliseconds = f.Cloud;
                FogMilliseconds = f.Fog;
                WaterMilliseconds = f.Water;
                MistMapMilliseconds = f.MistMap;
                TerrainMilliseconds = f.Terrain;
                CanopyMilliseconds = f.Canopy;
                ExposureMilliseconds = f.Exposure;
                ToneMapMilliseconds = f.ToneMap;
            }
        }
        current = new()
        {
            Frame = frame
        };
        frames.Enqueue(current);
    }
    public IDisposable? Measure(RenderPass pass, string group = "")
    {
        if (!free.TryDequeue(out int index))
        {
            current.Valid = false;
            return null;
        }
        current.Remaining++;
        var sample = current;
        var scope = pass.BeginQuery(queries[index]);
        return new EndScope(() =>
        {
            scope.Dispose();
            pending.Enqueue((index, sample, group));
        });
    }
    public void EndFrame() => current.Complete = true;
    public void Dispose()
    {
        foreach (var query in queries)
            query.Dispose();
    }
    sealed class EndScope(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
