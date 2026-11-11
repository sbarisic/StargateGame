using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using FishGfx.Graphics.Drawables;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public EventHorizonSettings HorizonSettings
    {
        get;
        set;
    } = new();
    readonly Dictionary<string, EventHorizonState> horizonStates = [];
    ShaderProgram? horizonShader, horizonBlur;
    Mesh3D? horizonMesh;
    RenderTarget? horizonMask, horizonScratch;
    StaticEntity[] visibleHorizons = [];
    public int HorizonDrawCalls
    {
        get;
        private set;
    }
    public int HorizonRipples => horizonStates.Values.Sum(s => s.Impulses.Count);
    public long HorizonTargetBytes => HorizonWaveBytes + (long)(horizonMask?.Width ?? 0) * (horizonMask?.Height ?? 0) * 16;
    // Deterministic visual stress fixture; never influences simulation or network state.
    public bool HorizonStress
    {
        get;
        set;
    }
    double nextWalkTrace;
    public void TraceHorizonWalk(RenderFrame frame, Camera camera, SceneFrame snapshot, float time)
    {
        if (time < nextWalkTrace)
            return;
        nextWalkTrace = time + .1;
        walkProbe ??= LoadProgram("fullscreen.vert", "horizon_probe.frag");
        var gate = World.Definition.Entities.First(e => e.Model == "stargate");
        horizonWaves.TryGetValue(gate.Id, out var wave);
        double peak = 0;
        if (wave is not null)
        {
            walkProbe.SetUniform("uState", 0);
            walkProbe.SetUniform("uHeightScale", 10f);
            using var result = graphics.CreateRenderTarget(new(128, 128, [TextureFormat.RGBA8Unorm], null));
            using var pass = frame.BeginPass(result, new RenderPassDescriptor { View = new RenderView(camera), State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
            Mesh(pass, screen, wave.State.ColorAttachments[0], walkProbe);
            byte[] bytes = new byte[128 * 128 * 4];
            pass.ReadColorRgba32(bytes);
            for (int i = 0; i < bytes.Length; i += 4)
                peak = Math.Max(peak, Math.Abs(bytes[i] - bytes[i + 1]) / 255.0 / 10);
        }
        Console.WriteLine($"WALK CONTACT time={time:F3} relative={snapshot.Local.Position - gate.Position} epoch={snapshot.PhysicsEpoch} impulses={HorizonRipples} steps={HorizonSubsteps} waveTime={wave?.Time:F3} consumed={wave?.Consumed:F3} peak_mm={peak * 1000:F3}");
    }
    ShaderProgram? walkProbe;
    void ResetHorizons()
    {
        ClearGateSpray();
        DisposeGateLights();
        DisposeHorizonWaves();
        horizonStates.Clear();
        visibleHorizons = [];
        DisposeHorizonTargets();
    }
    void DisposeHorizonTargets()
    {
        horizonMask?.Dispose();
        horizonScratch?.Dispose();
        horizonMask = null;
        horizonScratch = null;
    }
    Guid waveConnection;
    void PrepareHorizons(SceneFrame snapshot, float time, ViewFrustum frustum)
    {
        HorizonDrawCalls = 0;
        var connection = AuthoritativeGate?.ConnectionId ?? Guid.Empty;
        if (connection != waveConnection)
        {
            DisposeHorizonWaves();
            horizonStates.Clear();
            waveConnection = connection;
        }
        if (!HorizonSettings.Enabled || !AnyGateSurface)
        {
            DisposeHorizonWaves();
            horizonStates.Clear();
            visibleHorizons = [];
            return;
        }
        var gates = World.Definition.Entities.Where(e => e.Model == "stargate" && (InspectActiveGate || StateFor(e)?.Surface == true)).ToArray();
        foreach (var gate in gates)
        {
            using var endpoint = SelectGate(gate);
            if (!horizonStates.TryGetValue(gate.Id, out var state))
                horizonStates[gate.Id] = state = new();
            if (GateSettled)
                state.Update(gate, snapshot.Remotes.Append(snapshot.Local), time, snapshot.LocationId, snapshot.PhysicsEpoch, snapshot.Bodies, World.Definition.Props);
            else
                state.Clear();
        }
        visibleHorizons = gates.Where(g =>
        {
            var centre = EventHorizonState.Centre(g);
            float radius = EventHorizonState.ModelRadius * g.Scale;
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (float x in new[] { -radius * 1.4f, radius * 1.4f })
                foreach (float y in new[] { -radius * 1.4f, radius * 1.4f })
                    foreach (float z in new[] { -.5f * radius, 3.1f * radius })
                    {
                        var p = centre + Vector3.Transform(new Vector3(x, y, z), g.Rotation);
                        lo = Vector3.Min(lo, p);
                        hi = Vector3.Max(hi, p);
                    }
            return frustum.Intersects(new AxisAlignedBoundingBox(lo, hi));
        }).ToArray();
        if (visibleHorizons.Length == 0)
            return;
        PrepareGateVisualResources();
    }
    public bool PrepareGateVisualResources()
    {
        if (horizonShader is null)
        {
            // Compile before any player touches the surface; first-contact shader
            // compilation must not consume the short first-person feedback window.
            if (HorizonSettings.CollisionWaves)
                PrepareWavePrograms();
            horizonShader = LoadProgram("event_horizon.vert", "event_horizon.frag");
            horizonShader.SetUniform("uSceneDepth", 1);
            horizonShader.SetUniform("uWaveState", 2);
            horizonShader.SetUniform("uWaveObstacles", 3);
            horizonBlur = LoadProgram("fullscreen.vert", "horizon_blur.frag");
            var points = new List<Vector3>();
            var centre = new Vector3(0, EventHorizonState.ModelCentreY, 0);
            Vector3 Point(int ring, int sector)
            {
                float a = sector * MathF.Tau / 128;
                return centre + new Vector3(MathF.Cos(a), MathF.Sin(a), 0) * (EventHorizonState.ModelRadius * ring / 48);
            }
            for (int r = 0; r < 48; r++)
                for (int i = 0; i < 128; i++)
                {
                    points.Add(Point(r, i));
                    points.Add(Point(r + 1, i));
                    points.Add(Point(r + 1, i + 1));
                    if (r > 0)
                    {
                        points.Add(Point(r, i));
                        points.Add(Point(r + 1, i + 1));
                        points.Add(Point(r, i + 1));
                    }
                }
            horizonMesh = Own(graphics.CreateMesh3D());
            horizonMesh.SetVertices(points.ToArray());
        }
        gateEmissionShader ??= LoadProgram("world.vert", "gate_emission.frag");
        gateEmissionShader.SetUniform("uSceneDepth", 1);
        PrepareGateSpray();
        return true;
    }
    void GateTimelineUniforms(ShaderProgram program)
    {
        var f = GateEffect;
        program.SetUniform("uOpeningAge", f.OpeningAge);
        program.SetUniform("uClosingAge", f.ClosingAge);
        program.SetUniform("uGateShape", new Vector4(f.Extension, f.Retraction, f.Activity, f.Stability));
        program.SetUniform("uGateSurface", new Vector4(f.Formation, f.Flash, f.Breakup, f.SurfaceFade));
        program.SetUniform("uPlumeLength", KawooshShape.Length);
    }
    void DrawHorizons(RenderPass pass, bool emissive, float time, bool showDisturbance = true)
    {
        if (visibleHorizons.Length == 0 || horizonShader is null)
            return;
        var settings = HorizonSettings;
        Lighting(horizonShader);
        horizonShader.SetUniform("uKawooshBloom", KawooshBloomEnabled ? 1f : 0f);
        horizonShader.SetUniform("uWaveScale", settings.WaveScale);
        horizonShader.SetUniform("uDistortion", settings.Distortion);
        horizonShader.SetUniform("uSpeed", settings.Speed);
        horizonShader.SetUniform("uNormalStrength", settings.NormalStrength);
        horizonShader.SetUniform("uHighlightWidth", settings.HighlightWidth);
        horizonShader.SetUniform("uBlue", settings.Blue);
        horizonShader.SetUniform("uCentreEmphasis", settings.CentreEmphasis);
        horizonShader.SetUniform("uEmissiveOnly", emissive ? 1 : 0);
        horizonShader.SetUniform("uEdgeFade", settings.EdgeFade);
        horizonShader.SetUniform("uRimBrightness", settings.RimBrightness);
        horizonShader.SetUniform("uDisturbanceSmoothing", settings.DisturbanceSmoothing);
        horizonShader.SetUniform("uGrazingCompensation", settings.GrazingCompensation);
        horizonShader.SetUniform("uEmissionIntensity", settings.EmissionIntensity);
        horizonShader.SetUniform("uMaskSize", new Vector2(horizonMask?.Width ?? 1, horizonMask?.Height ?? 1));
        foreach (var gate in visibleHorizons)
        {
            using var endpoint = SelectGate(gate);
            GateTimelineUniforms(horizonShader);
            horizonShader.SetUniform("uGateScale", gate.Scale);
            horizonShader.SetUniform("uGateNormal", Vector3.Transform(Vector3.UnitZ, gate.Rotation));
            horizonShader.SetUniform("uGateRight", Vector3.Transform(Vector3.UnitX, gate.Rotation));
            horizonShader.SetUniform("uGateUp", Vector3.Transform(Vector3.UnitY, gate.Rotation));
            horizonWaves.TryGetValue(gate.Id, out var wave);
            using var waveBinding = wave?.State.ColorAttachments[0].Bind(2);
            using var obstacleBinding = wave?.PreviousMask.ColorAttachments[0].Bind(3);
            horizonShader.SetUniform("uHasWaveState", wave is null || !wave.WasActive || !showDisturbance ? 0 : 1);
            horizonShader.SetUniform("uWakeFade", wave?.Fade ?? 0);
            horizonShader.SetUniform("uWaveCell", wave?.Cell ?? 1);
            horizonShader.SetUniform("uContactRamp", wave?.Ramp ?? 0);
            horizonShader.SetUniform("uRadius", EventHorizonState.ModelRadius * gate.Scale);
            horizonShader.SetUniform("uGateCentre", EventHorizonState.Centre(gate));
            using var transform = pass.PushModel(gate.Transform);
            // The TV gate has a flat rear surface, not the movie's rear whirlpool.
            // A depth-writing back sheet hides the inside of the forward plume.
            if (GateEffect.Extension > 0)
            {
                horizonShader.SetUniform("uBackSheet", 1);
                Mesh(pass, horizonMesh!, white, horizonShader);
                HorizonDrawCalls++;
            }
            horizonShader.SetUniform("uBackSheet", 0);
            Mesh(pass, horizonMesh!, white, horizonShader);
            HorizonDrawCalls++;
        }
    }
    void RenderHorizonBloom(RenderFrame frame, RenderView view, float time)
    {
        if ((visibleHorizons.Length == 0 && !gateEmissionVisible && visibleLitDhds.Length == 0) || HorizonSettings.BloomStrength <= 0)
        {
            DisposeHorizonTargets();
            return;
        }
        horizonBlur ??= LoadProgram("fullscreen.vert", "horizon_blur.frag");
        float spread = 1 + (KawooshBloomEnabled ? 2.4f * GateEffect.Activity : 0);
        int w = (opaqueTarget!.Width + 1) / 2, h = (opaqueTarget.Height + 1) / 2;
        if (horizonMask?.Width != w || horizonMask.Height != h)
        {
            DisposeHorizonTargets();
            try
            {
                horizonMask = graphics.CreateRenderTarget(new(w, h, [TextureFormat.RGBA16Float], null));
                horizonScratch = graphics.CreateRenderTarget(new(w, h, [TextureFormat.RGBA16Float], null));
                var sampling = new TextureSamplingState(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge);
                horizonMask.ColorAttachments[0].SetSampling(sampling);
                horizonScratch.ColorAttachments[0].SetSampling(sampling);
            }
            catch
            {
                DisposeHorizonTargets();
                throw;
            }
        }
        using (var pass = frame.BeginPass(horizonMask, new RenderPassDescriptor { View = view, State = ScreenState, Time = time, ClearColor = new Color(0, 0, 0), ColorLoadAction = RenderLoadAction.Clear }))
        using (opaqueTarget.DepthStencilAttachment.Bind(1))
        {
            using (gpuTimer.Measure(pass))
            {
                DrawHorizons(pass, true, time);
                DrawGateEmission(pass, true);
                DrawDhdEmission(pass, true);
            }
            using (gpuTimer.Measure(pass, "gate-spray"))
                DrawGateSpray(pass, view, true);
        }
        using var coverage = (viewmodelDrawn ? viewmodelTarget!.ColorAttachments[0] : white).Bind(1);
        horizonBlur!.SetUniform("uViewmodelCoverage", 1);
        horizonBlur.SetUniform("uMaskViewmodel", viewmodelDrawn ? 1 : 0);
        using (var pass = frame.BeginPass(horizonScratch!, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass))
        {
            horizonBlur!.SetUniform("uBlurStep", new Vector2(spread / w, 0));
            Mesh(pass, screen, horizonMask.ColorAttachments[0], horizonBlur);
            HorizonDrawCalls++;
        }
        horizonBlur.SetUniform("uMaskViewmodel", 0);
        using (var pass = frame.BeginPass(horizonMask, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass))
        {
            horizonBlur!.SetUniform("uBlurStep", new Vector2(0, spread / h));
            Mesh(pass, screen, horizonScratch!.ColorAttachments[0], horizonBlur);
            HorizonDrawCalls++;
        }
    }
}
