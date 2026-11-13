using StargateGame.Shared;

namespace StargateGame.Client;

// All times are in the authored reload clip's seconds, on the viewmodel clock.
sealed class RifleReloadAudio
{
    public readonly record struct Marker(string Cue, double Seconds);
    public static readonly Marker[] Markers =
    [
        new("rifle.reload.remove", 0.40),
        new("rifle.reload.insert", 1.18),
        new("rifle.reload.pull", 1.80),
        new("rifle.reload.release", 2.10),
    ];
    long sequence = -1, startTick = -1;
    int next;
    bool active;

    public void Update(CharacterPresentation? state, double tick, Func<string, float> duration,
        Action<string, float> play, Action stop)
    {
        if (state is not null && state.Sequence < sequence) return;
        if (state is null || !state.Equipped || state.Action != "reload" || state.ReloadUntil <= tick)
        {
            if (active) stop();
            active = false;
            if (state is not null) sequence = Math.Max(sequence, state.Sequence);
            return;
        }
        double age = Math.Max(0, (tick - state.StartTick) / 60);
        // Rejected commands can acknowledge a newer sequence while retaining
        // the same accepted reload. They must not restart/cut off its sounds.
        if (active && startTick == state.StartTick) sequence = state.Sequence;
        if (sequence != state.Sequence || startTick != state.StartTick)
        {
            // Do not resurrect a completed action from a repeated baseline.
            if (state.Sequence == sequence && state.StartTick <= startTick) return;
            if (active) stop();
            sequence = state.Sequence; startTick = state.StartTick; next = 0; active = true;
            // Late joins/state delivery do not replay historical mechanics.
            while (next < Markers.Length && Markers[next].Seconds < age) next++;
        }
        if (!active) return;
        while (next < Markers.Length && Markers[next].Seconds <= age)
        {
            var marker = Markers[next++];
            float offset = (float)(age - marker.Seconds);
            if (offset < duration(marker.Cue)) play(marker.Cue, offset);
        }
    }
}
