using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    // Simulation targets never share attachments with the colour or bloom passes.
    sealed class HorizonWave : IDisposable
    {
        public required RenderTarget State, Other, Mask, PreviousMask, Start, End;
        public required int Resolution, Generation;
        public required float Cell;
        public double LastTime, Time, Accumulator, ActiveUntil, Consumed = double.NegativeInfinity;
        public float Ramp, Fade = 1;
        public bool Initialized, WasActive;
        public void Dispose()
        {
            State.Dispose();
            Other.Dispose();
            Mask.Dispose();
            PreviousMask.Dispose();
            Start.Dispose();
            End.Dispose();
        }
    }
    readonly Dictionary<string, HorizonWave> horizonWaves = [];
    ShaderProgram? waveBaseline, waveObstacle, waveStep;
    void PrepareWavePrograms()
    {
        waveBaseline ??= LoadProgram("fullscreen.vert", "horizon_baseline.frag");
        waveObstacle ??= LoadProgram("fullscreen.vert", "horizon_obstacle.frag");
        waveStep ??= LoadProgram("fullscreen.vert", "horizon_step.frag");
    }
    public int HorizonSubsteps
    {
        get;
        private set;
    }
    public int HorizonObstacles => horizonStates.Values.Sum(s => s.Footprints.Count + s.PlayerFootprints.Count);
    public int HorizonResolution => HorizonSettings.SimulationResolution > 0 ? Math.Clamp(HorizonSettings.SimulationResolution, 64, 1024) :
        Settings.Quality switch
        {
            GraphicsQuality.Low => 256,
            GraphicsQuality.High => 512,
            _ => 384
        };
    public long HorizonWaveBytes => horizonWaves.Values.Sum(w => (long)w.Resolution * w.Resolution * 40);
    void DisposeHorizonWaves()
    {
        foreach (var wave in horizonWaves.Values)
            wave.Dispose();
        horizonWaves.Clear();
    }
    HorizonWave CreateHorizonWave(int resolution, int generation, float radius, double time)
    {
        List<RenderTarget> created = [];
        RenderTarget Target(TextureFormat format)
        {
            var target = graphics.CreateRenderTarget(new(resolution, resolution, [format], null));
            created.Add(target);
            target.ColorAttachments[0].SetSampling(new(TextureFilter.Linear, TextureFilter.Linear, TextureWrap.ClampToEdge, TextureWrap.ClampToEdge));
            return target;
        }
        try
        {
            return new()
            {
                State = Target(TextureFormat.RG32Float),
                Other = Target(TextureFormat.RG32Float),
                Mask = Target(TextureFormat.RGBA16Float),
                PreviousMask = Target(TextureFormat.RGBA16Float),
                Start = Target(TextureFormat.R32Float),
                End = Target(TextureFormat.R32Float),
                Resolution = resolution,
                Generation = generation,
                Cell = 2 * radius / resolution,
                LastTime = time,
                Time = time
            };
        }
        catch
        {
            foreach (var target in created)
                target.Dispose();
            throw;
        }
    }
    void ClearWaveTarget(RenderFrame frame, RenderView view, RenderTarget target)
    {
        using var pass = frame.BeginPass(target, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear, ClearColor = new Color(0, 0, 0, 0) });
    }
    void AdvanceHorizonWaves(RenderFrame frame, RenderView view, float time)
    {
        HorizonSubsteps = 0;
        if (!HorizonSettings.Enabled || !AnyGateSettled || !HorizonSettings.CollisionWaves)
        {
            DisposeHorizonWaves();
            return;
        }
        foreach (var gate in World.Definition.Entities.Where(e => e.Model == "stargate"))
        {
            using var endpointScope = SelectGate(gate);
            if (!GateSettled)
                continue;
            if (!horizonStates.TryGetValue(gate.Id, out var contacts))
                continue;
            float radius = EventHorizonState.ModelRadius * gate.Scale;
            int resolution = HorizonResolution;
            bool hadWave = horizonWaves.TryGetValue(gate.Id, out var wave);
            if (hadWave && (wave!.Generation != contacts.Generation || wave.Resolution != resolution))
            {
                wave.Dispose();
                horizonWaves.Remove(gate.Id);
                wave = null;
            }
            bool forcing = contacts.Footprints.Count > 0 || contacts.PlayerFootprints.Count > 0 || contacts.Impulses.Any(i => time - i.Started < .25) || HorizonStress;
            if (wave is null)
            {
                if (!forcing)
                    continue;
                horizonWaves[gate.Id] = wave = CreateHorizonWave(resolution, contacts.Generation, radius, time);
                wave.Consumed = hadWave ? time : time - 1.0 / 60;
            }
            PrepareWavePrograms();
            if (!wave.Initialized)
            {
                foreach (var target in new[] { wave.State, wave.Other, wave.Mask, wave.PreviousMask })
                    ClearWaveTarget(frame, view, target);
                wave.Initialized = true;
            }
            double elapsed = time - wave.LastTime;
            wave.LastTime = time;
            if (forcing)
                wave.ActiveUntil = time + EventHorizonState.Lifetime;
            if (time >= wave.ActiveUntil)
            {
                if (wave.WasActive)
                {
                    ClearWaveTarget(frame, view, wave.State);
                    ClearWaveTarget(frame, view, wave.Other);
                }
                wave.WasActive = false;
                wave.Time = time;
                wave.Accumulator = 0;
                continue;
            }
            wave.WasActive = true;
            wave.Fade = forcing ? 1 : 1 - GateEffectTimeline.Smooth(0, 1, (float)(time - (wave.ActiveUntil - 1)));
            if (elapsed < 0 || elapsed > .25)
            {
                wave.Accumulator = 0;
                wave.Time = time;
                wave.Ramp = 0;
                wave.Consumed = time;
                // Sync masks on resume; a stale silhouette must not create an impact.
                DrawObstacleMask(frame, view, wave.Mask, contacts, gate, time, wave.Consumed, true);
                wave.Mask.ColorAttachments[0].CopyTo(wave.PreviousMask.ColorAttachments[0]);
                continue;
            }
            wave.Accumulator += elapsed;
            const double tick = 1.0 / 60;
            int steps = EventHorizonSettings.Substeps(resolution, radius, HorizonSettings.WaveSpeed);
            while (wave.Accumulator + 1e-9 >= tick)
            {
                double end = wave.Time + tick;
                DrawObstacleMask(frame, view, wave.Mask, contacts, gate, end, wave.Consumed, false);
                foreach (var endpoint in new[] { (wave.Start, wave.Time), (wave.End, end) })
                {
                    using var pass = frame.BeginPass(endpoint.Item1, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
                    using var timing = gpuTimer.Measure(pass, "horizon-wave");
                    waveBaseline!.SetUniform("uRadius", radius);
                    waveBaseline.SetUniform("uSampleTime", (float)endpoint.Item2);
                    waveBaseline.SetUniform("uWaveScale", HorizonSettings.WaveScale);
                    waveBaseline.SetUniform("uDistortion", HorizonSettings.Distortion);
                    waveBaseline.SetUniform("uSpeed", HorizonSettings.Speed);
                    Mesh(pass, screen, white, waveBaseline);
                }
                waveStep!.SetUniform("uState", 0);
                waveStep.SetUniform("uObstacles", 1);
                waveStep.SetUniform("uPreviousObstacles", 2);
                waveStep.SetUniform("uBaselineStart", 3);
                waveStep.SetUniform("uBaselineEnd", 4);
                waveStep.SetUniform("uCell", wave.Cell);
                waveStep.SetUniform("uDt", (float)tick / steps);
                waveStep.SetUniform("uRadius", radius);
                waveStep.SetUniform("uDamping", forcing ? HorizonSettings.Damping : 1.2f);
                waveStep.SetUniform("uSpeedSquared", HorizonSettings.WaveSpeed * HorizonSettings.WaveSpeed);
                wave.Ramp = Math.Min(1, wave.Ramp + (float)tick * 20);
                waveStep.SetUniform("uResponse", HorizonSettings.BoundaryResponse * wave.Ramp);
                waveStep.SetUniform("uMotionStep", wave.Ramp);
                waveStep.SetUniform("uContactTime", (float)end);
                waveStep.SetUniform("uImpulseAmplitude", contacts.Impulses.Where(i => i.Started > wave.Consumed && i.Started <= end).Select(i => i.Strength).DefaultIfEmpty(0).Max());
                waveStep.SetUniform("uImpulseHeight", contacts.Impulses.Where(i => i.Started > wave.Consumed && i.Started <= end).Select(i => i.Displacement).DefaultIfEmpty(0).Max());
                waveStep.SetUniform("uMotionAmplitude", Math.Clamp(contacts.Footprints.Select(c => c.Motion * .008f).Concat(contacts.PlayerFootprints.Select(c => c.Motion * .008f)).DefaultIfEmpty(0).Max(), 0, .08f));
                using (wave.Mask.ColorAttachments[0].Bind(1))
                using (wave.PreviousMask.ColorAttachments[0].Bind(2))
                using (wave.Start.ColorAttachments[0].Bind(3))
                using (wave.End.ColorAttachments[0].Bind(4))
                    for (int sub = 0; sub < steps; sub++)
                    {
                        using (var pass = frame.BeginPass(wave.Other, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
                        using (gpuTimer.Measure(pass, "horizon-wave"))
                        {
                            waveStep.SetUniform("uPhase", (sub + .5f) / steps);
                            waveStep.SetUniform("uFirstSubstep", sub == 0 ? 1 : 0);
                            waveStep.SetUniform("uPacket", HorizonStress && sub == 0 && wave.Time % 2 < tick ? 1 : 0);
                            Mesh(pass, screen, wave.State.ColorAttachments[0], waveStep);
                            HorizonSubsteps++;
                        }
                        (wave.State, wave.Other) = (wave.Other, wave.State);
                    }
                (wave.Mask, wave.PreviousMask) = (wave.PreviousMask, wave.Mask);
                wave.Consumed = end;
                wave.Time = end;
                wave.Accumulator -= tick;
            }
        }
    }
    void DrawObstacleMask(RenderFrame frame, RenderView view, RenderTarget target, EventHorizonState contacts, StaticEntity gate, double time, double consumed, bool suppress)
    {
        float radius = EventHorizonState.ModelRadius * gate.Scale;
        using var pass = frame.BeginPass(target, new RenderPassDescriptor
        {
            View = view,
            State = ScreenState with
            {
                BlendEnabled = true,
                SourceBlend = BlendFactor.One,
                DestinationBlend = BlendFactor.OneMinusSourceColor
            },
            ColorLoadAction = RenderLoadAction.Clear,
            ClearColor = new Color(0, 0, 0, 0)
        });
        using var timing = gpuTimer.Measure(pass, "horizon-wave");
        waveObstacle!.SetUniform("uRadius", radius);
        waveObstacle.SetUniform("uCell", 2 * radius / target.Width);
        void Polygon(Vector2[] vertices, Vector4 channels)
        {
            if (vertices.Length < 3)
                return;
            if (vertices.Length > 80)
                throw new InvalidOperationException("Convex gate footprint exceeds the mask vertex budget.");
            waveObstacle.SetUniform("uVertexCount", vertices.Length);
            waveObstacle.SetUniform("uChannels", channels);
            for (int i = 0; i < vertices.Length; i++)
                waveObstacle.SetUniform($"uVertices[{i}]", vertices[i]);
            Mesh(pass, screen, white, waveObstacle);
        }
        foreach (var contact in contacts.Footprints)
        {
            var polygon = EventHorizonState.SampleFootprint(contact, time, gate);
            Polygon(polygon, new(1, suppress || contact.SuppressImpact ? 1 : 0, 0, 1));
        }
        foreach (var contact in contacts.PlayerFootprints)
            Polygon(contact.Vertices, new(1, suppress || contact.SuppressImpact ? 1 : 0, 0, 1));
        foreach (var impulse in contacts.Impulses.Where(i => i.Started > consumed && i.Started <= time))
            foreach (var polygon in impulse.Polygons)
                Polygon(polygon, new(0, 0, suppress ? 0 : 1, 0));
        if (HorizonStress)
            Polygon([new(0, -.6f), new(.65f, -.6f), new(.65f, .6f), new(0, .6f)], new(1, 0, 0, 0));
    }
}
