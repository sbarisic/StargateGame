namespace StargateGame.Shared;

// Seconds from the endpoint's authoritative PhaseTick, never frame counts.
public readonly record struct GateEffectFrame(float OpeningAge, float ClosingAge, bool Visible,
    float Formation, float Extension, float Retraction, float Activity, float Stability,
    float Flash, float Breakup, float Light, float Chevron, float Hum, float Power, float SprayAge, float SprayFade, float SurfaceFade = 1);

public static class GateEffectTimeline
{
    public const int TicksPerSecond = 60, OpeningTicks = 240, IncomingLeadTicks = 42,
        ReceiverSettledTicks = OpeningTicks + IncomingLeadTicks, ClosingTicks = 245;
    public const float OpeningSeconds = 4, ClosingSeconds = ClosingTicks / (float)TicksPerSecond, SprayStart = 1.15f, SprayEnd = 2.2f,
        ClosingSurfaceSeconds = 2.0f, SprayLifetimeMin = .4f, SprayLifetimeMax = 1.1f, SprayGone = 3.5f;
    public static float Smooth(float a, float b, float t)
    {
        float x = Math.Clamp((t - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }
    // Shared removal curve: opening evaluates the same curve backwards.
    public static float Dissolve(float progress)
    {
        float hold = .20f / ClosingSurfaceSeconds;
        float x = Math.Clamp((progress - hold) / (1 - hold), 0, 1);
        return 1 - MathF.Pow(1 - x, 1.65f);
    }
    public static float Remnant(float progress) => 1 - Smooth(.65f, 1, progress);
    static float OpeningLight(float t) => t < 1.1f ? .55f * Smooth(.5f, 1.1f, t) :
        t < 2.2f ? .55f + 1.1f * Smooth(1.1f, 2.2f, t) : 1.65f - .65f * Smooth(2.2f, 3.5f, t);
    public static GateEffectFrame Evaluate(GatePhase phase, double age, double interruptedOpeningAge = -1)
    {
        if (!double.IsFinite(age))
            return default;
        float t = (float)Math.Clamp(age, -100, 10000);
        if (phase == GatePhase.Open)
            return new(-1, -1, true, 1, 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, -1, 0);
        if (phase == GatePhase.Opening && t >= 0)
        {
            float stability = Smooth(2.7f, 3.5f, t);
            float formation = Math.Clamp((t - .3f) / .8f, 0, 1);
            float surge = 1 - MathF.Pow(1 - Math.Clamp((t - 1.3f) / .7f, 0, 1), 3);
            float retraction = MathF.Pow(Math.Clamp((t - 2.2f) / 1.1f, 0, 1), 2.2f);
            return new(Math.Min(t, OpeningSeconds), -1, true, formation,
                surge * (1 - retraction), retraction,
                Smooth(1.1f, 1.4f, t) * (1 - stability), stability, 1 - Smooth(1.3f, 2.1f, t), Dissolve(1 - formation), OpeningLight(t), 1, stability,
                .5f * (1 - stability), t, 1 - Smooth(3.3f, SprayGone, t), Remnant(1 - formation));
        }
        if (phase == GatePhase.Closing && t >= 0 && t < ClosingSeconds)
        {
            // Shutdown cue is 4.08 s; round up to 245 authoritative ticks.
            float progress = t / ClosingSeconds;
            float peak = Smooth(0, .15f, t) * (1 - Smooth(.20f, 1.1f, t));
            float flash = Smooth(0, .15f, t) * (1 - .08f * Smooth(.20f, 1.1f, t));
            float surfaceProgress = t / ClosingSurfaceSeconds;
            float breakup = Dissolve(surfaceProgress), fade = Remnant(progress);
            var result = new GateEffectFrame(-1, t, t < ClosingSurfaceSeconds, 1, 0, 0, 0, 1, flash, breakup,
                Math.Min(1.65f, (1 + .65f * peak) * (1 - breakup)), fade, 1 - Smooth(0, .25f, t), 0, -1, 0, Remnant(surfaceProgress));
            if (double.IsFinite(interruptedOpeningAge) && interruptedOpeningAge >= 0 && interruptedOpeningAge < OpeningSeconds)
            {
                var start = Evaluate(GatePhase.Opening, interruptedOpeningAge);
                float withdraw = 1 - Smooth(0, .22f, t);
                result = result with
                {
                    OpeningAge = start.OpeningAge,
                    Formation = start.Formation + (1 - start.Formation) * Smooth(0, .12f, t),
                    Breakup = Math.Max(result.Breakup, start.Breakup),
                    Extension = start.Extension * withdraw,
                    Retraction = start.Retraction + (1 - start.Retraction) * Smooth(0, .22f, t),
                    Activity = start.Activity * withdraw,
                    SprayAge = start.OpeningAge + t,
                    SprayFade = 1 - Smooth(0, .1f, t)
                };
            }
            return result;
        }
        return new(-1, -1, false, 0, 0, 0, 0, 0, 0, 0, 0, phase is GatePhase.Idle or GatePhase.Failed ? 0 : 1, 0,
            phase == GatePhase.Preparing ? .5f : 0, -1, 0);
    }
}
