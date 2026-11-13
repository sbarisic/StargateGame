using System.Numerics;
using StargateGame.Shared;

namespace StargateGame.Client;

sealed partial class WorldAudioController
{
    sealed class ReloadPlayback
    {
        public readonly RifleReloadAudio Timeline = new();
        public readonly List<Voice> Voices = [];
    }
    readonly Dictionary<int, ReloadPlayback> reloads = [];
    public readonly record struct ReloadCueEvent(int Player, long Sequence, long StartTick, double Tick,
        string Cue, float Offset, Vector3 Position, bool Local);
    public readonly List<ReloadCueEvent> TestReloadEvents = [];

    void StopReload(ReloadPlayback playback)
    {
        foreach (var voice in playback.Voices)
        {
            audio.Stop(voice.Handle, .015f);
            voices.Remove(voice);
        }
        playback.Voices.Clear();
    }

    void ReloadEvents(ClientSession session, PlayerState local, PlayerState[] remotes, double tick)
    {
        foreach (int id in reloads.Keys.Where(id => id != local.Id && !remotes.Any(p => p.Id == id)).ToArray())
        {
            StopReload(reloads[id]); reloads.Remove(id);
        }
        foreach (var actor in remotes.Append(local))
        {
            var state = session.Characters.GetValueOrDefault(actor.Id);
            if (!reloads.TryGetValue(actor.Id, out var playback))
            {
                if (state?.Action != "reload") continue;
                reloads.Add(actor.Id, playback = new());
            }
            bool isLocal = actor.Id == local.Id;
            var position = isLocal ? eye : StargateGame.Shared.Movement.EyePosition(actor);
            playback.Voices.RemoveAll(v => !voices.Contains(v));
            foreach (var voice in playback.Voices) voice.Position = position;
            playback.Timeline.Update(state, tick, cue => cues.TryGetValue(cue, out var definition)
                ? definition.Variants.Max(v => duration.GetValueOrDefault(Path.GetFileName(v.Path), 0)) : 0,
                (cue, offset) =>
                {
                    string id = cue + (isLocal ? ".local" : "");
                    var voice = Play(id, position, isLocal ? 4 : 2, offset: offset);
                    if (voice is null) return;
                    playback.Voices.Add(voice);
                    if (TestReloadEvents.Count >= 128) TestReloadEvents.RemoveAt(0);
                    TestReloadEvents.Add(new(actor.Id, state!.Sequence, state.StartTick, tick, id, offset, position, isLocal));
                }, () => StopReload(playback));
        }
    }
}
