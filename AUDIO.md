# Spatial audio

The Windows client uses `StargateGame.Audio` and the local
`StargateAudioNative.dll`. The dedicated server never creates an audio device
and does not reference either library. FishGfx is unchanged.

## Controls

- Audio is on by default. Use `--audio off` for a silent comparison.
- Escape opens Master, Effects, Ambience, and UI sliders. M toggles mute while
  this menu is open. Settings persist in `%LOCALAPPDATA%/StargateGame/audio.json`.
  Pausing does not stop the shared world's ambience.
- F3 shows device state, voices, estimated memory, obstruction queries, dropped
  playback requests, reported underruns, and the last client update cost.
- `--audio-cue gate.open` plays a cue in a ten-second diagnostic session.
  `--audio-trace` prints starts on the client thread. `--audio-record path.wav`
  writes the stereo 48 kHz float mix through a bounded native capture ring.

Edit `assets/audio/audio-bank.json` to tune gains, buses, ranges, variants, and
instance limits. Reimporting regenerates this file; retain permanent tuning in
`tools/import_audio.py`. Recognizable gate effects have no pitch randomization.

## Playback

The listener uses the rendered camera: Y is up, forward is negative Z, and
positive X is right. The native stereo test checks this convention. Positional
recordings are mono, ambience beds stereo. Doppler is zero. Inverse-distance
attenuation ends in a smooth cutoff near each cue's maximum range.

The engine has 128 voices. Gameplay admits 112 tracked voices, leaving room for
short stealing fades. Nearby DHD/gate and local movement have priority over
distant impacts. Generation-checked handles prevent an old event from changing
a reused voice. Short recordings decode once; long loops stream through
miniaudio's resource manager. The mixer makes no managed calls and does not do
file I/O or logging. Moving emitters and source low-pass targets are updated on
the client thread; filter coefficients smooth in the native processor.

Local mechanical DHD presses start immediately. Accepted state revisions supply
electronic glyph and chevron sounds, including rapid consecutive presses.
Duplicate acknowledgements are silent. Gate phase timestamps schedule incoming
locks, opening and closing; late joiners skip historical locks and seek into an
opening recording still in progress. Powered and membrane loops fade across
phases. Idle loops stop. Recordings may retain their authored release tails.
Failed activation produces one abort cue and keeps the existing symbol reset.

Movement sounds use forward presentation, never reconciliation replay. Footstep
distance subtracts supporting-body motion. Material gains and filters distinguish
snow, sand, grass, hard surfaces and shallow water. Spawn, large corrections and
epoch resets seed history without a movement event. Membrane entry uses the
existing swept contact detector; sleeping footprints do not repeat contact audio.

Protocol **7** adds bounded, location/epoch-scoped cosmetic sound messages for
confirmed building edits, doors and prop contacts. These messages contain cue
kinds, identity, tick, body, position and strength, not asset paths. Contact
observation leaves physics responses untouched. Impact pairs have cooldowns;
scraping refreshes a quiet loop that fades when contact updates stop. A separate
32-message cosmetic send queue drops excess sound messages without closing a
connection. Clients validate, deduplicate and age out received events.

## Environment

Three rays per source at 10 Hz test the 16 highest-priority positional voices.
An immutable CPU triangle BVH covers terrain, buildings, closed doors and rocks;
rendered prop boxes supplement it. Trees and the membrane do not obstruct sound.
Full obstruction approaches -10 dB and a 1.5 kHz low-pass. Source bodies are
excluded. No GPU shelter readback is used.

Planet wind, daytime birds and icy snow beds follow the same weather/lighting
state as rendering. Shelter reduces exterior beds; camera submersion brings in
underwater ambience and filtering. A bounded positional water loop follows nearby
canonical river/lake data. Relocation and reconnect fade emitters and clear
contact histories. Device loss retries the existing device, then recreates the
engine if necessary; gameplay can continue silently.

## Assets and build

`python tools/import_audio.py` extracts selected local Stargate Network SoundWave
assets and imports general effects from RaylibGame. Source roots can be supplied
with `--source` and `--raylib`. It requires ffmpeg/ffprobe and the existing UModel
extraction tooling. Runtime builds need neither source checkout.

The bank contains 46 recordings and 27 cues. Short effects use PCM16 WAV; loops
use FLAC. Offline linear overlap crossfades condition loop seams (40 ms for the
temporary scrape source, 150 ms for other loops). Provenance records source and
output hashes, decoded PCM hashes, duration, channels, conversion and loop points.
Original files are untouched. UModel exposes SoundCue node names, including the
seven-way glyph random node, but cannot deserialize all cue properties. Gains and
loop selection are explicit game tuning, not purported recovered cue parameters.

Managed/native code was adapted from RaylibGame revision
`a183d45726f60d50e1a7d2a4803acf4ad82474a6`. Miniaudio stays pinned to
`9634bedb5b5a2ca38c1ee7108a9358a4e233f14d`. Code license copies are under
`vendor/licenses` and `vendor/miniaudio`. Recording rights remain separate.

Release builds invoke `scripts/Build-NativeAudio.ps1` incrementally. Use
`scripts/Publish.ps1 -Client` to refresh Windows packages and
`scripts/Publish.ps1 -ServerRuntime linux-x64` for the audio-free Linux server.

## Checks and limits

Run `dotnet test tests/StargateGame.Audio.Tests -c Release`, then
`dotnet run --project tests/StargateGame.Tests -c Release -- --audio-only`.
`scripts/Test-Audio.ps1` records successful or failed dialing; `-Failed` selects the failure recipe; use typed `audio-record` actions for custom recordings. `scripts/Test-AudioEndpoints.ps1` checks both rendered endpoints
and records their separate mixes. `scripts/Record-Audio.ps1` produces an optional
moving audiovisual review clip, excluded from benchmarks. See `VALIDATION.md`
for measurements.

Music, voice chat, HRTF, room reverb and teleportation cues are absent. Scraping
currently uses a quiet conditioned general-gameplay sample, not a dedicated
crate material recording. Material footsteps share recordings with gain/filter
variation. Reported underruns cover the wrapper's tracked stream events, not
every possible hardware glitch; memory is an estimate. Automated mix checks do
not establish subjective loudspeaker/headphone balance or hardware hot-unplug
recovery. Those listening/device checks remain manual acceptance work.

## Supporting-surface footsteps (2026-11-07)

`python tools/import_steps.py` imports the eight supplied recordings as five cue
sets: `step.wood` (3), `step.leaves` (2), `step.stone` (1), `step.mud` (1), and
`step.gravel` (1). The full audio importer invokes this step too. Effects are
48 kHz mono PCM16, with conservative silence trim, a short onset fade and bounded
RMS normalization. Detailed conversions and source hashes are in provenance.json.
No license was supplied in the steps folder; their rights are not inferred from
miniaudio or RaylibGame code licensing.

Protocol 8 carries GroundSurface in player snapshots. Both server physics and
prediction read actual ground contact: dynamic props provide their material;
streamed rock/trunk triangles carry stone/wood tags; terrain uses canonical
material/moisture; other current building surfaces use stone. Shallow water uses
existing water footsteps. Snow, sand and metal retain the generic filtered set.
Cadence uses travel relative to the support velocity. Variant selection avoids
immediate repetition for multi-recording footstep cues. Existing correction,
spawn, relocation and rollback suppression stays in effect.

Diagnostic playback: `StargateGame.Client --audio-cue step.wood`; substitute any
of the five cue IDs. Mixing remains on the Effects bus with existing range and
obstruction. Numerical normalization and device playback do not replace final
subjective listening/balance feedback.
