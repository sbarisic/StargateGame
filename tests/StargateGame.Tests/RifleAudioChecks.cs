using System.Text.Json;
using StargateGame.Client;
using StargateGame.Shared;

static class RifleAudioChecks
{
    public static void Run()
    {
        var state = new CharacterPresentation(7, 1, true, false, "reload", 100, 264);
        var timeline = new RifleReloadAudio();
        var played = new List<(string Cue, float Offset)>();
        int stopped = 0;
        void At(double age, CharacterPresentation? value = null) => timeline.Update(value ?? state,
            100 + age * 60, _ => .2f, (cue, offset) => played.Add((cue, offset)), () => stopped++);
        At(0);
        foreach (var marker in RifleReloadAudio.Markers)
        {
            int before = played.Count;
            At(marker.Seconds - .01);
            Verify.Check(played.Count == before, "reload cue never plays before its animation marker");
            At(marker.Seconds + .02);
            Verify.Check(played.Count == before + 1 && played[^1].Cue == marker.Cue, "each marker plays once");
            Verify.Near(played[^1].Offset, .02f, .0001f, "frame delay seeks into mechanical cue");
            At(marker.Seconds + .02);
            Verify.Check(played.Count == before + 1, "repeated state does not duplicate cue");
        }
        Verify.Check(RifleReloadAudio.Markers.Select(m => m.Seconds).SequenceEqual(new[] { .4, 1.18, 1.8, 2.1 }), "authored reload timing");
        At(2.74);
        Verify.Check(stopped == 1, "reload completion stops active mechanics");
        At(.4);
        Verify.Check(played.Count == 4, "completed baseline and clock rewind cannot resurrect reload");

        timeline = new(); played.Clear(); stopped = 0;
        At(1.3); // First observation arrives after both magazine actions.
        Verify.Check(played.Count == 0, "late state skips past events rather than bursting them");
        At(1.81);
        Verify.Check(played.Count == 1 && played[0].Cue.EndsWith("pull"), "late observation retains future events");
        At(2.5);
        Verify.Check(played.Count == 1, "hitch beyond clip end skips expired sound");
        At(2.51, state with { Action = "idle", ReloadUntil = 0 });
        Verify.Check(stopped == 1, "action invalidation stops active sounds");
        At(2.52);
        Verify.Check(played.Count == 1, "old reload snapshot stays cancelled");

        timeline = new(); played.Clear(); stopped = 0;
        At(0, state with { Action = "idle", ReloadUntil = 0 });
        At(.5, state with { Action = "idle", ReloadUntil = 0 });
        Verify.Check(played.Count == 0, "rejected reload has no audio");
        At(0, state with { Sequence = 2 });
        At(.41, state with { Sequence = 2 });
        timeline.Update(null, 126, _ => .2f, (_, _) => throw new Exception("removed actor played audio"), () => stopped++);
        Verify.Check(stopped == 1 && played.Count == 1, "actor disappearance cancels queued and active mechanics");
        At(1.19, state with { Sequence = 1 });
        Verify.Check(played.Count == 1, "stale sequence cannot replace newer action");

        var observer = new RifleReloadAudio();
        int observed = 0;
        observer.Update(state, 100, _ => .2f, (_, _) => observed++, () => { });
        observer.Update(state, 125, _ => .2f, (_, _) => observed++, () => { });
        Verify.Check(observed == 1, "independent listener schedules the same authoritative action");
        observer.Update(state with { Sequence = 2 }, 126, _ => .2f, (_, _) => observed++,
            () => throw new Exception("acknowledgement interrupted accepted reload"));
        observer.Update(state with { Sequence = 2 }, 171, _ => .2f, (_, _) => observed++, () => { });
        Verify.Check(observed == 2, "newer rejection sequence preserves reload timeline");

        using var bank = JsonDocument.Parse(File.ReadAllText("assets/audio/audio-bank.json"));
        var cues = bank.RootElement.GetProperty("cues").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);
        Verify.Check(!cues.ContainsKey("rifle.reload"), "monolithic reload cue removed");
        Verify.Check(cues["rifle.fire"].GetProperty("variants").GetArrayLength() == 2, "two recorded firing variants");
        foreach (string id in new[] { "rifle.fire" }.Concat(RifleReloadAudio.Markers.Select(m => m.Cue)))
        {
            var cue = cues[id];
            Verify.Check(!cue.GetProperty("looping").GetBoolean(), "rifle effects are one-shots");
            foreach (var variant in cue.GetProperty("variants").EnumerateArray())
            {
                Verify.Check(!variant.TryGetProperty("pitchMin", out _) && !variant.TryGetProperty("pitchMax", out _), "no random rifle pitch");
                using var pcm = new BinaryReader(File.OpenRead(Path.Combine("assets/audio", variant.GetProperty("path").GetString()!)));
                pcm.BaseStream.Position = 20;
                Verify.Check(pcm.ReadInt16() == 1 && pcm.ReadInt16() == 1 && pcm.ReadInt32() == 48000, "PCM mono 48 kHz rifle asset");
                pcm.BaseStream.Position = 34;
                Verify.Check(pcm.ReadInt16() == 16, "16-bit rifle asset");
            }
        }
    }
}
