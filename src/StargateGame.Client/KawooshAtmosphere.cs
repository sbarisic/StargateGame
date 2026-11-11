using System.Numerics;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class SceneRenderer
{
    public bool KawooshMistEnabled
    {
        get;
        set;
    } = true;
    public bool KawooshBloomEnabled
    {
        get;
        set;
    } = true;

    void KawooshVolumeUniforms(ShaderProgram program, Vector3 eye)
    {
        int count = 0;
        if (!HorizonSettings.Enabled || !KawooshMistEnabled)
        {
            program.SetUniform("uKawooshCount", 0);
            return;
        }
        foreach (var gate in World.Definition.Entities.Where(e => e.Model == "stargate"))
        {
            if (count == 2)
                break;
            using var endpoint = SelectGate(gate);
            var effect = GateEffect;
            float age = effect.OpeningAge;
            float envelope = age < 0 ? 0 : GateEffectTimeline.Smooth(1.15f, 1.65f, age) *
                (1 - GateEffectTimeline.Smooth(2.8f, 3.9f, age));
            if (effect.ClosingAge >= 0)
                envelope *= 1 - GateEffectTimeline.Smooth(0, .2f, effect.ClosingAge);
            if (envelope <= 0)
                continue;
            // A lagged analytical envelope follows the spray without simulation history.
            // This also reconstructs correctly on delayed updates and planet changes.
            var trail = GateEffectTimeline.Evaluate(GatePhase.Opening, Math.Max(0, age - .18f));
            float reach = Math.Max(effect.Extension, trail.Extension);
            if (effect.ClosingAge >= 0)
                reach = effect.Extension;
            if (Vector3.DistanceSquared(EventHorizonState.Centre(gate), eye) > 96 * 96)
                continue;
            if (!Matrix4x4.Invert(gate.Transform, out var inverse))
                continue;
            program.SetUniform($"uKawooshInverse[{count}]", inverse);
            program.SetUniform($"uKawooshParameters[{count}]", new Vector4(envelope, age, Math.Max(.12f, reach), effect.Retraction));
            count++;
        }
        program.SetUniform("uKawooshCount", count);
        program.SetUniform("uKawooshSamples", Settings.Quality == GraphicsQuality.High ? 5 : Settings.Quality == GraphicsQuality.Low ? 2 : 3);
    }
}
