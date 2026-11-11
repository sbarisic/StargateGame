> Testing entry points now use the shared harness. See [TESTING.md](TESTING.md) for current arguments, recipes, readiness, and replacement commands. Render-only previews remain separate from real gameplay.

# Outdoor renderer

## Rifle finish, viewmodel shadows and cloud layers (2026-11-11)

Weapon and arms render into a 4x MSAA RGBA16Float/Depth32Float target at
scene resolution across all quality presets. After the draw pass, color resolves
into a single-sample RGBA16Float target without depth. Premultiplied-alpha
composition preserves resolved edge coverage, also used by horizon glow masking.
Hardware reporting fewer than four samples uses the single-sample color/depth
path and logs the fallback once. Both targets resize with the scene and are
included in target memory reporting (56 bytes/pixel for MSAA plus resolve,
12 bytes/pixel for the fallback). World rendering and final FXAA are unchanged.
MSAA implementation has not been built or tested; validation is left to the developer.

The procedural rifle material uses matte coating roughness (0.62–0.94), low
coating metallic response, and restrained sky reflections. Wear and brass retain
separate highlights. Arms retain their surface texture variation with a 0.74
roughness floor and reduced specular strength.

The viewmodel uses a 2048-square depth map with bilinear visibility comparisons
and a weighted nine-tap filter. Each depth comparison uses receiver-plane
correction. The existing six-metre coverage is retained to avoid clipping poses;
the larger depth map costs 12 MiB more than the previous 1024-square map.

Cloud density uses wind-aligned regional base-height and thickness variation,
with a softer lower profile. Local layers stay inside the global march bounds.
Cloud lighting and ground shadows use the same layer definition.

These changes have not been built, rendered, tested, or performance-validated;
validation and playtesting are left to the developer as requested.

## Authoritative gate presentation (2026-11-07)

Normal play follows replicated gate state. `GateEffectTimeline` supplies one shared
phase evaluation for mesh shape, material, spray, light and audio. Opening lasts
240 ticks (four seconds); the receiver retains its 42-tick chevron lead-in.
The connected, rounded plume reaches 1.2 aperture diameters (5.52 m at unit scale).
Its rounded head has rolling bulges and a wider trailing column, which narrows during retraction; it settles by 3.5 seconds. Closing lasts 245
ticks (4.083 seconds): a white flash gives way to fixed-noise holes spreading outwards, followed
by perimeter remnants. Colour, depth and bloom share the same removal mask.

Settled contact deforms the existing ripple coordinates by at most 3 cm, using
player-capsule and prop contours in gate-local space. It adds no glow, displaced
geometry or separate ring layer. Quiet initialization and three-second decay
remain in the existing GPU solver. Effects do not change collision or travel rules.
See [GATE_EFFECTS.md](GATE_EFFECTS.md) for timing, assets and validation commands.

Use `--inspect-active-gate` for the previous always-active shader diagnostics.
`--event-horizon off` still disables gate effects. `--dial-check` exercises actual
network dialing during smoke/benchmark runs and captures every visual phase.
See [DHD_DIALING.md](DHD_DIALING.md) for timing, controls, and developer inspection.

### SG-1 transition references and inspection

The visual references are the practical water-tank effect discussed by
[John Gajdecki](https://www.thecompanion.app/john-gajdecki-stargate-vfx/), including
the SG-1 opening footage embedded in that interview, and the shutdown in
["Prisoners", around 1:43](https://www.youtube.com/watch?v=FnIGstp0wE8&t=103s).
The latter shows a white surface flash followed by broken bright remnants.
The TV design has no rear whirlpool. A flat, depth-writing rear sheet hides the
inside of the forward surge from rear observers during opening.

`gate_transition.glsl` supplies deterministic deformed positions to colour and
selective bloom passes. Geometric tangents follow the rolled silhouette; filtered
liquid detail supplies smaller silver folds and froth. The settled membrane keeps
its existing material, ripple scale, animation speed and contact simulation.
Closing clips the sheet in fixed membrane coordinates instead of scaling it down.
The short flash also modulates the existing blue spill light; it does not change
camera exposure. Chevrons and the DHD still fade smoothly at shutdown.

All transition ages come from replicated server phase timestamps. Late observers
sample the current effect; rendering does not restart its clock. The geometry,
shader and reference interpretation are authored here, not copied footage or a
fluid simulation. Reference recordings are not runtime or packaged assets.

Repeatable render-only inspection (combine with `--benchmark-seconds 8`):

- `--gate-effect opening|closing|open|idle --gate-effect-time SECONDS` fixes a phase.
- `--gate-effect-sequence` captures 51 completed frames in 0.05-second increments,
  covering opening, settled surface and closing after eight seconds of warm-up.
- `--view horizon-front|horizon-45|horizon-70|horizon-rear|horizon-close` sets the view.
- `--time-of-day 12 --pause-sky --audio off --capture-dir PATH` fixes the surrounding
  scene for comparison. These synthetic phases do not send commands or audio events.

Use `--dial-check` without a render override to check real authoritative transitions.
Do not use sequence capture runs for performance figures: PNG readback/write stalls
are intentionally included in those frames.


The Windows client defaults to a 1920 x 1080 window, Performance graphics, and
VSync on. Use `G` in the Escape menu to cycle Performance, Low, and High.
Command-line settings are `--graphics performance|low|high` and `--vsync on|off`.
Settings apply to the current client session and do not change simulation or terrain.

| Preset | Static shadow maps | Dynamic maps | Distance | Filter |
|---|---|---|---|---|
| Performance | 3 x 1536 | 3 x 768 | 192 m | 3x3 PCF footprint |
| Low | 2 x 1024 | 2 x 512 | 96 m | 3x3 PCF footprint |
| High | 3 x 2048 | 3 x 1024 | 320 m | 5x5 PCF footprint |

FishGfx implements PCF using bilinear comparison samples: four taps for the
smaller footprint and nine for the larger one. The game retains its pinned
FishGfx checkout. The receiver shader is adapted from FishGfx's voxel shader,
with triangle-plane depth correction and a distance fade for this terrain.
Cascade texture accesses use constant sampler indices: per-fragment sampler-array
indexing is not dynamically uniform and has undefined results under the
[GLSL 4.00 specification](https://registry.khronos.org/OpenGL/specs/gl/GLSLangSpec.4.00.pdf).
This mattered on the test GPU: non-uniform indexing produced false terrain shadows
near cascade boundaries. The game shader fixes this without modifying FishGfx.

## Frame pipeline

1. Take one snapshot of rendered players and crates. Evaluate the server-anchored
   planetary clock and winter weather once for the frame.
2. Sample the atmospheric lookup tables, update the sky, cloud raymarch/history,
   cloud shadows, sunlight cascades, coarse terrain shadows, and volumetric fog.
3. Draw opaque geometry and the event horizon into unfogged RGBA16F colour and
   Depth32F depth. The sky receives atmospheric transport at its background depth.
4. Composite aerial perspective into a separate target. Draw water while sampling
   the original unfogged opaque colour/depth. Water applies underwater absorption
   and camera-to-surface atmospheric transport separately.
5. Draw snowfall, then extract and blur the depth-tested selective emission mask.
6. Tone-map, apply display conversion once, run FXAA, and draw sharp UI. F12
   captures the completed frame.

Render targets are reused, resized with the framebuffer, and skipped for zero-sized
windows. Per-world cloud/fog/star resources are released on world changes and
reconnects. The three atmosphere-profile LUT sets remain cached until renderer
shutdown. Quality changes recreate cloud/fog targets as well as shadow resources.
FishGfx remains pinned. Rendering does not add server graphics dependencies.
Exposure is fixed; a cool night ambient floor keeps navigation possible.

## Diagnostics and repeatable checks

F3 shows quality, updated shadow maps, scene draw calls, approximate render-target
memory, CPU scene submission time, and asynchronous GPU pass time. Target memory
includes static and half-resolution dynamic depth maps, with a conservative four
bytes per depth pixel. It excludes meshes, material textures, and driver overhead.
GPU timing sums completed scene/shadow/post-process queries without blocking;
it excludes attachment copies, UI, and presentation. CPU scene timing excludes
streaming uploads, UI, and final presentation. Full frame timings include those.

```powershell
./scripts/Test-Graphics.ps1 -Seconds 15 -Settings @{orbit=$true}
./scripts/Test-Graphics.ps1 -Presets temperate -Views gate,dhd -Graphics high
./scripts/Run-Client.ps1 -Server 127.0.0.1 -Graphics performance -VSync off
```

The graphics script builds Release, runs its own loopback server, launches clients
sequentially, and saves screenshots and logs under `artifacts/graphics-checks`.
It uses ten seconds of warm-up, saves screenshots at eight seconds, then measures
the requested interval at 1080p with VSync off. Close other game instances before
building. The default checks cover gate, river, and cliff views in each preset.
Desert's river view falls back to the gate site if there is no shallow-water sample.

Developer-only client arguments:

- `--benchmark-seconds N`: warm up, capture, measure, and exit without smoke resizing.
- `--view player|gate|dhd|river|cliff|wide|rocks|trees` and `--orbit`: repeatable render-only
  inspection cameras during smoke/benchmark runs; authoritative players do not teleport.
- `--no-shadows`: isolate lighting and shadow artifacts.
- `--smoke-graphics`: cycle all quality levels during a normal smoke run of at least 12 seconds.
- `--smoke-reconnects N`: disconnect/reconnect N times at the end of each smoke interval.

Use the ordinary `--smoke-seconds` run for pause/resume and resize checks. Use
`--smoke-planets` with a server started with `--dev-planets` for repeated switches.
Performance benchmarks must run separately from these lifecycle checks and the
headless physics suite. Initial shader compilation and terrain loading are reported
by the all-frame smoke metrics, not by warmed-up benchmark metrics.

Normal maps are imported by `tools/import_terrain_textures.py`; provenance and
source hashes are recorded beside the assets. No original installation is needed
to run or publish the game.

Tree leaves/needles use atlas alpha cutouts in both world and shadow shaders.
The client-owned `uFoliageCutoff` uniform avoids FishGfx's automatic `uAlphaCutoff`
override. Tree manifests opt into repeating UVs for bark and atlases; existing
gate/DHD/rock texture sampling is retained. UI is still rendered after post-processing.

## Building kit

The building renderer batches shared procedural geometry by owner chunk, uses
extracted construction-plaster albedo/normal maps at a 2 m scale, and draws
buildings in static shadow passes. Placement previews use a separate translucent
shader; palette text remains in the final UI pass. `--view buildings` provides a
repeatable inspection camera for populated saves. Performance fixtures are listed
in BUILDING.md and VALIDATION.md.

## Event horizon

The gate now displays a visual-only liquid membrane by default. Use
`--event-horizon off` for the inactive comparison (membrane, chevron emission,
bloom and local lights all off). There is no dialing, teleportation, kawoosh,
physical membrane barrier or DHD activation.

The flat 192-segment disc sits in model XY at (0, 3.063692, 0), radius 2.30 m,
then follows the gate entity's scale and translation. Terrain and the ring
occlude it; it writes depth and hides opaque geometry behind it from either side.

Three independently moving, rotated spatial-gradient-noise fields provide
non-repeating surface slopes. Their similar physical scales avoid stretched
patches and regular sine-wave interference. Only a small coordinate warp remains.
The normals reflect an art-directed gate-local blue/white environment; a radial
envelope fades reflective detail towards the ring without fading opacity or
changing ripple wavelengths. Defaults and appearance controls live in
`EventHorizonSettings` in the client. `EdgeFade` is a normalized radius range.

Contacts use local predicted and interpolated remote transforms. Player capsule
entries inject their extended contour once, with hysteresis and correction/spawn
suppression. Props retain oriented-box/plane polygons clipped to the aperture.
Sleeping boxes remain reflecting obstacles. Moving masks use the rendered
previous/current transforms; fast crossings retain sampled swept contours.
Overlapping masks form a union. There are no periodic random-point pulses.
The bounded entry queue holds 16 contour events; old residuals decay for three
seconds. Location/epoch changes and reconnects clear all contact and GPU history.

The GPU solver stores residual height/velocity in alternating RG32F targets.
The baseline height and analytic gradient share `horizon_baseline.glsl`. At an
obstacle, ghost samples enforce zero normal derivative of total height. Its
animated baseline drives continuous reflections even with a stationary crate.
One combined normal feeds the existing white reflection, emission and fog;
there is no contact-colour overlay or separate luminous outline.

A half-resolution, scene-depth-tested gate mask contains procedural horizon
emission, masked chevron emission and active DHD emission.
Two separable blur passes feed the existing HDR tone mapper. Other scene content
and UI are not bloom sources. Two RGBA16F targets cost about 7.9 MiB at 1080p;
they are disposed on resize, planet clearing, disabling or leaving the view.
F3 reports obstacle count, simulation resolution/substeps/damping/response/GPU time,
emission draws, target memory, local lights and
shadow-face updates. F12 captures the
completed frame with UI.

`scripts/Test-Horizon.ps1` provides front, close, oblique and rear capture sweeps
across all three planet presets using isolated saves. Use `-Horizon off` for
a baseline, `-Stress` for a controlled wave packet and rectangular obstacle, and `-Occlusion` for
render-only foreground/background player and crate fixtures. The extra
`horizon-cross` view moves the camera through the surface. Client equivalents
are `--view horizon-front|horizon-close|horizon-oblique|horizon-rear|horizon-cross`,
`--horizon-stress` and `--horizon-occlusion`, with the existing benchmark/smoke
options. Diagnostic fixtures never alter authoritative transforms.

Tests executable options: `--horizon-only` for contact/lifetime tests and
`--horizon-network` for two real client sessions through 100 ms RTT / 20 ms jitter.


## Gate lighting and grazing highlights

The reflected horizon bands blend toward a slope-driven lobe at grazing views.
`EventHorizonSettings.GrazingCompensation` defaults to 0.85; emission intensity is
0.35. The random metre-scale gradients and radial rim fade stay unchanged.
Emission is separate from direct reflected brightness and uses the existing HDR
conversion. Chevron crystal/strip masks emit linear orange (1, 0.20, 0.025) at
intensity 3.5. Ordinary metal and the two inactive chevrons do not emit.

`GateLightSettings` controls seven orange point lights (range 3 m, intensity 1.6,
source radius 0.22 m) and one blue point light at the membrane centre (range 7 m,
intensity 3, radius 1.2 m). The blue source approximates a broad luminous disc; it
is not area lighting. Both kinds illuminate nearby opaque surfaces and water.

Each light uses six guarded shadow views, with separate static/dynamic tiles in
one depth atlas. Low/Performance/High use 128/256/512 interior pixels per face,
plus two guard pixels on each side, and 3x3 PCF. Atlas allocation is approximately
6.4/24.8/97.5 MiB. Static tiles refresh for affected terrain/LOD/tree changes and
building revisions. Dynamic tiles refresh when actors entering, leaving or moving
within a face change its caster set, including the local player proxy. Shadow
casters use the same visible transforms as the colour pass. The membrane does
not cast shadows. Targets are released outside the light-influence view or when
the gate is disabled, and rebuilt on planet/quality changes.

`--gate-lighting off` disables only spill/shadows for paired performance checks;
emission and bloom remain visible. `--visual-time 6` freezes surface animation in
benchmark/smoke captures. Additional views: `horizon-45`, `horizon-70`, and
`horizon-shadow`. Test-Horizon.ps1 accepts `-GateLighting off`, `-VisualTime 6`,
`-Graphics low|performance|high`, and `-Props` for one resting and three moving
render-only crates. `-ShadowFixture -FixtureDoor open|closed` adds an isolated
render-only doorway; `-DarkDiagnostic` dims daylight to reveal light leakage.
These diagnostics never change saves, network state or physical transforms.

## DHD glyph appearance and selection

DHD keys use subdued warm metal markings when inactive. Source glyph material
assignments and the source glyph mask identify the markings; albedo brightness
does not decide which surfaces emit. Selected keys illuminate their glyph and
raised upper rim in amber. The central orb uses its recovered emission texture
for red lowlights, pale pink hot patches and dark mottled areas, with depth-aware
bloom. Static model-space mottling is baked into the linear RGB emission map;
it stays attached to the surface and does not flicker or change with the camera.

`SceneRenderer.DhdGlyphs.SetGlyph(dhdEntityId, sourceKeyId, true|false)` controls
one key. `SetGlyphs(id, keys)` replaces the complete selection atomically;
`SetOrb(id, true|false)` controls the orb independently. `Clear(id)` clears one
DHD; world unload/reconnect clears all selections and orb overrides, restoring
the configured prototype default.
IDs are the `SM_Key_NN` numbers, **not** mesh indices or dial sequence positions.
The standard 38-key layout hides overlapping key 13 and retains key 01, as
specified in DHD_GLYPH_MAPPING.md. Invalid/hidden IDs are rejected.

DHD captures now use actual replicated dialing state. Use a Sandbox/Travel
recipe with `teleport` to the DHD station, typed `dial` presses, and a `dhd-close`
camera/capture step. Retired `--dhd-lit` and `--dhd-orb` selectors are no longer
launch options. See TESTING.md for the request schema.
Normal play follows the replicated selection described above. Selected keys use
a 1.75x gain in the selective bloom mask, leaving their sharp emission, the orb,
and Stargate bloom unchanged. They also cast a small amber wash onto the same
DHD's upper metal surfaces. Sources use area-weighted upper-face geometry and
follow key presses; selection changes, undo, and reset take effect on the next draw.

`SceneRenderer.DhdLights` exposes `DhdLightingSettings`: `GlyphBloomGain` (1.75),
`SpillStrength` (0.02), and `SpillRange` (0.3 model-space metres). The soft source
radius is 0.06 m; both range and radius scale with the console. Set spill strength
to zero and bloom gain to one for the previous appearance. The local approximation
preserves surface albedo and roughness, excludes the orb and underside, and adds
no shadow maps or light to terrain, actors, or other DHDs. Inactive markings do not
emit, but their metal can reflect adjacent keys. Visual tuning is pending an
in-game check; this change was built without launching the project.
Each key's `emissionColor` and `emissionIntensity` are editable in `dhd.json`.
The reproducible defaults are in `tools/export_dhd_materials.py`. Original
atlases are retained as `sourceTexture`; regenerated weathered albedo and
emission masks use separate files. No geometry or runtime UVs are changed.


Desert daylight uses denser sandy distance/height fog: `Daylight.Fog` is
(base density, ground-layer density, height falloff), with desert defaults
`(0.0017, 0.0024, 0.010)`. Temperate/icy retain `(0.00048, 0.00055, 0.012)`.
At clearing height this gives about 50% fog by 169 m, while nearby scenery stays
readable. All surface and emissive passes share the same transmission function.
The DHD marking albedo is slightly darker for inactive-glyph readability;
its emission masks and lit intensity remain independent of that colour.


The main membrane animation speed remains **1.40**. Object-wave propagation is
**1.35 m/s**, 25% slower than the original solver default. A 0.05 m derivative
footprint filters small contact bumps without filtering the procedural baseline.
`RimBrightness=0.55` lifts only the outer blue falloff (previously 0.30); centre
shading, white-highlight fade and opacity remain unchanged.

Client `EventHorizonSettings` exposes `WaveSpeed`, `Damping` (2.4),
`BoundaryResponse` (1), `DisturbanceSmoothing` (0.05 m), `RimBrightness`, and optional
`SimulationResolution` override. Defaults are 256/384/512 for Low/Performance/High.
The fixed 60 Hz update subdivides to keep c*dt/dx <= 0.65 (2/3/4 substeps at the
unscaled gate with current speed). Baseline endpoint textures interpolate through
each tick. A 0.35 m absorbing rim damps residual ringing. No-contact state remains
exactly zero; base animation always follows elapsed time, including during contact.

Targets allocate on first contact, are reused while idle, and are released on
horizon disable, quality/location/epoch changes, reconnect and renderer disposal.
Framebuffer resize does not resize the gate-local simulation. A stall over 250 ms
resets its accumulator and ramps boundary forcing back over 0.25 seconds.
Per-gate target memory is 2.5/5.625/10 MiB, separate from selective bloom.
Opaque and bloom read the same completed field; updates happen only once per frame.

`--horizon-waves off` disables only collision waves for matched performance
comparisons; `--event-horizon off` still disables the complete active-gate effect.
`Test-Horizon.ps1 -Waves off` exposes the former option. `-Props -PropMode all`
keeps all four rendered boxes touching while rotating three. `-PropMode stationary`
keeps all sleeping; default `motion` includes repeated crossings.
`-WaveCheck` / `--horizon-wave-check` runs actual GPU readback assertions at all
three grids: no-obstacle baseline, reflected packet, blocked transmission and
continuous stationary boundary. The diagnostic runs before warm-up timing.

`Test-Horizon.ps1 -Props -Motion` exercises moving/resting prop contacts and
captures six live frames 0.25 seconds apart. Omit `-Props` to inspect base motion.
`-VisualTime` deliberately freezes animation for still comparisons; those test
windows now display `FIXED-TIME INSPECTION - animation frozen`. Normal play
uses elapsed time and does not freeze the membrane.

`./scripts/Test-HorizonLifecycle.ps1` uses isolated saves and an owned test window
to exercise resize, minimize/restore, quality changes, planet switches and
reconnect. It asserts cleared horizon allocation after disconnect. It does not
modify or minimize other application windows.

## Ground cover and winter weather

Temperate planets now render wind-animated grass tufts. Icy planets use rounded
snow detail, continuous snowfall, overcast lighting and seed-driven gusts. These
are client visuals: canonical terrain, physics, world saves and protocol stay
unchanged. FishGfx remains at the existing pinned revision.

| Quality | Grass range | Near tufts/m² | Flake budget | Shelter map |
|---|---:|---:|---:|---:|
| Low | 32 m | 6 | 1,500 | 256² |
| Performance (default) | 48 m | 12 | 4,000 | 512² |
| High | 72 m | 18 | 8,000 | 1024² |

Use `--grass on|off` and `--weather auto|off|light-snow|storm`. Defaults are `on`
and `auto`. Weather overrides affect icy planets only. `off` restores their old
atmosphere and removes flakes; the improved snow material remains. F3 reports
grass tiles, rendered triangles, preparation jobs, flakes, gust strength, shelter
refreshes, GPU buffer/target memory and separate grass/weather GPU timings.

`EnvironmentState.cs` contains the client settings, immutable per-frame weather
and pure placement code. Grass uses a location/seed/world-cell hash, canonical
triangle heights and material/slope/moisture data. Each 8 m tile owns its tufts;
quality and distance select stable ranked subsets. Four tapered blades per tuft
use 0.18–0.45 m heights. Background jobs prepare tiles, and uploads share the
terrain's 2 ms allowance. The budget is checked between whole mesh uploads, so a
single upload can exceed the remaining allowance. Tile replacements retain fade
age; build/scenery edits invalidate affected tiles. Terrain mesh LOD changes do
not reroll grass. Water cells, rock slopes, source-model tree bases, rocks,
low building geometry, spawn points, DHD and the gate approach are excluded.
Grass density falls in the gate clearing. Wind is the only bending input.

`grass.vert/frag` provide anchored wind, smooth density/range fades, darker roots,
two-sided diffuse/backlighting, sun shadows and gate lighting. Blades cast no
shadows. Underlying grass uses broad irregular colour and dry soil transitions.
`snow_surface.glsl` is shared by terrain and snow-covered rocks: bounded 3 cm
height detail, rounded normals, filtered grain, roughness variation and restrained
glints. Near upward-facing terrain has shallow parallax UV relief, which fades
with distance and grazing angle. It does not alter silhouettes or collision.

An immutable `WeatherFrame` is sampled once from planet seed and the existing
server tick clock. Local extrapolation smooths snapshot cadence. Automatic gusts
combine 31- and 47-second cycles; snow never stops. Sunlight ranges from 35% of
old icy sunlight in light snow to 15% in a strong gust, with 80–65% ambient light.
Exposure and gate/DHD emission are unchanged. At clearing height, fog reaches
50% transmission at 115 m in light snow and 65 m in a storm. The same frame feeds
surface lighting, sky, water, flakes and emission fog. Other planet atmospheres
retain their settings.

Snowflakes use a world-space 48×32×48 m periodic volume, with continuous integrated
wind travel, varied fall speeds, turbulence and fades before volume wrapping.
A small subset forms low blowing snow in gusts. A snapped 88 m overhead depth map
includes terrain, water, buildings, gate/DHD, rocks and alpha-tested tree canopies.
It is cached until movement, quality or static geometry changes. Snow below its
highest surface is suppressed; it is an overhead shelter approximation, not a
wind/roof collision simulation. Coarse maps can soften shelter edges, and drifting
snow does not enter rooms through open doors. Flakes render after water into HDR,
with depth testing, soft depth intersections and no depth writes or bloom.

Resources are reused and released on location changes, reconnect and disposal.
Resize changes scene targets but does not reallocate the quality-sized shelter
map. Minimized/zero-sized frames are skipped. No additional maps were imported;
existing grass, cliff and snow albedo/normal maps retain their existing provenance.
There are no footprints, accumulating drifts, grass collisions, player bending,
frozen water or new snow meshes on buildings.

For captures, `scripts/Test-Environment.ps1` accepts `-Views grass,snow,gate,river,
cliff,trees,rocks,weather-shelter`, `-Grass`, `-Weather`, and `-Graphics`. It uses
isolated saves, 1080p, VSync off and a ten-second warm-up. `-WeatherFixture
-WeatherCheck` checks actual GPU shelter classification inside/above/below a test
room; `-RoofOff` removes its roof, and `-RoofCycle` removes it during the run to
check invalidation. Fixtures only alter the rendered scene. `-VisualTime` freezes
weather and wave motion for repeatable stills. `scripts/Test-EnvironmentLifecycle.ps1`
checks resizing, minimize/restore, quality changes, planet switching and reconnect.
Headless placement/weather checks run with `--environment-only`; actual direct and
latency-proxied client weather checks run with `--environment-network`.

`Test-Environment.ps1 -Motion` saves eight live frames; `-Settings @{traverse=$true}` moves only the
inspection camera rapidly across a 60 m path to check streaming and shelter
recentering. Clouds and flakes use the same integrated gust-driven wind, avoiding
phase jumps as wind speed changes. Flake density selection is independent of
spawn height. A fragment-level shelter check also softens ground/water contacts.
CPU counters separate main-thread grass preparation/uploads and weather passes;
GPU counters separate grass rendering and shelter/snowfall passes. See
[VALIDATION.md](VALIDATION.md#grass-snow-and-winter-weather--2026-11-06) for measured
results and their limits.

## Planetary atmosphere and connected water — 2026-11-07

Generator 3 requires fresh worlds. There is no generator-2 fallback. Protocol 5
carries persisted environment definitions, server UTC anchors, canonical water
regions, continuous swim inputs, and authoritative swimming state.

The atmosphere adapts Eric Bruneton's RGB Rayleigh/Mie/ozone model with four
scattering orders. The original BSD-3-Clause sources and license are retained in
`assets/shaders/bruneton`; `provenance.json` identifies the exact upstream commit
and hashes. Fragment passes precompute transmittance (256×64), irradiance (64×16),
and packed scattering (128×1024; R16/MU64/MUS16/NU8) tables. Cache keys include
shader contents, dimensions, orders and aerosol profile. Moving the sun samples
these tables rather than rebuilding them. The first encounter with each atmosphere
profile has a synchronous precomputation stall; subsequent visits reuse its LUTs.

Sunlight uses atmospheric transmittance. Cached sunlight shadows invalidate at
approximately half a projected texel of angular movement, in addition to normal
streaming and actor invalidation. A 1024² whole-world terrain depth map covers low
sun angles beyond detailed cascades. Dynamic actors use the detailed cascades.

Clouds occupy a world-space 800–2,400 m layer. Seeded shape/erosion noise,
sunlight extinction, approximate multiple scattering, and continuously integrated
wind drive the layer. The raymarch uses an equirectangular reduced-resolution
view, first-hit depth reprojection, 3×3 neighbourhood clamping, and depth rejection.
History resets after large camera moves, stalls, epoch/location changes and resize.
A projected cloud transmittance map shades opaque objects, water, and fog.

| Quality | Cloud dimensions / steps | Fog grid at 1080p | Cloud shadow |
|---|---|---|---|
| Low | Quarter width and height / 32 | 120×68×48 | 256² |
| Performance | Half width and height / 48 | 160×90×64 | 512² |
| High | Half width and height / 72 | 240×135×96 | 1024² |

The logarithmic fog grid integrates clear-air scattering and varying local
mist/dust, with sunlight, cloud and gate-light shadows. It uses RGB scattering and
scalar extinction. Up to 32 nearby closed square/triangle roof cells exclude dense
mist; thin haze remains. Openings are not a full indoor airflow simulation. Fog
uses spatial reconstruction; only clouds use temporal accumulation. Winter keeps
its gust, visibility, snowfall and illumination multipliers throughout the day.

Stars and the galactic band/dust lanes are deterministic cached 2048×1024 textures.
They rotate with the planetary clock and are sampled independently of the coarse
sky lookup, then attenuated by atmosphere, clouds, and fog. There are no moons.

Water and physics share clipped canonical water triangles, body IDs, mean surface
height, depth, and downhill flow. Wet terrain and a bank band retain metre detail
at every LOD, with conforming transitions. Water has no fixed elevation offset.
Natural closed basins form level lakes up to 12 m deep; rivers remain shallow.
Generator 5 incises river routes instead of using raised priority-flood levels,
blends overlapping reach heights, and uses a rounded shallow channel profile.
Generator 4 remains supported for existing locations and uses its original terrain
and cache identity. Start a separate universe with `--universe rivers-v5` to use
the new channels without modifying an existing save. Both versions use the
repaired shoreline: water closes on the wet side of a steep dry bank.
Water vertices carry actual bed depth for shoreline fading and bounded refraction;
this is reconstructed from retained terrain vertices for far tiles too. Shared
vertex flow and bounded overlapping animation phases avoid triangle seams and
accumulating UV stretch. `--view river-bank` reproduces the seed-7319 bank camera.
`--rivers-only` tests generation, seam/depth agreement, and legacy-save retention.

The shader has flow-aligned normals, Fresnel, depth absorption, guarded refraction,
shore foam, and bounded screen-space reflections on Performance/High. Misses fall
back to the sky/cloud/star environment. Low uses that environment alone.
Underwater views add extinction and a two-sided surface with a soft transition.
There are no caustics or geometric waves. River flow is capped at 1 m/s.

Swimming runs in shared authoritative/predicted physics at 3 m/s, or 4.5 m/s with
Shift. Space rises and Ctrl dives. Capsule immersion uses entry/exit hysteresis.
Crate buoyancy clips the oriented box against a local water plane through Jolt;
nine footprint samples approximate partial coverage at banks. Water motion never
changes the collision terrain. Native buoyancy uses the pinned library's float ABI
directly because the managed wrapper did not apply impulses in the validated build.

Inspection controls (client executable):

- `--time-of-day 0..24`: fixed local hour and frozen environmental animation.
- `--visual-time SECONDS`: deterministic weather/water animation time.
- `--pause-sky`: freeze the current visual clock without stopping physics.
- `--clouds on|off`, `--volumetrics on|off`, `--water-reflections on|off`.
- `--view sky|sun|galaxy|river|lake|underwater` with smoke/benchmark mode.
- F3: local hour, sun elevation, cloud history, volume/LUT memory, water-body
  count, immersion, and separate sky/cloud/fog/water GPU times.

Build Release, then use `scripts/Test-Atmosphere.ps1` for fixed dawn/noon/sunset/
twilight/night captures across all presets. `--atmosphere-only` runs focused
geometry, physics and persistence checks; `--atmosphere-network` runs two actual
client sessions through direct and delayed/jittered connections. The default test
suite includes the focused checks. See VALIDATION.md for measured results and limits.

## Tree Pack 3D and forests

Tree Pack 3D adds five temperate models, including one autumn-coloured tree.
World-space seeded forest patches concentrate trees into stands while retaining
meadows. Icy worlds use denser winter spruce/birch stands; deserts remain sparse.
Roots use jittered 8 m cells (at least 6 m spacing), stay dry, avoid steep slopes
and retain the gate clearing/approach exclusions. Canopies may cross chunk edges
and overhang banks; the root chunk owns rendering, collision and removal IDs.
Scenery version 2 changes the cache key and requires a fresh universe rather than
rerolling vegetation around saved buildings. Existing saves are preserved.

Distant tree models are drawn in groups of eight through a shared transform
palette, including sunlight, gate-shadow and precipitation-shelter passes.
This uses the pinned FishGfx mesh API; batch geometry is reused and disposed with
the renderer, and its memory is included in F3's approximate geometry total.
Only visible, non-removed trees enter each pass. Near trees retain individual LOD
selection and full source branches. The new pack has LODs at 80 and 180 m.
Use `--view forest` or `--view tree-pack-1` through `tree-pack-5` with benchmark
mode for repeatable inspection. `scripts/Test-Environment.ps1` accepts these
views and `-TimeOfDay 12 -VisualTime 12` for stable daylight captures.

## Movement presentation and pushing

The client predicts both players and crates at 60 Hz. Rendering uses the previous
and current predicted snapshots with one tick of visual delay (16.7 ms), including
box rotation. Camera, local player and props use one presentation sample per frame.
Mouse look remains immediate. Authoritative collision is never interpolated.
Small reconciliation errors decay with a 35 ms half-life; corrections of 1 m or
more snap. Matching snapshots retain the existing physics/contact state.

Remote actors use a bounded server-tick history with a 100 ms interpolation delay.
Ping corrections and changing prediction lead are steered at up to three ticks per
second after the first RTT anchor. Input is sampled every render frame and sent
for every scheduled fixed tick, including catch-up ticks. Jump remains an edge.

The player push limit is 300 N. This exceeds the approximate 216 N friction
resistance of two grounded 10 kg crates; walking/sprint speed, gravity and crate
friction are unchanged. Physics compatibility hashes include this change, so
restart both updated client and server. No save or protocol migration is needed.

F3 includes prediction confirmations/corrections. Run `scripts/Test-Movement.ps1`
for an isolated generated-world walking/pushing route, screenshots and a per-frame
`movement.csv` trace. It uses a new diagnostic universe, not the user's save.

## Volumetric blowing snow and water classification

Low blowing snow now uses wind-advected 3D density in the shared fog volume.
The stretched ground billboards are removed. Small falling flakes remain
billboards, with a shorter storm streak and slightly lower opacity. The density
follows the cached precipitation surface, fades with height and coverage bounds,
and samples the same sunlight/gate lighting as fog. Roofs and enclosed room
volumes suppress it. `WeatherSettings.BlowingSnowDensity` defaults to 0.18 per
metre; gust strength scales this value. The periodic wind lattice wraps smoothly.
Three samples across each depth cell reduce banding in shallow wisps. No extra
render targets are allocated. With `--volumetrics off`, ground spindrift is off;
falling snow continues according to the existing weather control.

Water uses the camera's canonical water query for immersion, shared with the
underwater composite. A river uphill from a dry camera no longer takes the
underwater branch or loses atmospheric fog. Wave normals follow the water mesh
slope; absorption uses the accepted refracted sample's optical path. Water writes
surface depth for later snow occlusion while refraction reads separate opaque
colour/depth attachments. Terrain generation, saves and physics are unchanged.

`--weather-check --weather-fixture` also probes the actual blowing-snow shader:
shelter, changing wind, periodic wrap, height cutoff and disabled density.

### Persistent ground mist

Icy planets also have a persistent low mist layer, including in light snow and
between gusts. It shares the existing scattering volume and lighting with
blowing snow. `WeatherSettings` exposes `GroundMistDensity` (0.12/m),
`GroundMistBlanketHeight` (0.6 m), `GroundMistWispHeight` (1.2 m), and
`GroundMistSpeed` (0.3 m/s). Density is strongest below 0.33 m, falls toward
0.6 m, and has sparse wisps above that. Broad world-space noise retains a
nonzero lower-layer density; its 4096 m wind period wraps continuously.

A separate 256 m orthographic depth map contains only terrain and water.
Trees, rocks, roofs and props do not raise the mist surface. Height lookup
interpolates valid texels and rejects holes. The map centre snaps to texels;
camera movement and relevant terrain mesh changes invalidate it. Low,
Performance and High use 256², 512² and 1024² respectively: 0.25, 1 and 4 MiB.
Density fades radially from 96 to 120 m, inside the map boundary. Enclosed-room
exclusion includes the floor and refreshes when building geometry changes.
Five samples across each volume depth cell integrate the shallow mist layer.
This is procedural visual mist, not a fluid simulation around obstacles.

Use `--ground-mist off` for comparison while retaining snowfall and gust-driven
blowing snow. `--weather off` or `--volumetrics off` also disables ground mist
and releases its map. Planet changes and disconnect release the map too.
Benchmark logs report `mist_map`, `mist_map_max`, `mist_map_CPU` and `mistMiB`
alongside `fog`; map updates are measured separately from volume injection.

`scripts/Test-GroundMist.ps1` captures snow, gate, slopes, forest, river and
doorway views at noon and midnight in all three quality presets. It also
cycles the shelter roof and reads back the ground-mist shader to check density,
height, animation, wind wrap, coverage fade, missing surfaces and room exclusion.
`Test-Environment.ps1` accepts `-GroundMist on|off`, `-Volumetrics on|off` and
`-ActiveGate` for comparisons and gate-lighting inspection.

## Player entry waves

Player contacts already reached the contour queue, but the old one-pixel edge
impulse was attenuated by both the entry multiplier and the persistent-boundary
startup ramp. It was barely visible after the normal filter. Entry now injects
velocity along a 0.10 m contour band, independent of simulation resolution and
boundary warm-up. This uses the existing wave height/normal and white reflection
shading; there is no separate colour overlay. The same contour treatment applies
to prop entry. Persistent prop reflection and motion-boundary forcing keep their
existing ramp. Players remain entry-only, with no collider or persistent obstacle.

`Test-Horizon.ps1 -Players -Motion` renders opposing local/remote capsule crossings.
`-WaveCheck` now exercises real player contacts through lazy target allocation and
the production fixed-step renderer at all three grids. It checks field amplitude,
not just whether a CPU ripple record exists.

Player entry also applies a 25 mm initial displacement to that contour, plus
0.35 m/s initial velocity. This bends the white folds immediately: the first-person
camera crosses about 80 ms after capsule contact at normal walking speed. Props
retain their existing velocity-only entry. Capsule outlines use two symmetric
semicircles; approaching the opening sideways also triggers entry. Wave programs
compile with the visible horizon instead of on first contact.

Use `Test.ps1 run -Scenario sandbox -Recipe gate-roundtrip` for real movement
through a dialed gate, or a JSON recipe with bounded `move` and `capture` steps.
`Test-Horizon.ps1 -Players` remains a rendered-position preview.
Walk diagnostics read GPU heights
and are unsuitable for performance measurements. `-WaveCheck` asserts a response
within 35 ms and compares actual white-highlight shading with/without the field.

## Dense meadow rendering (2026-11-07)

Low / Performance / High use 24 / 36 / 48 candidate tufts per square metre,
8 / 12 / 16 m detailed ranges and 32 / 48 / 72 m geometric ranges. Suitability
and exclusions reduce actual counts. Each tuft has four 15-35 cm blades.
Detailed blades have three triangles; simplified blades have one. A six-metre
transition overlaps complementary dither patterns. Stable ranked subsets thin
geometry farther away; the coverage field continues on terrain beyond it.

8 m tiles upload two RGBA32F texels per tuft. Shared, bucket-sized blade templates
expand records using vertex IDs through the pinned FishGfx mesh API. Camera
movement no longer rebuilds tile geometry for density changes. Background jobs
and uploads share the terrain budget, checked between whole uploads. Canonical
coverage is cached in a 1024-square R8 texture, including wet-cell, slope, gate,
spawn, DHD, building and scenery-base exclusions. Edits invalidate affected grass
and coverage; terrain LOD replacements do not change placement.

`meadow.glsl` shares colour variation and wind with terrain. A periodic,
mipmapped normal map is baked by `python tools/build_meadow_detail.py`; no external
texture source is used. Blades receive sunlight, cloud and gate shadows/lighting,
but cast no individual shadows. Bending remains wind-only. Shared blade templates
and the detail map are reused until renderer disposal; world-dependent tile data
and coverage are released on relocation or grass disable.

F3 includes detailed/simple draw counts, uploaded bytes, dynamic grass memory and
CPU/GPU time. `--view grass`, `--view props`, `--view river` and `--view wide` work
with smoke/benchmark capture controls. `--grass off` disables geometric grass and
terrain meadow detail.

Prop materials accept optional `metallicRoughness` texture metadata (glTF green
roughness / blue metallic). Existing materials retain their previous constants.
`python tools/import_props.py` reproducibly imports the supplied crate and barrel
scenes, texture maps and attribution. Sources remain untouched.

The distance-detail approach follows Karl Bittner's grass LOD discussion:
https://hexaquo.at/pages/grass-rendering-series-part-4-level-of-detail-tricks-for-infinite-plains-of-grass-in-godot/
Shaders are original code for this renderer, not copied Godot shader code.


## Streaming and corner update (2026-11-07)

Protocol 10 adds bounded detailed terrain, render-only distant tiles, outgoing-only destination preloads, cached visibility lists, tree LOD hysteresis and repeated-prefab batching. F3 exposes the new budgets and timings. See [STREAMING.md](STREAMING.md). Village wall joints now derive imported corner trim with matching shadows and collision.


## Terrain palettes, foliage and desert dust

Terrain uses eight active materials per biome from `assets/terrain/pbr/manifest.json`.
Three RGBA arrays contain sRGB albedo, DirectX normals, and linear
AO/roughness/height/metallic (metallic is zero, including packed alpha).
Low/Performance/High select 512/1024/2048 maps: 32/128/512 MiB for the active
palette, plus a 16 MiB surface field. Only the current palette stays resident.
`python tools/import_terrain_pbr.py` reproduces packed maps, colour-correct
albedo mips, normalized normal mips, source hashes and duplicate inventories.
Original and unused packs remain in `assets_itch/textures/freepbr_ground`.
The manifest records 2 m soil, 4 m sand and 6 m cliff scales, normal strength,
height-blend strength and albedo gains for the darker meadow scans.

The one-metre surface field stores canopy, moisture, canonical ground height and
placement availability. Neighbouring chunks contribute to canopy and actual-water
bank masks. Deterministic 18 m regions and 2.6 m clusters are shared by terrain,
vegetation and sand emission. Tree removal and building edits invalidate masks.
Two ground layers plus exposed rock use normalized height-adjusted blends and
triplanar slope mapping. AO affects indirect light only; dielectric GGX/Schlick
is terrain-only. Height changes blend edges, never geometry or physics.
The old meadow colour override is no longer applied.
Natural clearings use dry-brown-dirt-pbr-material; temperate and icy cliff slots
use barren-ground-rock. The retired bumpy-worn-ground and grassy
rocky-rugged-terrain-pbr layers are no longer selected. Desert gravel repeats
at 2 m. Use --view gate-ground --gate-effect open for a close material check
under gate lighting.

`assets/foliage` includes eight grasses, two ferns and three dry bushes, each with
a simplified mesh LOD and retained attribution. Blender import bakes transforms,
normalizes planting pivots and matches bottom-left UVs to bottom-up texture rows.
Flat spreading plants preserve proportions with a 1.4 m footprint cap; upright
clumps are 35-80 cm high, mixed with 15-35 cm procedural blades. Performance
uses overlapping LODs around 16 m and fades grass by 64 m, ferns/bushes by 48 m.
GPU instances share one mesh per species/LOD and two RGBA32F placement records.
Placement uploads are spread across frames within the preparation budget.
FishGfx's added DrawMeshInstances API supports indexed and non-indexed meshes.
Alpha-tested plants write depth, receive existing lighting/shadows and bend from
anchored roots. Individual grass shadow casting and interaction remain deferred.
`--grass off` disables blades and clumps; terrain materials stay enabled.

Desert scattering uses linearized #C99B69 lit by sun and sky, with extinction
0.0012 per metre at baseline and up to 0.0025 during gusts. The existing volume
integrates local wisps; transparent quads use depth tests and soft intersections.
Low/Performance/High allocate 400/1200/2400 particles at 32/48/64 m. Wet ground,
water, rock and closed interiors suppress emission. Integrated wind drives both
particles and wisps. `--dust off` or `--weather off` disables moving dust and
gust modulation but retains baseline haze. The non-volumetric path uses the
same coefficients. There is no accumulation or gameplay effect.

F3 shows palette memory, clumps, particles and preparation CPU time. Benchmark
LANDSCAPE lines report terrain GPU time, combined preparation CPU time and
resource counts. The grass timer includes blades and clumps; weather includes
sand quads; fog includes dust integration. The dust_with_fog_GPU_ms metric is
the per-frame sum of the complete fog pass and sand particles, so it bounds the
added dust cost conservatively. Distant normal-map reads stop after detail fades. `--landscape-check` reads back GPU
results for valid materials, packed alpha and mesh instancing. Add
`--weather-fixture --view weather-shelter` on a desert planet for room tests;
`--weather-roof-cycle` checks invalidation. Fixtures never enter saved worlds.

`python tools/validate_landscape_assets.py` checks hashes, mipmaps, normals,
alpha coverage and mesh LODs. `scripts/Test-Landscape.ps1` uses isolated port 7837,
a 20-second warm-up and artifacts/landscape-validation output. `-Motion` records
48 moving-camera frames with live wind for video assembly. Existing development
servers are not stopped. Tree sizes/depths, terrain collision, saves and protocol
formats remain unchanged.

## Clear temperate daylight (2026-11-08)

The display pass already uses an ACES-style fitted tone curve and sRGB encoding.
Clear weather is calibrated in linear HDR before that pass, rather than adding
another gamma conversion or lifting exposure. AtmosphereIllumination owns the
editable values: temperate cloud coverage 0.40 (previously 0.52), daytime sky
radiance scale 1.4 (previously 3), indirect irradiance scale 3 (previously 5),
sky fill (0.24, 0.28, 0.34), and sun multiplier (1.45, 1.38, 1.26).
The warmer directional light gives sunlit surfaces more separation from cool
shade without driving all three channels toward white. Cloud shadows still
attenuate direct sunlight normally; cloudy patches are not bypassed.

Daytime calibration blends in over sun-height 0.05 to 0.35. Twilight, night,
desert and winter calibration remain unchanged by this clear-day refinement.
Winter retains its 0.85 cloud coverage and weather multipliers. Fixed exposure,
UI, gate/DHD emission, terrain generation and networking are unchanged.
Use --time-of-day 11 with --clouds on/off for comparison. The sky table cache
still depends on atmosphere parameters; these final sampling gains do not
require scattering-table regeneration.

## Interaction rendering (2026-11-09)

F3 toggles statistics. Shift+F3 independently toggles collision and placement
lines. Statistics and their cached glyph mesh refresh five times per second.
Static collision batches are cached by terrain/content revision, culled near
the camera and prepared incrementally; dynamic bodies use a separate buffer.
The overlay reports incomplete preparation and caps the total at 30,000 lines.
DHD hover uses a local triangle BVH and the live native physics ray query for
occlusion; the server rechecks clicks using the same collision rules.

Matched 1080p Performance DHD views in the compact lab measured frame p95 of
1.660 ms (off), 2.282 ms (statistics), and 1.956 ms (collision plus statistics).
Isolated CPU p95 was at most 0.0072 ms for the cached HUD, 0.0402 ms for DHD
interaction, and 0.0842 ms for the cached collision overlay. These are small-scene
measurements, not full-world or cold-start guarantees. A separate full-world
travel recording with frame capture enabled measured 27.049 ms frame p95 and
does not establish the 8.33 ms full-world target.

Logs, screenshots, pickup/travel videos and limitations are indexed in
`artifacts/interaction-pickup/validation.json`. Reproduce pickup with
`scripts/Run-GraphicsLab.ps1 -Build artifacts/interaction-pickup/build -Seconds 12 -Characters 0 -ClientArguments @('--pickup-check')`.
Build client and server together: these changes require protocol 16 and matching
physics content hashes. Normal packages and existing development servers are
not replaced by this validation run.


### Shadow and viewmodel correction (2026-11-09)

Directional cascade selection and range fading use camera distance to match the
camera-centred clipmaps. Adjacent cascades blend before combining distant terrain
visibility. Turning in place therefore does not change a fixed surface's cascade.
Receiver-plane correction, quality resolutions, update schedules and bias remain
unchanged. Viewmodel opaque coverage rejects hidden world emission before bloom;
world atmospheric composition precedes the viewmodel.

Inspection cameras `shadow-rotation`, `shadow-rotation-left`,
`shadow-rotation-right`, `gate-weapon` and `gate-weapon-edge` support repeatable
regression captures. Use `--visual-time 12` for fixed animation time. Validation
artifacts and matched shader-only comparisons are in
`artifacts/gate-physics-fixes`; normal gameplay does not enable diagnostics.

### Vegetation materials and loading (2026-11-09)

Grass uses eight-blade clumps with 18–45 cm blade heights and 20–45 cm footprints.
Near/middle/far templates contain eight curved, four simplified curved and two
single-triangle blades. Settings retain 8 m tiles, deterministic ranked placement,
shared ecology exclusions and wind anchored at blade roots. Terrain and blades
share meadow colours; imported clover, ferns and accent grasses now also sample
that ground blend. Their two-sided lighting uses softened normal detail and a sky-
facing leaf response, reducing the nearly black patches without changing exposure.

The blade colour/normal atlas comes from ambientCG Foliage008. Ground meadow uses
ambientCG Grass005. Both are CC0; provenance and reproducible import commands are
in `ASSET_LOADING.md`. Soil, woodland, banks and other planet materials remain.

Tree rendering has explicit leaf/wood roles, clustered distant crowns, summer
birch crowns on temperate worlds and shared wind. Projected-height transitions
use 256/112/56 pixels with hysteresis; shadows initially use conservative rest
poses. Cooked alpha mipmaps retain leaf coverage and dilated border colours.
Texture row orientation is explicitly bottom-up to match the loose FishGfx loader.
Persistent scenery identities and collision are unchanged.

`--grass off`, `--loose-assets`, fixed-time inspection and existing quality presets
remain available. F3 includes clump/triangle counts, preparation/upload costs and
asset queue/memory counters. No additional benchmark sweeps are required for this
visual revision; further performance testing was stopped at the user's request.

## Tree inspection and review, 10 November 2026

See [TREE_PROTOTYPE.md](TREE_PROTOTYPE.md) for the source-derived trunk and
volume canopy, now enabled on normal planets. Leaf colour and
saturation are explicit `TreeRenderSettings` values; the existing gameplay tint
is retained. Loose builds now use the same alpha-coverage mip filter as cooked
packages. Imported prefab alpha cutoffs also apply in their shadow passes.

### Inspection canopy lighting correction

The opt-in volume tree uses descriptor version 2: maximum occupancy is separate
from average optical density, stored in the second texture's alpha channel.
Density-weighted linear-colour/normal mips, full-crown sun and sky attenuation,
two-sided camera-independent normals, and distance-integrated opacity replace
the earlier short shadow ray and camera-facing normal flip. Wind deforms both
bounds and normals. See `TREE_PROTOTYPE.md` for diagnostic views and limits.
Physical scenery remains unchanged; normal forests now use this lighting path.

The tree inspection fixture also accepts `--tree-species broadleaf`,
`broadleaf-2`, `birch`, or `spruce`. `current`, `mesh`, and `volume` modes use the
same selected species and scale for direct comparisons. Birch/spruce use lower
optical density for their branch-card input. Normal forests use per-species bakes.

The graphics-lab `--tree-forest` option places a deterministic mixed grove with
shared canopy textures, per-tree transforms and conservative projected bounds.
It provides inside, near, above, far and walking cameras. This is render-only
inspection; the extra grove does not replace gameplay placements or add collision. See
`TREE_PROTOTYPE.md` for controls and retained captures.

## Ambient occlusion

Opaque lighting writes HDR colour, ambient diffuse and geometric normal/material
classification. A spatial horizon AO pass (GTAO-style slice integration), bilateral
filter and depth-aware reconstruction remove only occluded ambient diffuse.
Sunlight, direct gate/DHD light, specular and leaf transmission remain unchanged.
The resolved unfogged target feeds aerial perspective and water transmission/SSR.
Sky and gate membranes are excluded. Additive emission writes zero auxiliary
contribution. Previews and debug overlays render after the AO resolve; the weapon,
particles and UI do not receive AO.

`--ao auto|off|low|performance|high` defaults to the graphics preset. The pause
menu's Ambient occlusion button (O) persists the explicit quality under LOCALAPPDATA.
Inspection overrides: `--ao-radius 1` (0.1-3 metres), `--ao-strength 1` (0-2),
`--ao-view lit|raw|filtered|normals|ambient`. Debug outputs bypass tone mapping/fog.
Low: half resolution, 2 slices x 3 samples each side; Performance: half, 3 x 4;
High: full, 3 x 6. Sampling is fixed, with no temporal history. Thin leaves/grass
have reduced occluder weight; receiver strength is 25%/35% respectively. F3 reports
AO GPU time/targets and shared canopy visibility bytes. Screen-space AO cannot
represent offscreen occluders and may lose detail at silhouettes or subpixel leaves.

The independent GLSL implementation uses the slice integral described by Jimenez
et al., Practical Realtime Strategies for Accurate Indirect Occlusion:
https://research.activision.com/publications/2020-03/practical-real-time-strategies-for-accurate-indirect-occlusion
Intel XeGTAO informed the separation/filtering approach; no Intel shader source
is copied: https://github.com/GameTechDev/XeGTAO . FishGfx remains pinned.

## Distant grass and world canopies

Grass now extends to 64 / 112 / 160 metres on Low / Performance / High. Near and
middle representation distances and physical blade heights remain unchanged.
The outer two-blade clumps have wider silhouettes and retain 10-20% ranked density,
instead of falling to 5% by 30 metres. Outer tiles store only the first quarter
of candidate ranks, promoting to dense records before they are needed nearby.
Promotion preserves roots, attributes and exclusions. CPU draw counts exclude
ranks that the distance shader would discard. Mip-filtered meadow normal detail
and subtle tuft colour variation continue on terrain beyond geometric grass.
Both use the active ecology field, including roads, canopy and water exclusions.

All normal worlds enable species-specific volume trees, including canopy AO.
The existing global AO pass already applies across presets. See `TREE_PROTOTYPE.md`
for the mesh fallback, projected-size transition and unchanged placement/collision.
F3 reports ready/pending canopy resources. No new benchmark sweep was run.

## Rifle surface finish

The rifle keeps its source atlas and UVs. `rifle_finish.glsl` adds filtered,
UV-attached fine machining grain, sparse broken scuffs with varied direction/length
and handling variation. These affect roughness and shallow normal relief as
well as colour. The receiver is smoother than the magazine, and cartridge brass
retains a separate material response. Arms and character clothing are excluded.

Rifle sunlight uses a GGX specular lobe with Fresnel response. A roughness-spread
sky/ground reflection follows the current atmosphere and viewing direction;
this is an environment approximation, not a reflection of nearby scene geometry.
It does not sample the viewmodel render target or bake fixed highlights into
the albedo. The mesh, sights, animations and original source textures are unchanged.

Scuffs have jittered positions, tapered ends, slight curvature, interruptions and
varied strength. They are finite marks rather than long stretched-noise stripes.
The quieter machining grain uses warped coordinates; subpixel marks fade out.

Local contrast in the authored rifle atlas guides patchy wear on raised details
and grime around screw/recess details. Dust darkens the coating and increases
roughness; faint handling-oil patches are slightly darker and smoother. Exposed
wear has a stronger metal response. These masks follow UVs through animation and
do not outline mesh triangles or introduce a uniform dirt tint.


### Detailed rifle arms (2026-11-10)

The first-person rifle now uses the gloved arms and sleeves from Cransh's
`characters/arms2` source (14,852 triangles). The original rifle geometry,
camera, 72-bone skeleton and eleven animation tracks are retained. The pistol
and its animations are not imported.

`python tools/import_viewmodel_arms.py` updates only the viewmodel mesh and its
materials. It reconstructs the source mesh from inverse binds, fits anatomical
arm/palm/finger frames to the existing rig, and maps the original four skin
influences, including forearm twists. It does not import the distorted displayed
glTF pose or alter animation tracks. Authored normals bypass the legacy flat-arm
normal repair. The full Blender character importer runs this step by default;
pass `-- --original-arms` to `tools/import_characters.py` for the original arms.

The 2K maps retain the supplied colour and OpenGL normals. The surface texture
packs occlusion/roughness/metallic in RGB; roughness is one minus source gloss,
and metallic is zero for the gloves and sleeves. Occlusion affects indirect
lighting only. Rifle wear and reflections remain exclusive to weapon parts.
Source files remain untouched. See `assets/characters/arms2-provenance.json`
and `arms2-license.txt` for hashes, settings and attribution.

Focused checks: `python tools/validate_characters.py` and
`python tools/validate_viewmodel_arms.py --baseline PATH_TO_PREVIOUS_RIFLE`.
The latter verifies exact preservation of skeleton, animation and weapon data,
repeatable fitting, material maps and every exported animation frame.
`tools/render_viewmodel_review.py` renders exported animation poses in Blender;
these inspect deformation and contacts, not the game's lighting implementation.

## Regional landscapes (generation 9)

Generated planets now cover 4096 x 4096 metres. Distant terrain samples the cached
8 m regional landscape directly through hierarchical tiles; it does not load
canonical collision chunks or distant vegetation. Revision 9 caps interior mesh
spacing at 16 m to preserve dunes and tributary ridges in large distant tiles.
Shared metre-spaced tile edges
retain shoreline and land continuity. A world-stable complementary mask hands
land and water between resident LODs over 0.35 seconds.

Habitat fields feed the material palettes, clustered trees and hybrid grass.
Meadows use four additional 0.35-0.75 m grass variants with independent 1 m bake
bounds; woodland keeps the original short variants. Existing vegetation quality
and distance settings still apply. Clear-weather regional fog and the far camera
range allow the larger landforms to remain visible. Fixed fixtures retain their
own extents and cloud-layer settings.

Marble rock, dry riverbed rock, Ground 055 S sand and Snow 002 enter through the
existing PBR importer and cooked material blocks. See PLANET_GENERATOR.md for
source attribution, controls, focused validation and remaining visual risks.
