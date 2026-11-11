using StargateGame.Shared;
namespace StargateGame.Client;

// Compatibility adapter for diagnostics. Production uses the complete shared frame.
readonly record struct GateTransition(float OpeningAge, float ClosingAge, bool Visible, float Light)
{
    public const float OpeningSeconds = GateEffectTimeline.OpeningSeconds, ClosingSeconds = GateEffectTimeline.ClosingSeconds;
    public static GateTransition Evaluate(GatePhase phase, double age)
    {
        var f = GateEffectTimeline.Evaluate(phase, age);
        return new(f.OpeningAge, f.ClosingAge, f.Visible, f.Light);
    }
    public static float Smooth(float a, float b, float t) => GateEffectTimeline.Smooth(a, b, t);
}
