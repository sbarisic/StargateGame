using StargateGame.Client;
using StargateGame.Shared;

static class GateTransitionChecks
{
    public static void Run()
    {
        Verify.Check(!GateTransition.Evaluate(GatePhase.Idle, 0).Visible, "idle gate has no transition surface");
        Verify.Check(!GateTransition.Evaluate(GatePhase.Opening, -.01).Visible, "scheduled opening stays hidden until its server tick");
        foreach (double age in new[] { 0, .3, 1.1, 1.3, 2, 2.2, 2.7, 3.3, 3.5, 4, 8 })
        {
            var live = GateTransition.Evaluate(GatePhase.Opening, age);
            Verify.Check(live == GateTransition.Evaluate(GatePhase.Opening, age), "late join/colour/bloom agree at " + age);
            Verify.Check(live.Visible && float.IsFinite(live.Light) && live.Light is >= 0 and <= 1.65f, "bounded opening illumination at " + age);
        }
        Verify.Near(GateTransition.Evaluate(GatePhase.Opening, 4).Light, 1, .00001f, "opening settles to unchanged illumination");
        foreach (double age in new[] { 0, .1, .23, .35, .45, .59 })
        {
            var closing = GateTransition.Evaluate(GatePhase.Closing, age);
            Verify.Check(closing.Visible && closing.Light is >= 0 and <= 1.65f, "shutdown has bounded illumination at " + age);
        }
        Verify.Check(!GateTransition.Evaluate(GatePhase.Closing, GateEffectTimeline.ClosingSeconds).Visible, "shutdown has no residual surface at completion");
        Verify.Check(!GateTransition.Evaluate(GatePhase.Opening, double.NaN).Visible, "invalid clock cannot light gate");
        Verify.Check(GateEffectTimeline.OpeningTicks == 240 && GateEffectTimeline.ReceiverSettledTicks == 282 && GateEffectTimeline.ClosingTicks == 245, "server timeline tick contract");
        Verify.Near(GateEffectTimeline.Evaluate(GatePhase.Opening, 2).Extension, 1, .00001f, "plume reaches maximum at two seconds");
        Verify.Near(GateEffectTimeline.Evaluate(GatePhase.Opening, 3.3).Extension, 0, .00001f, "plume fully retracts before readiness");
        Verify.Near(GateEffectTimeline.Evaluate(GatePhase.Opening, 3.5).Stability, 1, .00001f, "stable surface before travel");
        float previous = 0;
        for (int i = 0; i <= 100; i++)
        {
            float age = i / 100f * GateEffectTimeline.ClosingSeconds;
            var effect = GateEffectTimeline.Evaluate(GatePhase.Closing, age);
            if (effect.Visible)
            {
                Verify.Check(effect.Breakup >= previous, "shutdown cannot restore removed fragments");
                previous = effect.Breakup;
            }
        }
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Closing, 1.8).Flash > .9f, "remaining sheet stays white through breakup");
        Verify.Check(GateEffectTimeline.ClosingSeconds >= 4.08f && GateEffectTimeline.ClosingSeconds < 4.1f, "closing matches natural shutdown cue");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Closing, .15).Flash > .99f, "closing whitens in 150 ms");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Opening, 1.3).Flash == 1, "opening stays white until plume begins");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Opening, 2.1).Flash == 0, "opening returns to water blue during surge");
        Verify.Check(GateEffectTimeline.Dissolve(.4f) - GateEffectTimeline.Dissolve(.2f) > GateEffectTimeline.Dissolve(.9f) - GateEffectTimeline.Dissolve(.7f), "shutdown removal slows towards the end");
        foreach (float progress in new[] { 0f, .2f, .5f, .8f, 1f })
        {
            var formation = GateEffectTimeline.Evaluate(GatePhase.Opening, .3 + (1 - progress) * .8);
            var removal = GateEffectTimeline.Evaluate(GatePhase.Closing, progress * GateEffectTimeline.ClosingSurfaceSeconds);
            if (progress < 1)
                Verify.Near(formation.Breakup, removal.Breakup, .00001f, "opening reverses the shutdown mask");
        }
        var finish = GateEffectTimeline.Evaluate(GatePhase.Closing, GateEffectTimeline.ClosingSurfaceSeconds);
        Verify.Check(!finish.Visible && finish.Chevron > 0, "surface finishes before the natural light/audio tail");
        Verify.Near(GateEffectTimeline.ClosingSurfaceSeconds, 2f, .00001f, "surface finishes in two seconds without a crawling tail");
        Verify.Check(finish.SurfaceFade == 0, "surface fade reaches zero before teardown");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Opening, 1.65).Extension > .85f, "surge is fast initially and eases into the peak");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Opening, 2.75).Retraction < .25f, "retraction starts slowly");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Opening, 3.2).Retraction > .8f, "retraction accelerates into the horizon");
        var interrupted = GateEffectTimeline.Evaluate(GatePhase.Closing, 0, 2);
        Verify.Near(interrupted.Extension, 1, .00001f, "interrupted opening starts at its current shape");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Closing, .1, 2).SprayFade == 0, "interrupted spray gone within a tenth second");
        Verify.Check(GateEffectTimeline.Evaluate(GatePhase.Closing, .22, 2).Extension == 0, "interrupted plume withdraws smoothly");
        foreach (int fps in new[] { 30, 60, 120 })
        {
            var live = GateEffectTimeline.Evaluate(GatePhase.Opening, 2.2);
            Verify.Check(live == GateEffectTimeline.Evaluate(GatePhase.Opening, (2.2 * fps) / fps), "presentation independent of frame rate and join history");
        }
        Verify.Check(GateTransition.Evaluate(GatePhase.Open, 0).OpeningAge < 0, "settled gate bypasses surge shading");
    }
}
