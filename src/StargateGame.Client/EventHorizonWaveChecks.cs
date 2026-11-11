using System.Numerics;
using FishGfx;
using FishGfx.Graphics;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool HorizonWaveDiagnostics
    {
        get;
        set;
    }
    bool waveDiagnosticsComplete;
    // Explicit diagnostic: exercise the actual fragment solver, then read an encoded
    // field back through FishGfx. It never runs during ordinary play or benchmarks.
    void CheckHorizonWaves(RenderFrame frame, RenderView view)
    {
        if (!HorizonWaveDiagnostics || waveDiagnosticsComplete)
            return;
        waveDiagnosticsComplete = true;
        CheckGateRendering(frame, view);
        waveBaseline ??= LoadProgram("fullscreen.vert", "horizon_baseline.frag");
        waveObstacle ??= LoadProgram("fullscreen.vert", "horizon_obstacle.frag");
        waveStep ??= LoadProgram("fullscreen.vert", "horizon_step.frag");
        var probe = LoadProgram("fullscreen.vert", "horizon_probe.frag");
        probe.SetUniform("uState", 0);
        CheckPlayerEntryWaves(frame, view, probe);
        probe.SetUniform("uHeightScale", 1000f);
        foreach (int resolution in new[] { 256, 384, 512 })
        {
            (double Left, double Right, double Total) Run(bool wall, bool packet, bool animated)
            {
                using var wave = CreateHorizonWave(resolution, 0, 2.3f, 0);
                using var result = graphics.CreateRenderTarget(new(resolution, resolution, [TextureFormat.RGBA8Unorm], null));
                foreach (var target in new[] { wave.State, wave.Other, wave.Mask, wave.PreviousMask, wave.Start, wave.End })
                    ClearWaveTarget(frame, view, target);
                if (wall)
                {
                    using var pass = frame.BeginPass(wave.Mask, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.Clear, ClearColor = new Color(0, 0, 0, 0) });
                    waveObstacle.SetUniform("uRadius", 2.3f);
                    waveObstacle.SetUniform("uCell", wave.Cell);
                    waveObstacle.SetUniform("uVertexCount", 4);
                    waveObstacle.SetUniform("uChannels", new Vector4(1, 0, 0, animated ? 1 : 0));
                    Vector2[] polygon = [new(0, -3), new(.3f, -3), new(.3f, 3), new(0, 3)];
                    for (int i = 0; i < 4; i++)
                        waveObstacle.SetUniform($"uVertices[{i}]", polygon[i]);
                    Mesh(pass, screen, white, waveObstacle);
                }
                wave.Mask.ColorAttachments[0].CopyTo(wave.PreviousMask.ColorAttachments[0]);
                int substeps = EventHorizonSettings.Substeps(resolution, 2.3f, HorizonSettings.WaveSpeed);
                for (int tick = 0; tick < (int)Math.Ceiling(2.34f / HorizonSettings.WaveSpeed * 60); tick++)
                {
                    if (animated)
                        foreach (var endpoint in new[] { (wave.Start, tick / 60f), (wave.End, (tick + 1) / 60f) })
                        {
                            using var pass = frame.BeginPass(endpoint.Item1, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
                            waveBaseline.SetUniform("uRadius", 2.3f);
                            waveBaseline.SetUniform("uSampleTime", endpoint.Item2);
                            waveBaseline.SetUniform("uWaveScale", 1.25f);
                            waveBaseline.SetUniform("uDistortion", .04f);
                            waveBaseline.SetUniform("uSpeed", HorizonSettings.Speed);
                            Mesh(pass, screen, white, waveBaseline);
                        }
                    waveStep.SetUniform("uState", 0);
                    waveStep.SetUniform("uObstacles", 1);
                    waveStep.SetUniform("uPreviousObstacles", 2);
                    waveStep.SetUniform("uBaselineStart", 3);
                    waveStep.SetUniform("uBaselineEnd", 4);
                    waveStep.SetUniform("uCell", wave.Cell);
                    waveStep.SetUniform("uDt", 1f / 60 / substeps);
                    waveStep.SetUniform("uRadius", 2.3f);
                    waveStep.SetUniform("uDamping", 2.4f);
                    waveStep.SetUniform("uSpeedSquared", HorizonSettings.WaveSpeed * HorizonSettings.WaveSpeed);
                    waveStep.SetUniform("uResponse", 1f);
                    waveStep.SetUniform("uMotionStep", 0f);
                    waveStep.SetUniform("uContactTime", (tick + 1) / 60f);
                    using (wave.Mask.ColorAttachments[0].Bind(1))
                    using (wave.PreviousMask.ColorAttachments[0].Bind(2))
                    using (wave.Start.ColorAttachments[0].Bind(3))
                    using (wave.End.ColorAttachments[0].Bind(4))
                        for (int sub = 0; sub < substeps; sub++)
                        {
                            using (var pass = frame.BeginPass(wave.Other, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
                            {
                                waveStep.SetUniform("uPhase", (sub + .5f) / substeps);
                                waveStep.SetUniform("uFirstSubstep", sub == 0 ? 1 : 0);
                                waveStep.SetUniform("uPacket", packet && tick == 0 && sub == 0 ? 1 : 0);
                                Mesh(pass, screen, wave.State.ColorAttachments[0], waveStep);
                            }
                            (wave.State, wave.Other) = (wave.Other, wave.State);
                        }
                }
                byte[] bytes = new byte[resolution * resolution * 4];
                using (var pass = frame.BeginPass(result, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
                {
                    Mesh(pass, screen, wave.State.ColorAttachments[0], probe);
                    pass.ReadColorRgba32(bytes);
                }
                double left = 0, right = 0, total = 0;
                for (int y = 0; y < resolution; y++)
                    for (int x = 0; x < resolution; x++)
                    {
                        int i = (y * resolution + x) * 4;
                        if (bytes[i + 3] != 255)
                            throw new InvalidOperationException("Non-finite GPU wave state.");
                        double h = (bytes[i] - bytes[i + 1]) / 255.0;
                        total += h * h;
                        if (x < resolution * .43)
                            left += h * h;
                        if (x > resolution * .6)
                            right += h * h;
                    }
                return (left, right, total);
            }
            var baseline = Run(false, false, true);
            var open = Run(false, true, false);
            var reflected = Run(true, true, false);
            var stationary = Run(true, false, true);
            Console.WriteLine($"WAVE CHECK {resolution}: baseline={baseline.Total:F6} packet openL={open.Left:F3} wallL={reflected.Left:F3} openR={open.Right:F3} wallR={reflected.Right:F3} stationary={stationary.Total:F3}");
            if (baseline.Total != 0 || reflected.Left <= open.Left * 1.2 || reflected.Right > open.Right * .05 || stationary.Total < 1)
                throw new InvalidOperationException("GPU wave reflection diagnostic failed.");
        }
        Console.WriteLine("WAVE GPU CHECKS PASSED: unchanged no-obstacle baseline, reflected packet, blocked transmission, continuous stationary boundary at all quality grids.");
    }
    void CheckPlayerEntryWaves(RenderFrame frame, RenderView view, ShaderProgram probe)
    {
        var gate = World.Definition.Entities.First(e => e.Model == "stargate");
        var savedSettings = HorizonSettings;
        var savedContacts = horizonStates.ToArray();
        try
        {
            foreach (int resolution in new[] { 256, 384, 512 })
            {
                DisposeHorizonWaves();
                horizonStates.Clear();
                var contacts = new EventHorizonState();
                horizonStates[gate.Id] = contacts;
                HorizonSettings = savedSettings with
                {
                    SimulationResolution = resolution
                };
                using var result = graphics.CreateRenderTarget(new(resolution, resolution, [TextureFormat.RGBA8Unorm], null));
                double energy = 0, peak = 0, earlyPeak = 0;
                int entries = 0, earlyChanged = 0;
                probe.SetUniform("uHeightScale", 10f);
                (double Energy, double Peak) ReadField(HorizonWave wave)
                {
                    byte[] bytes = new byte[resolution * resolution * 4];
                    using (var pass = frame.BeginPass(result, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
                    {
                        Mesh(pass, screen, wave.State.ColorAttachments[0], probe);
                        pass.ReadColorRgba32(bytes);
                    }
                    double energy = 0, peak = 0;
                    for (int i = 0; i < bytes.Length; i += 4)
                    {
                        if (bytes[i + 3] != 255)
                            throw new InvalidOperationException("Non-finite player wave.");
                        double h = (bytes[i] - bytes[i + 1]) / 255.0;
                        energy += h * h;
                        peak = Math.Max(peak, Math.Abs(h) / 10);
                    }
                    return (energy, peak);
                }
                int ChangedHighlights(float time)
                {
                    byte[] Render(bool disturbed)
                    {
                        using var pass = frame.BeginPass(result, new RenderPassDescriptor { View = view, State = ScreenState, Time = time, ColorLoadAction = RenderLoadAction.Clear, ClearColor = new Color(0, 0, 0, 0) });
                        bool savedFog = deferOpaqueFog;
                        deferOpaqueFog = true;
                        try
                        {
                            DrawHorizons(pass, false, time, disturbed);
                        }
                        finally
                        {
                            deferOpaqueFog = savedFog;
                        }
                        byte[] bytes = new byte[resolution * resolution * 4];
                        pass.ReadColorRgba32(bytes);
                        return bytes;
                    }
                    var plain = Render(false);
                    var disturbed = Render(true);
                    int changed = 0;
                    for (int i = 0; i < plain.Length; i += 4)
                        if (Math.Abs(plain[i] - disturbed[i]) + Math.Abs(plain[i + 1] - disturbed[i + 1]) + Math.Abs(plain[i + 2] - disturbed[i + 2]) > 12)
                            changed++;
                    return changed;
                }
                // Exercise persistent player capsules without prop contacts. Check
                // visible ripple warp before the eye crosses, not a large height kick.
                for (int tick = 0; tick <= 24; tick++)
                {
                    float time = 10 + tick / 60f, z = .8f - tick * StargateGame.Shared.Movement.Dt * 4;
                    var feet = gate.Position + new Vector3(0, StargateGame.Shared.PlanetSettings.GateEmbedDepth, z);
                    var player = new StargateGame.Shared.PlayerState(1, "Wave check", feet, default, 0, 0, true, 0);
                    contacts.Update(gate, [player], time, World.Definition.LocationId, 1);
                    AdvanceHorizonWaves(frame, view, time);
                    entries = Math.Max(entries, contacts.Impulses.Count);
                    if (contacts.Impulses.Count > 0 && time - contacts.Impulses[0].Started <= .035 && horizonWaves.TryGetValue(gate.Id, out var early))
                    {
                        earlyPeak = Math.Max(earlyPeak, ReadField(early).Peak);
                        earlyChanged = Math.Max(earlyChanged, ChangedHighlights(time));
                    }
                }
                if (horizonWaves.TryGetValue(gate.Id, out var wave))
                {
                    (energy, peak) = ReadField(wave);
                    // Compare actual horizon shading at identical time/view. A nonzero
                    // simulation alone cannot prove that the visible white folds change.
                    byte[] Render(bool disturbed)
                    {
                        using var pass = frame.BeginPass(result, new RenderPassDescriptor { View = view, State = ScreenState, Time = 10.4f, ColorLoadAction = RenderLoadAction.Clear, ClearColor = new Color(0, 0, 0, 0) });
                        bool savedFog = deferOpaqueFog;
                        deferOpaqueFog = true;
                        try
                        {
                            DrawHorizons(pass, false, 10.4f, disturbed);
                        }
                        finally
                        {
                            deferOpaqueFog = savedFog;
                        }
                        byte[] bytes = new byte[resolution * resolution * 4];
                        pass.ReadColorRgba32(bytes);
                        return bytes;
                    }
                    var plain = Render(false);
                    var disturbed = Render(true);
                    int changed = 0;
                    for (int i = 0; i < plain.Length; i += 4)
                        if (Math.Abs(plain[i] - disturbed[i]) + Math.Abs(plain[i + 1] - disturbed[i + 1]) + Math.Abs(plain[i + 2] - disturbed[i + 2]) > 12)
                            changed++;
                    Console.WriteLine($"PLAYER SHADE CHECK {resolution}: changed_pixels={changed}");
                    if (changed < resolution * resolution * .001)
                        throw new InvalidOperationException("Player GPU wave does not alter visible horizon highlights.");
                }
                // Removal plus a routine physics epoch change must retain the field.
                double releaseEnergy = 0, lateEnergy = 0;
                float lateFade = 1;
                for (int tick = 1; tick <= 190; tick++)
                {
                    float t = 10.4f + tick / 60f;
                    contacts.Update(gate, [], t, World.Definition.LocationId, 2);
                    AdvanceHorizonWaves(frame, view, t);
                    var residual = horizonWaves[gate.Id];
                    if (tick == 1)
                        releaseEnergy = ReadField(residual).Energy;
                    if (tick == 90)
                        lateEnergy = ReadField(residual).Energy;
                    if (tick == 150)
                        lateFade = residual.Fade;
                }
                var endField = horizonWaves[gate.Id];
                if (releaseEnergy <= 0 || lateEnergy <= 0 || lateFade <= 0 || lateFade >= 1 || endField.WasActive || ReadField(endField).Energy != 0)
                    throw new InvalidOperationException("Departed contact wake did not propagate and decay for three seconds.");
                Console.WriteLine($"WAKE RELEASE CHECK {resolution}: release_energy={releaseEnergy:F6} after_1_5s={lateEnergy:F6} fade_2_5s={lateFade:F4} final_empty=true");
                Console.WriteLine($"PLAYER WAVE CHECK {resolution}: entries={entries} obstacles={contacts.Footprints.Count} energy={energy:F6} peak_mm={peak * 1000:F3} first_35ms_peak_mm={earlyPeak * 1000:F3} first_35ms_changed_pixels={earlyChanged}");
                if (entries != 1 || contacts.Footprints.Count != 0 || energy < 1 || peak < .0005 || earlyPeak < .0001 || earlyChanged == 0)
                    throw new InvalidOperationException("Player entry did not bend the surface before the first-person camera crossed.");
            }
        }
        finally
        {
            DisposeHorizonWaves();
            horizonStates.Clear();
            foreach (var pair in savedContacts)
                horizonStates[pair.Key] = pair.Value;
            HorizonSettings = savedSettings;
        }
    }
}
