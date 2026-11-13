using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using StargateGame.Shared;
using StargateGame.Testing;

namespace StargateGame.Harness;

static partial class Recipes
{
    static async Task RifleAudio(SessionManifest manifest, CancellationToken token, string controller,
        Func<TestRequest, CancellationToken, Task<TestResult>>? dispatch)
    {
        if (manifest.Options.Scenario != "sandbox") throw new ArgumentException("Rifle audio requires Sandbox.");
        async Task<TestResult> Send(TestAction action, object? data = null, int client = 1)
        {
            var result = await (dispatch ?? ((r, ct) => LocalPipe.Send(manifest.Pipe, r, ct)))(new()
            {
                Session = manifest.Id, Generation = manifest.Generation, Controller = controller, Action = action,
                Data = TestJson.Element(data ?? new { }), Client = client, TimeoutSeconds = 60,
            }, token);
            result.EnsureSuccess(); return result;
        }
        async Task<JsonElement> State(int client = 1) => (await Send(TestAction.Status)).Data.GetProperty("clients")[client-1];
        async Task<JsonElement> Until(Func<JsonElement, bool> predicate, int client = 1)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 15)
            {
                var state = await State(client);
                if (predicate(state)) return state;
                await Task.Delay(25, token);
            }
            throw new TimeoutException("Rifle audio condition not reached on client " + client);
        }
        int Count(JsonElement s, string cue) => s.GetProperty("audioCues").TryGetProperty(cue, out var n) ? n.GetInt32() : 0;
        int Rounds(JsonElement s)
        {
            var ammo = s.GetProperty("rifle").GetProperty("ammo");
            return ammo.GetProperty("magazine").GetInt32() + (ammo.GetProperty("chambered").GetBoolean() ? 1 : 0);
        }
        if (manifest.ClientPids.Length < 2) await Send(TestAction.AddClient);
        await Send(TestAction.Teleport, new { station = "props" });
        var initial = await State();
        var player = initial.GetProperty("player").Deserialize<PlayerState>(TestJson.Options);
        await Send(TestAction.Teleport, new { position = player.Position + new Vector3(3, 0, 0) }, 2);
        await Send(TestAction.Camera, new { bookmark = "player" });
        await Send(TestAction.Aim, new { target = player.Position + new Vector3(0, 10, -10) });
        await Send(TestAction.Wait, new { condition = "captureReady" });
        await Until(s => s.GetProperty("playable").GetBoolean(), 2);
        initial = await State();
        if (Rounds(initial) < 12) throw new InvalidOperationException("Rifle recipe needs at least 12 rounds; use a fresh Sandbox session.");
        var frames = new List<object>();
        try
        {
            await Send(TestAction.AudioRecord, new { name = "rifle-local.wav" });
            await Send(TestAction.AudioRecord, new { name = "rifle-observer.wav" }, 2);
            // Held gameplay input, not synthetic sound events, exercises server acceptance.
            await Send(TestAction.Input, new { buttons = new[] { "Left" }, seconds = 1.2 });
            await Send(TestAction.Input, new { seconds = .35 }); // Drain the last accepted shot before counting ammo.
            var fired = await Until(s => Rounds(s) < Rounds(initial));
            int shots = Rounds(initial) - Rounds(fired);
            if (shots < 2) throw new InvalidOperationException("Automatic fire did not produce multiple accepted shots.");
            await Until(s => Count(s, "rifle.fire.local") == shots);
            await Until(s => Count(s, "rifle.fire") == shots, 2);
            await Send(TestAction.Input, new { keys = new[] { "R" }, seconds = .08 });
            await Until(s => s.GetProperty("rifle").GetProperty("character").GetProperty("action").GetString() == "reload");
            var markers = (await State()).GetProperty("rifle").GetProperty("reloadMarkers").EnumerateArray()
                .Select(m => (Cue: m.GetProperty("cue").GetString()!, Seconds: m.GetProperty("seconds").GetDouble())).ToArray();
            foreach (var marker in markers)
            {
                var at = await Until(s => Count(s, marker.Cue + ".local") == 1);
                await Send(TestAction.Capture, new { name = marker.Cue.Replace('.', '-') + ".png", immediate = true });
                frames.Add(new { marker, state = at.GetProperty("rifle") });
            }
            var finished = await Until(s => !s.GetProperty("rifle").GetProperty("reloading").GetBoolean());
            foreach (var marker in markers) await Until(s => Count(s, marker.Cue) == 1, 2);
            // Full-magazine reload input must remain silent.
            await Send(TestAction.Input, new { keys = new[] { "R" }, seconds = .08 });
            await Send(TestAction.Input, new { seconds = 3.0 });
            var local = await State(); var remote = await State(2);
            if (Count(local, "rifle.fire.local") != shots || Count(remote, "rifle.fire") != shots)
                throw new InvalidOperationException("Accepted shot and playback counts disagree.");
            foreach (var marker in markers)
            {
                if (Count(local, marker.Cue + ".local") != 1 || Count(remote, marker.Cue) != 1)
                    throw new InvalidOperationException("Reload cue missing or duplicated: " + marker.Cue);
                foreach (var observation in new[] { local, remote })
                {
                    var entry = observation.GetProperty("rifle").GetProperty("reloadAudio").EnumerateArray()
                        .Single(e => e.GetProperty("cue").GetString()!.StartsWith(marker.Cue, StringComparison.Ordinal));
                    double age = (entry.GetProperty("tick").GetDouble() - entry.GetProperty("startTick").GetInt64()) / 60;
                    if (Math.Abs(age - marker.Seconds - entry.GetProperty("offset").GetDouble()) > .001)
                        throw new InvalidOperationException("Reload playback is not on the animation clock.");
                }
            }
            if (Count(local, "rifle.reload.local") != 0 || Count(remote, "rifle.reload") != 0 ||
                Count(local, "rifle.fire") != 0 || Count(remote, "rifle.fire.local") != 0)
                throw new InvalidOperationException("Wrong local/spatial cue or old monolithic reload played.");
            TestJson.Write(Path.Combine(manifest.Root, "rifle-audio.json"), new { shots, frames, finished, local, remote,
                review = "WAV mixes plus marker captures. Perceptual quality still requires listening; captures occur shortly after cue onset." });
        }
        finally
        {
            await Send(TestAction.AudioRecord, new { stop = true });
            await Send(TestAction.AudioRecord, new { stop = true }, 2);
        }
    }
}
