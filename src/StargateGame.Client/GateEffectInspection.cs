using System.Globalization;
using FishGfx.Graphics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class GameApp
{
    int gateEffectCapture;
    const float GateCaptureStep = .05f;
    static readonly int OpeningCaptureCount = (int)Math.Ceiling(GateEffectTimeline.OpeningSeconds / GateCaptureStep) + 1;
    static readonly int GateEffectCaptureCount = OpeningCaptureCount + 2 + (int)Math.Ceiling(GateEffectTimeline.ClosingSeconds / GateCaptureStep);
    void InspectGateEffect(double age)
    {
        if (!Smoke || scene is null || session is null)
            return;
        bool sequence = args.Contains("--gate-effect-sequence");
        string effect = Arguments.Get(args, "--gate-effect", "");
        if (!sequence && effect.Length == 0)
            return;
        // Fixtures have no galaxy association; inspection still needs a presentation state.
        var gate = session.Gate ?? new GateState(session.World.Definition.LocationId, Guid.Empty, Guid.Empty, 0, GatePhase.Idle, [], 0, 0, Guid.Empty);
        float seconds;
        GatePhase phase;
        if (sequence)
        {
            int index = Math.Min(gateEffectCapture, GateEffectCaptureCount - 1);
            phase = index < OpeningCaptureCount ? GatePhase.Opening : index == OpeningCaptureCount ? GatePhase.Open : GatePhase.Closing;
            seconds = index < OpeningCaptureCount ? Math.Min(index * GateCaptureStep, GateEffectTimeline.OpeningSeconds) : index == OpeningCaptureCount ? 0 : Math.Min((index - OpeningCaptureCount - 1) * GateCaptureStep, GateEffectTimeline.ClosingSeconds);
            if (age < 8)
            {
                phase = GatePhase.Open;
                seconds = 0;
            }
        }
        else
        {
            phase = effect switch
            {
                "opening" => GatePhase.Opening,
                "closing" => GatePhase.Closing,
                "open" => GatePhase.Open,
                "idle" => GatePhase.Idle,
                _ => throw new ArgumentException("--gate-effect must be opening, closing, open or idle.")
            };
            seconds = float.Parse(Arguments.Get(args, "--gate-effect-time", "0"), CultureInfo.InvariantCulture);
            if (!float.IsFinite(seconds) || seconds < 0)
                throw new ArgumentException("--gate-effect-time must be finite and non-negative.");
        }
        // Render-only state; no DHD command, server mutation or audio event.
        scene.InspectActiveGate = false;
        scene.AuthoritativeGate = gate with
        {
            Phase = phase,
            PhaseTick = 0,
            Symbols = [2, 3, 4, 5, 6, 7, 1],
            Incoming = false
        };
        scene.GateVisualTick = seconds * GateEffectTimeline.TicksPerSecond;
        scene.GateConnectionStartTick = -1;
    }
    void CaptureGateEffect(RenderPass pass, double age)
    {
        if (!Smoke || !args.Contains("--gate-effect-sequence") || age < 8 || scene is null || gateEffectCapture >= GateEffectCaptureCount)
            return;
        string name = $"gate-{gateEffectCapture:D2}-{scene.AuthoritativeGate!.Phase}-{scene.GateVisualTick / 60:F2}.png";
        Capture(pass, Path.Combine(captureDirectory, name));
        Console.WriteLine($"GATE EFFECT frame={gateEffectCapture} phase={scene.AuthoritativeGate.Phase} seconds={scene.GateVisualTick / 60:F3} draws={scene.HorizonDrawCalls}");
        gateEffectCapture++;
    }
    int gateMovieFrame;
    double nextGateMovie;
    bool gateMovieStarted;
    double gateMovieClosing = -1;
    void CaptureGateMovie(RenderPass pass, double now)
    {
        bool contactMovie = args.Contains("--horizon-movie");
        var gate = contactMovie ? scene?.AuthoritativeGate : session?.Gate;
        if (!Smoke || (!args.Contains("--gate-movie") && !contactMovie) || gate is null)
            return;
        if (contactMovie)
        {
            if (now - connectedAt < 8 || now - connectedAt > 16)
                return;
            gateMovieStarted = true;
        }
        if (gate.Phase == GatePhase.Opening)
            gateMovieStarted = true;
        if (!gateMovieStarted || now < nextGateMovie || gateMovieFrame >= 900)
            return;
        if (gate.Phase == GatePhase.Closing && gateMovieClosing < 0)
            gateMovieClosing = now;
        if (gateMovieClosing >= 0 && now > gateMovieClosing + 4.2)
            return;
        string file = $"gate-live-{gateMovieFrame:D4}.jpg";
        Capture(pass, Path.Combine(captureDirectory, file), true);
        File.AppendAllText(Path.Combine(captureDirectory, "gate-live.csv"), $"{file},{(now - audioRecordStarted).ToString("F6", CultureInfo.InvariantCulture)},{gate.Phase},{(((session?.GateTime ?? 0) - gate.PhaseTick) / 60).ToString("F6", CultureInfo.InvariantCulture)}\n");
        gateMovieFrame++;
        nextGateMovie = now + 1.0 / 30;
    }

}
