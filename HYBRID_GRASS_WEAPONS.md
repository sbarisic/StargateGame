# Hybrid grass, hand shadows, and weapon storage

Implementation and validation snapshot: 10 November 2026. Parallel movement,
water, and asset-loading work remains in this checkout. No user save was deleted
or regenerated for this change. The validation programs used isolated build
directories and temporary saves.

## Grass

Near-mesh density, growth and distance fading now select whole blades using a
flat root attribute. Previously those decisions sampled changing positions along
each curved blade and could cut gaps into its middle. Hard exclusion boundaries
still clip fragments to keep grass out of buildings. Grass descriptor version 3
adds a root stream; matching client and cooked content are required.
`python tools/bake_grass_volume.py --roots-only` adds this stream only when the
generated mesh bytes exactly match the existing assets, leaving volume fields
unchanged. Normal full bakes also generate the roots.

### Rendering optimization update (13 November 2026)

The independent harness settings `grassDepthCulling`, `grassShaderReuse` and
`grassScreenLod` now default to true. Set all three false for the comparison
baseline. Settings, capability, submitted patch counts, mip distribution and
readiness are exposed in rendering observations and screenshot metadata.

`grassVolumeDensity` independently controls primary volume density (0.1 to 1,
default 0.8). It leaves nearby meshes and the mesh/volume overlap unchanged, then
smoothly reaches the requested density between the transition's far distance and
twice that distance (28 to 56 metres on Performance). The blend uses world-space
sample distance, so it stays continuous across patch boundaries. Sunlight sampling,
grass placement and draw distance are unchanged. This is an appearance control:
lower optical density can require longer marches and does not guarantee a speedup.
Set it to 1 for the previous fullness; the performance recipe fixes it at 1 to
keep the optimization comparison independent of this appearance adjustment.

Depth rejection reuses the existing post-canopy depth snapshot. Each tile caches
sixteen compact ranges from its existing surface field, covering ground height,
height scale and interpolation support. Patch proxies include the possible
threshold-reconstruction displacement; invalid ranges fall back to tile bounds.
The original march interval bounds wind-displaced samples in XZ, and occupied
samples' unbent roots remain inside their patch. Tighter proxies therefore do not
change marching intervals or sample positions. Front-to-back volume draws follow
the nearby mesh phase, with the same per-tile instance draws as before.

The volume shader uses conservative nearest depth and `depth_greater` on OpenGL
4.2 or ARB_conservative_depth hardware. Camera-inside and near-plane crossings
use near depth. Unsupported hardware retains explicit scene-depth clipping,
tighter raster bounds and ordinary hit-depth writes. Forced early fragment tests
are not used. No extra depth copy, new texture or asset rebake is required.

Shader reuse shares identical-coordinate terrain samples and computes patch
rotation once per invocation. Wind remains position-dependent. Surface samples
are shared with exclusion checks only when the reconstructed coordinates match
exactly. Mesh blades, placement, density, draw distance, palette, ambient lighting
and the four sunlight samples are unchanged.

Screen LOD uses projected patch width/height at the actual render resolution.
It blends additional fractional filtering after the mesh overlap, with a two-metre
patch guard, and caps at resident mip 3. Integration spacing uses the original
unclipped interval; the final clipped segment keeps its quadrature point. Occupancy
lookups cover both contributing density mip footprints. There is no hierarchical
skip traversal. Subtle distant detail differences are intentional.

Run `scripts/Test.ps1 check -Group grass-rendering` for focused checks and
`scripts/Test.ps1 run -Scenario sandbox -Recipe grass-performance` for measurement.
The recipe fixes 1920x1080, Performance quality, VSync off, noon, exposure, camera
and wind phase. It waits for asset/tile-growth readiness, warms each mode for five
seconds, and samples eight seconds before capturing evidence outside the timing
window. Existing build outputs and incremental content caches are reused.

Completed GPU queries report mesh, volume, other foliage and existing depth-copy
times separately, with median/p95/max and an aggregate grass value. CPU and frame
times, submitted triangles, patches and mip counts accompany them. GPU total sums
instrumented passes rather than measuring presentation latency. Mip counts describe
submitted patches, including patches later rejected by the shader or depth test.

Measured on AMD Radeon RX 9070 XT, driver 32.0.23017.1001:

| Mode | Grass + copy median (ms) | Frame median (ms) |
| --- | --- | --- |
| Baseline | 10.71 | 18.42 |
| Depth only | 4.77 | 12.45 |
| Shader reuse only | 10.33 | 18.05 |
| Screen LOD only | 10.38 | 18.09 |
| Combined | 4.48 | 12.15 |

A confirming pair after adding the patch overlap guard measured 10.73/10.88 ms
baseline grass median/p95 versus 4.50/4.68 ms combined. Frame median fell from
18.49 to 12.24 ms; render CPU rose from 1.66 to 1.86 ms. The existing depth copy
cost about 0.027 ms. Both modes submitted 1,712 volume patches and 758,608 nearby
mesh triangles. This is about 58% less grass GPU time and 34% less frame time in
this view, not a general FPS guarantee.

Validation passed 14 focused checks, including 4,538 occupied wind/slope samples,
all eight asset variants, and 28,967 filtered-empty samples across fractional mips.
The harness checks also passed. Paired low-camera, boundary, pond-bank slope,
low-sun and 720p captures showed no obvious new gaps or seams. The low-camera pair
was pixel-identical; all other pairs had a zero 99th-percentile channel difference.
Bounded movement with live wind completed. Static evidence does not establish
absence of temporal artifacts in every scene; the fallback has not been tested on
physical hardware lacking conservative depth support.

### Existing grass system

Hybrid is the default. Use `--grass-renderer=mesh|hybrid`, or H / the Grass control
in the pause menu, to switch. `--grass=off` still disables grass. The existing mesh
renderer remains active until volume assets and shaders are ready.

`tools/bake_grass_volume.py` produces eight deterministic variants from the current
blade profile and grass atlas. Each variant has a matching nearby mesh, a
128 × 32 × 128 density/color/normal/occupancy hierarchy, and cached hemisphere
visibility. Each footprint is 2 × 2 m. Four short variants have a 0.5 m vertical
domain and 0.28 m tips; four meadow variants have a 1 m domain and 0.75 m tips.
The surface field continuously scales meadow height. Fullness comes from coverage
and thickness.

The renderer retains 8 m streaming tiles and existing placement/ecology masks.
Each tile holds sixteen patches and a terrain-height, coverage, material, and
hard-exclusion surface. Conservative exclusion sampling prevents interpolation
from filling prohibited areas. Both paths use the same variant and world-space
transition mask, terrain-relative roots, and root-weighted wind.

| Quality | Mesh/volume transition | Hybrid range | Mesh-only range |
| --- | --- | --- | --- |
| Low | 12–20 m | 128 m | 64 m |
| Performance (Medium) | 20–28 m | 224 m | 112 m |
| High | 28–36 m | 320 m | 160 m |

The mesh/volume transition distances are doubled. This retains individual blades
farther from the player and increases mesh rendering work; total grass range and
placement are unchanged. Earlier performance measurements below predate this change.

First-person walk/run clips now share idle's weapon origin. Both skeletal roots
receive the same correction to keep hands attached, while authored motion remains.
Clip changes blend local poses over 150 ms, including interrupted transitions.
Inspection poses remain exact. Crouch, aim and action poses retain their authored
offsets; hand shadows use the same final pose as the visible model.

The outer 15% fades toward meadow shading. Habitat-driven terrain shading carries
the meadow appearance farther toward the horizon, where individual blades are
smaller than a pixel. Rocky displaced ground remains beneath nearby grass.
Grass eligibility is authored per texture as `grassCoverage` (0–1) in the terrain
PBR manifest. Material selection drives nearby grass fields and distant meadow
shading in every climate. Suitable images retain grass on slopes; bare rock,
sand and snow images are disabled through their material values, without separate
slope, beach or climate bans. Snow overlays reduce the exposed grass material.
Physical exclusions keep roads, water and structures clear. Hybrid fields include
continuous undergrowth between candidate tufts. Importers reproduce the flags.
Temperate meadow and woodland/moss roles use the rocky-rugged material with its
0.16 m parallax depth. Grass bakes wrap crossing blades at patch boundaries,
without a border fade. Per-patch rotations vary the motif; height blends through
the canopy field instead of switching between short and tall at tile boundaries.
Ray marching uses scene-depth
termination, occupancy skipping, distance-dependent mip selection, at most 192
primary samples and four sun-transmission samples. Shared volume resources stay
resident across travel; tile jobs are cancelled and tile resources released.
Diagnostics include volume bytes, patch count, and grass GPU time.

The asset-pack pipeline includes the bake. This workspace's updated shader and
grass blocks are available through `artifacts/cooked/dev-override`; current VS
profiles already mount that overlay. Base packs were not rewritten. Isolated
validation builds used `-p:CookAssets=false` with the overlay mounted.

## Weapon storage

I opens inventory, 1–4 selects or toggles the corresponding owned rifle, and 0
holsters. Empty hands disable aim, fire and reload and hide the rifle HUD and
viewmodel after the holster animation. Existing interaction/building controls
remain available.

The placeable **Weapon crate** reuses the furnishing chest asset. Both this prefab
and generated `furnishing-chest` instances provide sixteen shared slots. E uses
the nearest interaction target, a 2.5 m reach limit, and a server physics LOS test.
Cupboards and barrels remain decorative. The amber inventory screen provides
Equip, Holster, Store and Take actions. Retrieval does not equip automatically.

Each rifle has a unique ID. Inventories, container contents, equipped references,
revisions, and idempotency receipts live in dedicated SQLite tables. A transfer
commits both owners and revisions in one transaction before acknowledgement.
The server rejects stale requests, wrong ownership, full destinations, blocked
or remote access, and transit access. Open viewers receive committed updates.
Removal checks run inside the same database transaction as destruction: non-empty
containers block their own removal and removal of their containing structures.

Schema 9 creates a pre-migration backup. Legacy players receive one starter rifle
once, preserving their saved equipped preference. New players receive one
equipped rifle. Reconnects do not replace stored weapons. The current combined
checkout uses protocol 22 (including parallel movement work); use matching
client/server builds. Weapon/container definitions participate in content hashing.
This feature itself does not bump world generation or require fresh worlds.

## Hand and tree investigation

The 09:00 gate-pair comparison reproduced large triangular arm shading with the
old geometric receiver offsets. Smooth skinned receiver offsets remove those
facets; PCF depth correction continues to use the actual triangle plane.
Static-only and dynamic-only views isolate the remaining artifacts to dynamic
shadows from the separately posed world character. A dedicated 1024² near-field
dynamic map replaces that body with the actual hand/rifle pose for viewmodel
receivers. Other dynamic casters and existing static world shadows remain active.
The world character still casts its normal world shadow. The extra depth target
uses 4 MiB and is released with the scene.

The shared skeletal preparation path repairs arm normals for both cooked and
loose packages. A loose-asset gate-pair run also passed and rendered the corrected
arms (`20261110-221126-506-gate-pair`). Its vegetation was not used for the cooked
grass performance comparison. The first loose run lacked the isolated output's
vegetation-model link; after adding that link the run completed normally.

Deferred surface diagnostics now use harness `aoView` (see TESTING.md), replacing
`--hand-view`. Historical pose/shadow inspection switches include
`--hand-pose=idle|aim|walk|reload|holster`, and `--hand-shadow-baseline` in smoke
captures. The baseline selects the previous geometric offset/body shadow path.

The following historical canopy captures predate deferred lighting; current
diagnostics use harness `aoView`, and baked ambient visibility is no longer used. All three
winter spruces and winter birch were captured from opposite directions under
low-angle sun, including wind-on and wind-off cases. Spruce 1 also has overhead
component captures. These renders do **not** demonstrate a global inverted
sky/ground term. No speculative color rebake, normal flip, or ambient weighting
change was applied. The reported winter-tree inversion remains unconfirmed.

## Measured grass cost

AMD Radeon RX 9070 XT, driver 32.0.23017.1001; Performance quality, same standing
grass scene, fixed noon light, VSync off, 15 seconds after warmup. Values are
median / p95 milliseconds. These are local measurements, not a hardware-wide
performance guarantee. Other development work was active on the desktop.

| Resolution/path | Frame time | Total GPU | Grass GPU |
| --- | --- | --- | --- |
| 1920×1080 Mesh | 2.741 / 2.838 | 2.518 / 2.594 | 0.869 / 0.915 |
| 1920×1080 Hybrid | 6.324 / 6.792 | 6.110 / 6.526 | 4.447 / 4.746 |
| 2560×1440 Mesh | 4.411 / 4.591 | 4.057 / 4.250 | 1.261 / 1.318 |
| 2560×1440 Hybrid | 10.046 / 10.180 | 9.760 / 9.927 | 6.941 / 7.142 |

Hybrid used 33.1 MiB of reported environment memory versus 3.9 MiB for Mesh,
including 20.2 MiB of shared volume resources; 400 patches were visible. These
final-build measurements include the additional hand shadow pass, with both
render paths measured at the same framebuffer sizes. Hybrid is denser and
more expensive; no performance improvement is claimed.

The 1440p captures use the smoke-only `--exact-resolution` render target so Windows
window decorations cannot reduce the actual render height. Earlier clamped-size
trial runs are retained but excluded from this table.

## Validation and evidence

- Release client/server builds passed. The test build has one existing nullable
  warning in GalaxyChecks; no build errors.
- 25 weapon persistence/rule checks passed: capacity, unique IDs, all slots,
  stale/duplicate requests, simultaneous withdrawal, equipped deposits, removal,
  restart, backed-up v8 migration, and injected transaction rollback/conservation.
- Real two-client server checks passed: replication, forged equip rejection,
  shared views, competing withdrawals, LOS revocation, remote-access rejection,
  and authenticated reconnect preserving exact slots/equipment.
- 76 vegetation/environment/asset-loading checks passed, including deterministic
  terrain surfaces, slopes, hard exclusions, quality transitions, cancellation,
  and cooked-read accounting. A complete rebake was byte-for-byte reproducible;
  Python validation passed density mip, occupancy, dimensions and root checks.
- 11,559 UI layout checks passed, covering inventory hit targets from 960×600
  upward and layout DPI factors. Forty offscreen screen captures include the
  inventory at 960×600, 1920×1080, 2560×1440 and ultrawide.
- Standing, low-angle, distant and backlit grass captures cover all qualities.
  Winter component/species captures and hand pose comparisons are retained.
- A real two-client recording shows one player depositing a rifle and the other
  taking it; the depositor returns to gameplay with empty hands.

Evidence lives under `artifacts/validation/hybrid-weapons/`:

- `storage-multiplayer.mp4`: side-by-side two-client recording, 17.875 seconds.
- `storage-recording-final/`: original frames and client/server logs.
- `capture-matrix.json`: names and absolute paths for the 33-case render matrix.
- `hand-final.txt`: the final gate-pair hand poses with the dedicated shadow pass.
- `performance-final.json`: final paired benchmark directories.
- `weapon-network-final.log`, `weapons-final.log`, `vegetation-final.log`.
- `ui-final/`: screen previews; `ui-final.log`: rendering resource counts.

Primary performance/capture directories under `artifacts/graphics-lab/`:

| Evidence | Directory |
| --- | --- |
| Exact 1080p Hybrid | `20261110-221252-029-graphics-lab` |
| Exact 1080p Mesh | `20261110-221221-229-graphics-lab` |
| Exact 1440p Mesh | `20261110-221322-398-graphics-lab` |
| Exact 1440p Hybrid | `20261110-221352-021-graphics-lab` |
| Geometric-offset hand baseline, 09:00 | `20261110-215748-293-gate-pair` |
| Smooth-offset hand, 09:00 | `20261110-215732-583-gate-pair` |
| Dynamic-only residual before dedicated pass | `20261110-220312-597-gate-pair` |
| Static-only isolation | `20261110-220326-132-gate-pair` |
| Dedicated dynamic pass, idle | `20261110-220736-790-gate-pair` |
| Dedicated dynamic pass, aim | `20261110-220759-793-gate-pair` |
| Dedicated dynamic pass, walk | `20261110-220822-139-gate-pair` |
| Dedicated dynamic pass, reload | `20261110-220844-864-gate-pair` |
| Dedicated dynamic pass, holster | `20261110-220907-215-gate-pair` |
| Dedicated dynamic pass, afternoon | `20261110-220929-978-gate-pair` |
| Fully loaded High backlit grass | `20261110-220339-493-graphics-lab` |

Validation limits: no manual graphical walkthrough, no full live-server restart
or cross-gate storage transaction test, no exhaustive camera/sun/wind/gate-light
matrix for every tree species, and no physical Windows DPI setting sweep. Database
restart and real reconnect were tested separately. Winter inversion is not claimed
fixed. The render matrix is evidence for sampled cases, not every possible pose,
terrain boundary, or camera angle.

Integration fixes found during validation: empty loose-mesh base bounds are no
longer included as infinities, and the Windows native build script selects the
new `bin/Release/joltc.dll` explicitly instead of potentially copying a stale
recursive-search result. The packaged native library was rebuilt against the
current parallel movement ABI. These do not change weapon or grass save data.
