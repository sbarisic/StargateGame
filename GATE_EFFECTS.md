# Gate effects

The current implementation uses a four-second opening and a 245-tick
(4.083-second) closing. The longer closing supersedes the original one-second
proposal: it follows the supplied shutdown sound's 4.08-second duration.
Protocol compatibility is 15. Message layouts and world saves are unchanged.

`GateEffectTimeline` is shared by the server and client. Presentation evaluates
the authoritative phase timestamp and caches the result for geometry, materials,
spray, lights and loop gains. The receiver keeps its 42-tick lead-in and settles
at source opening +282 ticks. Existing pending-transfer and readiness rules remain.

## Appearance

Opening forms the membrane from 0.3–1.1 seconds, extends from 1.3–2 seconds,
holds through 2.2, accelerates back into the horizon by 3.3 and settles by 3.5. Capture remains disabled
until four seconds. The continuous mesh has a rounded leading volume, a trailing
column and a short collar at the aperture. Large rolling bulges affect its
silhouette. Retraction curls the head and narrows the column independently.
Nominal reach is 1.2 aperture diameters (5.52 m at unit scale); culling bounds include folds and spray.
The rear sheet remains flat.

Closing turns white in 150 ms and holds the flash briefly, then removes the
sheet with a restrained ease-out curve (power 1.65). The surface finishes at
2 seconds; the light and shutdown sound retain their natural 4.083-second tail.
This replaces the earlier long, crawling breakup. The sheet stays white through breakup. Fixed gate-local noise
thresholds produce outward-growing holes; removed fragments cannot reappear.
The same visibility is used for colour, depth and bloom. Chevrons fade over the
last 35 percent; hum fades during the first 0.25 seconds. Both existing one-shot
cues retain their natural speed and delayed-event seeking.

Spray uses 32/64/96 pooled GPU-animated quads, seeded by gate and connection.
Half are biased towards the upper head/folds. Radial feathering softens the
sprites; premultiplied blending, depth tests and soft intersections avoid hard
quad edges. No extra shadow casters or full-screen effect is added.

Settled player and prop contacts use persistent contours and the existing
baseline-coupled GPU solver, with continuous contour forcing even on sleeping
props. Ripple animation runs five times faster than the previous setting (7.0
instead of 1.4). Boundary response is 2.5 and the field bends ripple coordinates
by at most 12 cm using a smooth bound;
it does not add another visible wave-normal layer, glow or displaced geometry.
Cubic field reconstruction reduces blocky highlight derivatives at grid-cell
boundaries. Warp derivatives give matching shading normals. Contact resources reset on
closing, location/physics epoch and incompatible resolution changes. Player and
prop IDs remain separate. Simulation resolutions remain 256/384/512.

## Source artwork

`assets/gate-effects/provenance.json` records source paths and PNG SHA-256 hashes.
UE Viewer exports retain their original channels, including smoke alpha. The
8×8 atlas is sampled as a static density cell, not an assumed playback sequence.
See `THIRD-PARTY-NOTICES.md` for artwork attribution and rights boundaries.

Reproduce the copy from verified UE Viewer exports:

```powershell
python tools/import_gate_effects.py 'D:/Games/Stargate Network/extraction/gate-effects-textures'
```

## Development checks

Build into an isolated directory when another agent or server uses the checkout:

```powershell
dotnet build src/StargateGame.Client -c Release --artifacts-path artifacts/gate-reference/build
dotnet build src/StargateGame.Server -c Release --artifacts-path artifacts/gate-reference/build
dotnet run --project tests/StargateGame.Tests -c Release --artifacts-path artifacts/gate-reference/full-check-build -- --gate-transition-only
```

The graphics-lab launcher owns its loopback server and stops only its own
processes. It never opens production saves. Example fixed opening inspection:

```powershell
./scripts/Run-GraphicsLab.ps1 -Build artifacts/gate-reference/build -Seconds 15 -View horizon-side -ClientArguments @('--gate-effect','opening','--gate-effect-time','2.1','--visual-time','12','--pause-sky','--audio','off','--benchmark-warmup','10')
```

`--gate-effect-sequence` captures both complete transitions at 0.05-second
intervals. `horizon-front`, `horizon-side`, `horizon-rear` and `horizon-70` cover
front, side, rear and grazing views. `-TimeOfDay 0` selects night.

`--gate-effect open --horizon-players --horizon-props --horizon-wave-check`
exercises the real GPU solver and shading at all three quality resolutions.
Add `--horizon-movie --orbit` for an eight-second moving-camera contact recording.
This is a render fixture, not evidence of network synchronization.

For real audiovisual checks, use a separate normal-world server with a new
save directory and `--dial-check --gate-movie --audio-record <absolute.wav>` on
the client. Movie JPEGs and `gate-live.csv` preserve actual frame timestamps
relative to the audio recording. Encode with variable frame durations; do not
assume capture maintained 30 FPS. Recording/readback is excluded from performance
acceptance. Fixed inspection does not send DHD actions or play transition audio.

## Validation record

### Continuous contacts and prop handoff

The latest contact/closing checks are in `artifacts/gate-contact-transfer`:
48 travel physics/persistence/identity checks, 162 gate/contact checks, and GPU
checks at 256/384/512 passed. The network regression transfers a moving crate
and a barrel stopped at the last millimetre of entry, checks both against actual
destination terrain, then checks player travel and reconnect. A first iteration
correctly left the stationary barrel blocking the later player test; the final
test clears its own arrived props after verifying their landing.

Source collider completion and server capture now share the +2 mm threshold.
Previously an empty proxy could wait for -20 mm, allowing a stopped prop to
fall without terrain collision. Completed proxies now wait without integrating
gravity and retain their entry pose/momentum for snapshots and handoff. The
receiver inserts the original full collider. Physics source compatibility changes;
restart both client and server after rebuilding. Save formats are unchanged.

Item exits now reserve a centre-to-sheet distance of the collider's bounding
radius plus 20 cm, instead of 3 cm clearance for its initial orientation only.
This keeps tipping corners in front of the receiver. A backward normal velocity
is reflected outward; tangential/vertical velocity, speed and spin are retained.
The existing destination collision/occupancy check uses this final placement.
Rotated-gate and tipping regressions are included in the 138-check travel suite;
network arrival evidence is in `artifacts/gate-exit`.

The stationary-contact lab measured 1.169 ms frame p95 and 0.030 ms wave GPU
p95 at 1080p Performance without movie/diagnostic readback. This is a small-scene
result. `stationary-contacts.mp4` uses actual capture timestamps; the closing
clip uses fixed 0.05-second steps and confirms no surface remains at 2 seconds.
Neither clip contains audio.

### Kawoosh mist and bloom

The opening plume injects a moving, noisy mist shell into the existing fog
volume. The shell follows the rounded head, neck and aperture collar, with more
mist above the water and a short lag during retraction. It receives the existing
shadowed sun/gate illumination plus blue-white scattering from the luminous
water. Density rises from 1.15 seconds and disappears by 3.9 seconds. Interrupted
openings fade it out over 0.2 seconds. Its appearance is reconstructed from the
gate timeline; it keeps no particle history or additional render targets.

The opening also adds water highlights to the existing HDR bloom mask and
broadens its blur during the surge. This softens the silhouette without blurring
the water geometry or normal detail. The combined mask includes nearby gate
lights, so their halo broadens during the surge too. Settled gates use the
original bloom strength and blur width.

Both features default on. Use `--kawoosh-mist off` and `--kawoosh-bloom off`
independently for comparisons. `--volumetrics off` keeps the soft spray sprites
and bloom but disables the volumetric mist. Low/Performance/High average
2/3/5 mist samples per depth cell. The mist uses the existing volume resolution,
so distant or very close inspection can expose its spatial filtering; it is
not a fluid simulation. It follows the current gate presentation state, supports
full entity transforms and limits injection to two nearby gate instances.

Matched noon/night peak captures and sequences are under `artifacts/gate-volume`.
`Compare.ps1` and `Validate.ps1` use isolated Release binaries and owned loopback
servers. Peak 1080p Performance frame p95 was 3.080 ms at noon and 1.796 ms at
night with effects enabled. Noon fog GPU p95 rose from 0.212 to 0.498 ms; the
night runs had enough timing variation that they do not establish an incremental
cost. These are small-scene checks, not full-world performance acceptance.
Both 165-frame transition sequences completed. Low quality and volumetrics-off
fallback rendered without runtime errors. A fixed central settled-horizon crop
was pixel-identical with both features enabled/disabled; ground foliage outside
the crop differed with asynchronous streaming. The comparison movie contains
the opening at its 20 FPS capture cadence and has no audio. Results and original
logs are retained in `artifacts/gate-volume/validation.json` and the run folders.

Artifacts and the isolated capture scripts are under `artifacts/gate-reference`.
The tracked transition/contact entry point passed 201 checks. The focused
delayed-network run passed 241 checks, including both four-second openings,
late joins, repeat connections, dialer disconnect and 245-tick teardown.
GPU diagnostics passed player entry shading, untouched-field identity, packet
reflection, blocked transmission and stationary boundaries at 256/384/512.

An earlier broader network run failed the destination-unload grace-period check
after its gate checks passed. It is retained in `gate-reference-network.log`;
this effects work does not claim to fix destination lifetime management.
The earlier furnishing test compile failure was fixed by concurrent work; the
full test project subsequently built and its gate-only entry point passed.

Lab timings do not establish whole-world performance. The egg-profile peak run
measured 1.548 ms frame p95 and 0.008 ms spray GPU p95 at 1080p Performance,
with a ten-second warm-up. See individual logs for later shape refinements.
Contact diagnostics and image sequences include readback stalls. A complete
matched pre-change contact performance comparison and the entire live
30/60/120-FPS interaction matrix remain separate acceptance work.


## Wake persistence and viewmodel occlusion (2026-11-09)

Contact corrections and routine physics epochs quietly reseed tracking without
clearing the GPU field. Departed obstacle masks are removed immediately; their
contour contribution relaxes into the wave field. Residual damping is 1.2 per
second, with full simulation for three seconds and a smooth fade during the
last second. New contacts extend activity. Closing, connection/world/transform
replacement, incompatible resolution, and disabling contact effects clear the
field. Untouched horizon shading and the opening/closing timeline are unchanged.

The viewmodel keeps its separate projection and depth target. Its opaque alpha
coverage masks world emission before the first bloom blur. Open sights retain
visible light; hidden chevron shapes cannot project across the weapon. World
fog, mist, particles and water composite before the viewmodel, followed by bloom
and tone mapping. Viewmodel shading initializes its material state and does not
resample the world fog volume. Skinning/model normal transforms use inverse
transpose; original smooth skin normals and rifle hard edges are retained.

`--horizon-wave-check` additionally tests CPU/GLSL plume positions, pre-blur
coverage including an open sight, and residual wake lifetime at all three
simulation resolutions. These readback diagnostics deliberately stall the GPU;
their frame-time maxima are not gameplay performance measurements.
