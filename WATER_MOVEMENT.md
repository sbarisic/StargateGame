# Water and tactical movement

Implemented 10 November 2026. Validation resumed after the other agent finished,
following `instructions.md`: relevant Release builds, focused checks, and a small
water comparison. Broad gameplay coverage remains for developer playtesting.

## Movement

WASD runs at 5.4 m/s. Shift walks at 2.7 m/s. Ctrl requests crouch at 1.8 m/s.
Space is a jump edge; held Space does not generate another jump. In water,
Space/Ctrl rise/dive and Shift reduces swimming from 3 to 2 m/s.

`Movement.Accelerate` is shared by server simulation and prediction. Ground
friction is applied relative to the supporting body before wish-direction
acceleration. Walkable slope wishes follow the support plane at the selected
surface speed. Air input preserves momentum, with limited directional
acceleration and a takeoff-speed envelope. Gate arrival momentum enters the
same velocity integration instead of a separate velocity-assignment controller.

The standing and crouching capsules have the same 0.32 m radius and feet origin.
Their heights are 1.8 and 1.15 m; eye heights are 1.65 and 1.0 m. Growing the
capsule is rejected when obstructed. Both Jolt collision shapes are switched.
Checkpoint restore explicitly restores shape selection before restoring native
contacts. Stance and the air-speed envelope are part of `PlayerState`, so they
are also included in snapshots, prediction replay, and saved-player JSON.
Old saved-player JSON defaults to standing. Saved-position restore checks
clearance before choosing the saved stance or a safe recovery position.

Eye-position helpers cover interaction, aiming, carrying, building, audio, and
camera placement. Gate-crossing and destination-clearance queries use stance.
Camera smoothing changes only vertical presentation, with an approximately
0.12-second settling time and a swept collision probe. Crouched footsteps use
shorter strides and lower gain. Animation selection and the character importer
now include first-person and third-person `crouch-idle`/`crouch-walk` clips.
Both character packages have been reimported and validated, including the new
crouch clips. Provenance supports the external `assets_itch` junction. Texture
imports use temporary files and preserve unchanged outputs.

Protocol is **22** and the native bridge ABI is **2**. The source-derived physics
hash includes the movement, stance and bridge changes. Client, server and native
bridge must be rebuilt together; ordinary dotnet builds do not rebuild Jolt.
No terrain regeneration or world-save reset is required.

## Water

`--water-style legacy|realistic` selects the comparison path. Realistic is now the
normal and paired Visual Studio default; legacy remains an explicit comparison.
Tactical movement uses the shared simulation in both client and server.

The realistic shader uses three scales of advected, mip-filtered slope normals.
The generator creates a deterministic periodic spectrum and averages slopes
offline for each mip. The runtime reads RGBA payloads through the mounted asset
provider and publishes the texture only after all mips have uploaded. There is
no image conversion or mip generation in the water startup path.

`WaterAppearanceSettings` supplies absorption, scattering, refractive index,
roughness, ripple scales/strengths and foam strength. The same transport
coefficients are used above and below water. Reflections use a rough sky fallback
and a bounded screen-space trace: Low skips tracing, Performance uses 12 trace
samples plus at most 3 refinement samples, and High uses 24 plus at most 4.
Foreground and invalid refraction samples fall back to the original pixel.
Foam is broken into moving patches and is restrained on calm shorelines.
Neutral absorption is RGB (0.22, 0.215, 0.25) per metre, with linear scattering
(0.052, 0.057, 0.050). Icy worlds retain this muted gray-white appearance.
Temperate water blends toward the original blue-green optics in daylight,
with less blue under cloud shadows or overcast skies. The 430–650 m temperate
snow band smoothly restores neutral water at high elevations. Desert water
remains neutral. Both surface and underwater transport use this environment blend.
This appearance change needs a client restart, not world regeneration.
No second world render or water-geometry displacement was added. Canonical
water levels, swimming boundaries, buoyancy, currents and bank/bed assets remain
the source of truth.

`--water-debug` accepts `none`, `depth`, `normals`, `flow`, `reflection`,
`refraction`, and `foam`. A diagnostic view selects realistic rendering.
Render-only inspection cameras accept `--view water-shallow`, `water-deep`,
`water-grazing`, `water-river`, `water-lake`, `water-underwater`, or
`water-half-screen`; the existing `river-bank` view remains available.
Inspection point selection uses resident canonical terrain. Wait for the same
terrain region in both runs and record the resulting camera position.

The existing asynchronous GPU timer measures the water pass. `WATER` benchmark
output now also reports style, normal readiness, water CPU submission time,
managed allocation bytes, normal-texture residency, and total-frame statistics.
The normal texture is included in the renderer's approximate GPU residency.
CPU allocation counts cover the render thread, not graphics-driver allocations.

## Assets and focused validation

The water generator writes content-addressed RGBA payloads before atomically
publishing `assets/terrain/water/ripples.json`. Its incremental cooker stage tracks
the generator and the exact mip outputs. It does not depend on vegetation bakes.
The nine resident mips total 0.333 MiB. Preparation loads the complete chain even
for the baseline, so comparison modes have the same texture residency and a late
startup option cannot accidentally skip water preparation.

The character importer runs in Blender with `--python-exit-code 1`. Crouch sources
are `Rifle_Breathing_Crouching`, `Rifle_Walk_Crouching`, `Crouch_Idle_Loop`, and
`Crouch_Fwd_Loop`. `tools/validate_characters.py` passed for both packages, checking
weights, transforms, bone lengths, continuity, and required clips.

`python tools/build_asset_packs.py --skip-bakes` published the changed assets to
`artifacts/cooked/dev-override/`: 13 newly cooked blocks, 10,060 reused, no base
packs rewritten. Previously published shader changes remain in the overlay.
Original model sources, textures and licenses are retained. Restart the client
after updating shaders or skeletal packages; this provider requires a session
reload for those changes.

Release client and server/test builds passed. The test build reports an existing
nullable warning in `GalaxyChecks.cs:511`; the client build has no warnings.
The current ABI-2 native library was rebuilt by the other agent and used for the
focused native physics checks here.

`--tactical-movement --trace` passed: default/walk/crouch speeds, normalized
diagonals, support-plane acceleration, air momentum, jump height, no held-key
hopping, crouch clearance, both checkpoint shapes, camera clearance, delayed
stance rollback, and 30–240 Hz presentation. Measured fixed-tick timings:

| Action | Time |
| --- | ---: |
| Accelerate to 5.4 m/s | 0.333 s |
| Release to rest | 0.333 s |
| Counter-strafe to rest | 0.100 s |

Trace: `artifacts/movement/tactical-course.csv`.
`--atmosphere-only` passed 58 checks, including canonical shoreline seams,
swimming, diving, buoyancy, water checkpoint replay and environment persistence.
Log: `artifacts/water-movement-physics.log`.

The first render comparison exposed two integration problems, now fixed: water
detail preparation started before the realistic option was applied, and the
fixed-resolution benchmark target was copied upside-down into the window. Saved
target captures were upright. Both benchmark and UI preview window presentation
now use matching bottom-left framebuffer texture coordinates.

Visual/performance coverage is a short fixed-camera 1080p Performance comparison,
not the original exhaustive quality, weather, gate-light and travel matrix.
Extended traversal, repeated resource-lifetime checks and manual Visual Studio
F5 playtesting have not been performed in this pass.

## Short rendering comparison

RX 9070 XT, Release, exact 1920×1080, Performance, VSync off, `graphics-lab`
temperate fixture, `water-half-screen`, 11:00, visual time 30. Both modes loaded
the same ripple chain. Camera was `(-2, 1.2999992, 47)`. Each confirmed run used
15 seconds of warmup and 8 seconds of sampling.

| Metric | Legacy | Realistic |
| --- | ---: | ---: |
| Water GPU p95 | 0.136 ms | 0.171 ms |
| Water CPU submission p95 | 0.031 ms | 0.035 ms |
| Total frame median | 9.391 ms | 9.551 ms |
| Total frame p95 | 9.532 ms | 10.414 ms |
| Water managed allocation/frame | 4,816 B | 4,816 B |
| Water normal residency | 0.333 MiB | 0.333 MiB |

The confirmed comparison is below the 0.8 ms water budget and adds 0.882 ms to
total-frame p95. A prior five-second sample gave 0.146/0.170 ms water p95 but
9.943/11.171 ms total-frame p95, exceeding the 1 ms frame-delta target; grass-pass
p95 also varied by 0.923 ms. These short runs do not establish a robust all-scene
performance guarantee. Capture/readback stalls remain in the recorded maximum
frame times and were not removed from the logs. The final reported scene draw
counts also differed (332 legacy, 394 realistic), despite matching camera and
asset settings; total-frame delta is therefore provisional, not an isolated
measurement of water cost. The asynchronous water-pass timings are reported
separately for that reason.

Before/after captures and full timing/residency logs:
`artifacts/water-movement/confirmed-legacy/` and
`artifacts/water-movement/confirmed-realistic/`. The shallow fixture shows visible
pebbles, a smooth shoreline and restrained reflection; it is not validation of
deep lakes, underwater transitions or moving-water behavior across all worlds.
