# Hybrid grass, hand shadows, and weapon storage

Implementation and validation snapshot: 10 November 2026. Parallel movement,
water, and asset-loading work remains in this checkout. No user save was deleted
or regenerated for this change. The validation programs used isolated build
directories and temporary saves.

## Grass

Hybrid is the default. Use `--grass-renderer=mesh|hybrid`, or H / the Grass control
in the pause menu, to switch. `--grass=off` still disables grass. The existing mesh
renderer remains active until volume assets and shaders are ready.

`tools/bake_grass_volume.py` produces four deterministic variants from the current
blade profile and grass atlas. Each variant has a matching nearby mesh, a
128 × 32 × 128 density/color/normal/occupancy hierarchy, and cached hemisphere
visibility. Its footprint is 2 × 2 m and vertical domain is 0.5 m; actual blades
retain the 0.10–0.28 m height range. Fullness comes from coverage and thickness.

The renderer retains 8 m streaming tiles and existing placement/ecology masks.
Each tile holds sixteen patches and a terrain-height, coverage, material, and
hard-exclusion surface. Conservative exclusion sampling prevents interpolation
from filling prohibited areas. Both paths use the same variant and world-space
transition mask, terrain-relative roots, and root-weighted wind.

| Quality | Mesh/volume transition | Hybrid range | Mesh-only range |
| --- | --- | --- | --- |
| Low | 6–10 m | 128 m | 64 m |
| Performance (Medium) | 10–14 m | 224 m | 112 m |
| High | 14–18 m | 320 m | 160 m |

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

Inspection switches include `--hand-view=normals|mapped-normals|static|dynamic`,
`--hand-pose=idle|aim|walk|reload|holster`, and `--hand-shadow-baseline` in smoke
captures. The baseline selects the previous geometric offset/body shadow path.

Winter canopy diagnostics now include `--tree-volume-view=albedo|shadow`, in
addition to normals, sun transmission, sky and ground visibility. All three
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
