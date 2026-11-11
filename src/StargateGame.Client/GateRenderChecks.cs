using System.Numerics;
using FishGfx;
using FishGfx.Graphics;
using StargateGame.Shared;
namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    // Runs only with --horizon-wave-check. Tests the actual shipped GLSL.
    void CheckGateRendering(RenderFrame frame, RenderView view)
    {
        var probe = LoadProgram("fullscreen.vert", "gate_probe.frag");
        using var result = graphics.CreateRenderTarget(new(64, 64, [TextureFormat.RGBA8Unorm], null));
        using var coverage = graphics.CreateRenderTarget(new(64, 64, [TextureFormat.RGBA8Unorm], null));
        probe.SetUniform("uProbeMode", 0);
        probe.SetUniform("uBackSheet", 0);
        probe.SetUniform("uPlumeLength", KawooshShape.Length);
        int maximum = 0, count = 0;
        byte[] pixels = new byte[64 * 64 * 4];
        foreach (float age in new[] { 1.30f, 1.45f, 1.8f, 2.1f, 2.7f, 3.1f, 3.29f })
            foreach (float radius in new[] { 0f, .15f, .50f, .679f, .681f, .78f, .899f, .901f, .96f, 1f })
                foreach (float angle in new[] { 0f, 1.2f, 3.4f })
                {
                    var f = GateEffectTimeline.Evaluate(GatePhase.Opening, age);
                    var p = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius * KawooshShape.Radius;
                    probe.SetUniform("uOpeningAge", age);
                    probe.SetUniform("uGateShape", new Vector4(f.Extension, f.Retraction, f.Activity, f.Stability));
                    probe.SetUniform("uProbePoint", p);
                    probe.SetUniform("uExpected", KawooshShape.Position(p, f));
                    using var pass = frame.BeginPass(result, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare });
                    Mesh(pass, screen, white, probe);
                    pass.ReadColorRgba32(pixels);
                    int error = Math.Max(pixels[0], Math.Max(pixels[1], pixels[2]));
                    if (error > maximum)
                        Console.WriteLine($"PLUME parity age={age} r={radius} angle={angle} error_rgb={pixels[0]},{pixels[1]},{pixels[2]}");
                    maximum = Math.Max(maximum, error);
                    count++;
                }
        if (maximum > 128)
            throw new InvalidOperationException("CPU/render liquid shape differs by more than five millimetres.");
        Console.WriteLine($"PLUME CPU/GPU PARITY samples={count} maximum_error_mm={maximum / 25.5:F4}");
        probe.SetUniform("uProbeMode", 1);
        using (var pass = frame.BeginPass(coverage, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
            Mesh(pass, screen, white, probe);
        horizonBlur ??= LoadProgram("fullscreen.vert", "horizon_blur.frag");
        horizonBlur.SetUniform("uViewmodelCoverage", 1);
        horizonBlur.SetUniform("uMaskViewmodel", 1);
        horizonBlur.SetUniform("uBlurStep", new Vector2(1f / 64, 0));
        using (coverage.ColorAttachments[0].Bind(1))
        using (var pass = frame.BeginPass(result, new RenderPassDescriptor { View = view, State = ScreenState, ColorLoadAction = RenderLoadAction.DontCare }))
        {
            Mesh(pass, screen, white, horizonBlur);
            pass.ReadColorRgba32(pixels);
        }
        int At(int x, int y) => pixels[(y * 64 + x) * 4];
        if (At(12, 16) > 1 || At(56, 16) < 250 || At(27, 33) < 250)
            throw new InvalidOperationException("Pre-blur weapon coverage hides visible light or leaks hidden emission.");
        horizonBlur.SetUniform("uMaskViewmodel", 0);
        Console.WriteLine("BLOOM COVERAGE GPU PASS: hidden emission removed before blur; open sight and uncovered light retained.");
    }
}
