using System.Diagnostics;
using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public ToneMappingSettings ToneMapping { get; set; } = new();
    public bool ExposureTransitHold { get; set; }
    public bool ExposureDiagnosticsEnabled { get; set; }
    public float? ExposureEv { get; private set; }
    public float? ExposureTargetEv { get; private set; }
    public bool ExposureHeld { get; private set; }
    public double ExposureGpuMilliseconds => gpuTimer.ExposureMilliseconds;
    public double ToneMapGpuMilliseconds => gpuTimer.ToneMapMilliseconds;
    public long ExposureTargetBytes => exposureLevels.Sum(t => (long)t.Width * t.Height * 8) + (exposureHistory is null ? 0 : 32);
    readonly List<RenderTarget> exposureLevels = [];
    RenderTarget[]? exposureHistory;
    ShaderProgram exposureMeter = null!, exposureReduce = null!, exposureAdapt = null!;
    int exposureIndex;
    bool exposureInitialized;
    long exposureTimestamp;
    Guid exposureLocation;
    AsyncPixelReadback? exposureReadback;
    double exposureReadbackTime;
    Texture ExposureTexture => exposureHistory![exposureIndex].ColorAttachments[0];

    IEnumerable<bool> InitializeExposure()
    {
        exposureMeter = LoadProgram("fullscreen.vert", "exposure_meter.frag");
        exposureMeter.SetUniform("uDepth", 1);
        yield return false;
        exposureReduce = LoadProgram("fullscreen.vert", "exposure_reduce.frag");
        yield return false;
        exposureAdapt = LoadProgram("fullscreen.vert", "exposure_adapt.frag");
        exposureAdapt.SetUniform("uPrevious", 1);
        yield return false;
    }

    void EnsureExposureTargets()
    {
        if (exposureHistory is not null) return;
        try
        {
            int w = 160, h = 90;
            while (true)
            {
                exposureLevels.Add(graphics.CreateRenderTarget(new(w, h, [TextureFormat.RG32Float], null)));
                if (w == 1 && h == 1) break;
                w = (w + 3) / 4; h = (h + 3) / 4;
            }
            var first = graphics.CreateRenderTarget(new(1, 1, [TextureFormat.RGBA32Float], null));
            try { exposureHistory = [first, graphics.CreateRenderTarget(new(1, 1, [TextureFormat.RGBA32Float], null))]; }
            catch { first.Dispose(); throw; }
        }
        catch { DisposeExposure(); throw; }
    }

    void ResetExposure()
    {
        exposureInitialized = false;
        exposureTimestamp = 0;
        exposureReadback?.Dispose();
        exposureReadback = null;
        ExposureEv = ExposureTargetEv = null;
    }

    void DisposeExposure()
    {
        ResetExposure();
        foreach (var target in exposureLevels) target.Dispose();
        exposureLevels.Clear();
        if (exposureHistory is not null) foreach (var target in exposureHistory) target.Dispose();
        exposureHistory = null;
    }

    void RenderExposure(RenderFrame frame, RenderView view, Guid location)
    {
        EnsureExposureTargets();
        if (exposureLocation != location) { ResetExposure(); exposureLocation = location; }
        long now = Stopwatch.GetTimestamp();
        float delta = exposureTimestamp == 0 ? 0 : (float)Math.Clamp((now - exposureTimestamp) / (double)Stopwatch.Frequency, 0, .1);
        exposureTimestamp = now;
        bool flashing = !InspectActiveGate && visibleHorizons.Any(g => StateFor(g)?.Phase is GatePhase.Opening or GatePhase.Closing);
        ExposureHeld = flashing || ExposureTransitHold;
        using (waterTarget!.DepthStencilAttachment.Bind(1))
        using (var pass = frame.BeginPass(exposureLevels[0], new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, "exposure"))
            Mesh(pass, screen, waterTarget.ColorAttachments[0], exposureMeter);
        for (int i = 1; i < exposureLevels.Count; i++)
        {
            using var pass = frame.BeginPass(exposureLevels[i], new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
            using var timing = gpuTimer.Measure(pass, "exposure");
            Mesh(pass, screen, exposureLevels[i - 1].ColorAttachments[0], exposureReduce);
        }
        int next = 1 - exposureIndex;
        using (ExposureTexture.Bind(1))
        using (var pass = frame.BeginPass(exposureHistory![next], new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        using (gpuTimer.Measure(pass, "exposure"))
        {
            float day = Math.Clamp((environmentFrame.SunDirection.Y - MathF.Sin(-6 * MathF.PI / 180))
                / (MathF.Sin(15 * MathF.PI / 180) - MathF.Sin(-6 * MathF.PI / 180)), 0, 1);
            exposureAdapt.SetUniform("uDay", day * day * (3 - 2 * day));
            exposureAdapt.SetUniform("uDelta", delta);
            exposureAdapt.SetUniform("uInitialize", exposureInitialized ? 0 : 1);
            // A new destination is metered under white; ordinary transit frames hold history.
            exposureAdapt.SetUniform("uHold", flashing ? 2 : ExposureTransitHold ? 1 : 0);
            exposureAdapt.SetUniform("uMeasurementReady", resident.Count > 0 && atmosphereLight is not null ? 1 : 0);
            exposureAdapt.SetUniform("uAutomatic", ToneMapping.Mode == ExposureMode.Auto ? 1 : 0);
            exposureAdapt.SetUniform("uCompensation", ToneMapping.Compensation);
            Mesh(pass, screen, exposureLevels[^1].ColorAttachments[0], exposureAdapt);
            PollExposureReadback(pass, now / (double)Stopwatch.Frequency);
        }
        exposureIndex = next;
        exposureInitialized = true;
    }

    void PollExposureReadback(RenderPass pass, double now)
    {
        if (exposureReadback is not null)
        {
            bool ready = exposureReadback.TryGetResult(out var data);
            if (ready || exposureReadback.Failed)
            {
                if (ready && float.IsFinite(data.X) && float.IsFinite(data.Y))
                {
                    ExposureEv = data.X;
                    ExposureTargetEv = ToneMapping.Mode == ExposureMode.Auto ? data.Y + ToneMapping.Compensation : ToneMapping.Compensation;
                }
                exposureReadback.Dispose(); exposureReadback = null;
            }
        }
        if (ExposureDiagnosticsEnabled && exposureReadback is null && now - exposureReadbackTime >= .5)
        {
            exposureReadback = pass.ReadPixelAsync();
            exposureReadbackTime = now;
        }
    }
}
