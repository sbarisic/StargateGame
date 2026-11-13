# Source-derived tree inspection

Normal worlds now use the volume canopy path by default (`--trees volume`).
`--trees mesh` retains the previous renderer for comparison. Scenery IDs,
planting positions, scales, collision meshes, terrain and saves are unchanged.
The historical inspection work below remains available in `graphics-lab`.


## Canopy visibility and lighting controls

The harness `settings` action accepts three independent booleans, also reported
in rendering observations and capture metadata:

| Setting | Default | Behavior |
| --- | --- | --- |
| `canopyDepthCulling` | true | Copy opaque depth, reject hidden crowns and clip rays at opaque surfaces. |
| `canopyLightLod` | false | Blend fixed 24/12/8/6-sample sunlight estimates with distance. |
| `canopyHierarchicalSkip` | false | Extend local empty steps with bounded coarse-to-fine occupancy queries. |

Disable all three for the performance comparison baseline. It retains balanced
visibility LOD; it is **not** the full-detail `CanopyLodBaseline` inspection mode.
Placement, collision, shadow proxies, palettes and ambient lighting are unchanged.
No new volume textures, bakes, impostors or batching are required.

Opaque geometry finishes before canopy rendering. The existing depth scratch target
receives a copy; canopies never sample their active depth attachment. Sampling
planes, spacing and visibility mips use the original ray interval, including the
quadrature position of the last clipped segment. Wind-expanded bounds supply a
conservative nearest depth for front-to-back draws. Only the canopy shader declares
`depth_greater`, on OpenGL 4.2 or `GL_ARB_conservative_depth`; near-plane crossings
and camera-inside views use near depth. Actual leaf depth and discard remain enabled,
without forced early fragment tests. Unsupported hardware retains explicit clipping
and ordinary depth writes. Diagnostics report capability and active use. See the
[Khronos contract](https://registry.khronos.org/OpenGL/extensions/ARB/ARB_conservative_depth.txt).

Light LOD uses crown height at the actual render-target resolution, with thresholds
256/112/40 pixels and total 15% transition bands. Transitions evaluate two fixed
quadratures and blend transmission; outside them only one estimate runs. Nearby
crowns retain 24 samples. Lighting mips are clamped to the resident chain.
`lightCounts` reports primary 24/12/8/6 draw counts followed by the number of
blended draws. Small distant shading differences are intentional.

Hierarchy uses integer fetches from the existing maximum-occupancy pyramid. Only
exact zero permits skipping. The empty node interior excludes filtering footprints
of both density mips; a wind-derivative bound limits advancement. At most three
levels are probed, once per eight integration steps after the cheap empty test.
Failure to prove a larger empty interval falls back to ordinary traversal. Skips
preserve integration-plane alignment; exhausting queries never truncates the ray.

### Reproducible comparison

```powershell
./scripts/Test.ps1 check -Group canopy-lod
./scripts/Test.ps1 run -Scenario sandbox -Recipe canopy-performance
```

The recipe reuses the forest bookmark and prepared build/cache. It fixes 1920x1080,
Performance quality, noon, weather off and wind animation time. Harness clients use
VSync off. Each mode waits for capture readiness, warms for five seconds, then samples
eight seconds. Baseline and combined captures occur outside timing windows.
`canopy-performance.json` records completed GPU-query samples as median/p95/max for
canopy, depth copy, their sum, GPU pass total, render CPU, frame interval, draws and
visibility LOD counts. GPU pass total sums instrumented passes; it is not presentation
latency. Missing/in-flight query frames are not replaced by stale values. Captures
are evidence, not a visual assertion pass.

Measured on AMD Radeon RX 9070 XT (driver 32.0.23017.1001), 2026-11-13, 92 canopy draws:

| Mode | Canopy + copy median / p95 (ms) | GPU pass median (ms) | Frame median (ms) |
| --- | --- | --- | --- |
| Baseline | 2.55 / 2.60 | 20.69 | 20.83 |
| Depth only | 1.72 / 1.91 | 19.94 | 20.11 |
| Hierarchy only | 2.70 / 2.90 | 20.89 | 21.09 |
| Lighting only | 2.41 / 2.59 | 20.66 | 20.85 |
| All three | 1.93 / 2.13 | 20.07 | 20.24 |

Depth copy was approximately 0.009 ms. A confirming pair measured depth-only at
1.733 ms versus depth plus light LOD at 1.738 ms. Depth rejection is enabled by
default (about 33% less canopy work and 3.5% less frame time here). Hierarchy is a
net cost in this forest; light LOD adds no repeatable benefit once depth rejection
is active. Both remain opt-in. These are scene-specific results, not an FPS promise.

A second view exercised 16 two-estimate transitions among 111 crowns (54 using
24-sample primary estimates, 57 using 12). Canopy plus copy measured 1.576/1.625 ms
median/p95 with 24 samples versus 1.562/1.697 ms with light LOD. The small median
difference was within noise and the transition-mode p95 was higher; this also
supports leaving light LOD opt-in.

Validation covered 20 projection/LOD/depth checks and 1,152 dense-reference rays
through empty, sparse, thin and boundary volumes, fractional mips, reduced resident
chains and wind, with no false-empty skips or missed hits. Canopy asset checks
passed. Paired near, inside, below and low-sun captures retained silhouettes and
near lighting (99th-percentile channel difference zero); branches occluded leaves
correctly. Bounded movement with live wind completed. Static captures and CPU checks
do not certify temporal stability on all drivers. The unsupported-hardware path
has not been exercised on a GPU lacking conservative depth. Extreme close-up volume
contours remain as before.

## Normal-world integration

`assets/vegetation/world-canopies.json` maps all ten leafy render variants to
their own baked canopy and wood models. Summer and winter birch are separate;
all three winter spruces retain their original textures. `Broadleaf_Desktop`
is a bare wood model and intentionally has no generated foliage.

All visible crowns use the volume shader, hemisphere canopy AO and normal scene
AO, including distant trees. Loading crowns show their available wood until the
complete volume is ready; they never fall back to old leaf cards. Source-derived
trunks are used where available; other species
retain prepared wood. The graphics-lab 1.6x enlargement is never applied to saved
tree placements. Rest-pose meshes still supply shadows, shelter and collision.

World uploads select complete mip chains capped at 128 voxels per axis for
Performance/High and 64 for Low. Memory pressure selects smaller chains down to
32 rather than rejecting a species. Requested crowns remain resident until the
world is cleared. A typical 128-cube canopy uses 18.9 MiB including AO, shared
across its instances. Explicit inspection retains the full-resolution bake.
The debug HUD and streaming snapshot count custom-volume and legacy-leaf draws.
The gate-pair smoke test in `artifacts/asset-packs/all-volume-trees` completed
with 83 visible volume trees, zero legacy leaf trees and no pending canopies;
Low, High and Performance screenshots were captured during the run.

Prepare variants with `python tools/cook_world_canopies.py`; use `--rebuild`
after changing prepared source meshes. The original four bakes retain their
source-specific cooking commands below. Release publishing prepares the registry
before packaging. Global AO follows Auto quality on every world; an explicit
saved Off remains respected.

## What was added

- The original Nicholas-3D tree supplies a 404,537-triangle wood source and
  421,116 leaf triangles. Three inspection trunks retain 40,000, 10,000 and
  2,999 triangles. Larger connected branch groups are retained before reduction.
- The source leaves and their alpha texture are sampled into a 256³ canopy.
  Colour/occupancy and normal volumes include mips, conservative occupancy for
  empty-space skipping, and colour padding around empty voxels.
- The fragment shader marches the canopy, writes the first visible leaf depth,
  and uses scene sunlight, shadow maps, cloud attenuation, gate lighting and
  deferred atmospheric transport. It adds approximate self-shadowing and leaf
  transmission. Wind follows the same broad gust as mesh trees.
- The mesh crown remains the conservative rest-pose shadow/shelter proxy. The
  volume does not create collision or change physical bounds.
- Uploads are sliced through the existing model budget. The volume is loaded
  only on request and released when the location changes. The shader is cached
  for the renderer lifetime. Cooked packages include independently checked blocks.

The original bark maps are 1024². The prototype preserves their UVs, normal and
metallic/roughness maps; it does not invent higher-resolution bark detail or bake
a new high-poly normal map. Original files and CC-BY attribution remain intact.

## Inspect

Start the packaged server with:

```powershell
.\StargateGame.Server.exe --bind 127.0.0.1 --port 47881 --fixture graphics-lab --allow-test-identities
```

Start the client with:

```powershell
.\StargateGame.Client.exe --host 127.0.0.1 --port 47881 --fixture graphics-lab --identity-profile tree-inspection --tree-prototype volume --view tree-prototype --time-of-day 12 --visual-time 0 --audio off
```

Use `--tree-prototype current`, `mesh`, or `volume` to compare the existing tree,
the improved trunk with mesh leaves, and the improved trunk with a volume canopy.
`off` is the default. Inspection views include `tree-prototype-far`,
`tree-prototype-below`, `tree-prototype-above`, `tree-prototype-back` and
`tree-prototype-wind` (moving camera). Deferred diagnostics now use the harness
`aoView` setting; see TESTING.md. In smoke captures, `--tree-occlusion`
places render-only crates before and behind the crown. It never moves physics bodies.
The normal F3 model memory total includes the canopy allocation.

## Reproduce the assets

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b --python tools/extract_tree_prototype.py -- --output 'D:\Games\Stargate Network\BuildCache\tree-volume'
python tools/cook_tree_prototype.py --source 'D:\Games\Stargate Network\BuildCache\tree-volume'
python tools/test_tree_prototype.py
```

The extraction records source hashes and actual triangle counts. The bake checks
the extraction hashes, dimensions and 160 MiB resource cap. The original planting
origin is retained. `cook_assets.py` includes the descriptor and referenced slices;
it does not include unrelated extraction staging files.

## Volume lighting correction (descriptor version 2)

Leaf normals are two-sided axes and no longer flip toward the camera. This fixes
upper-crown shading changing when the camera moves below the tree. Wind applies
the inverse-transpose deformation to normals and expands the ray bounds.

The two RGBA8 textures retain their allocation. Colour alpha holds conservative
maximum occupancy for empty-space skipping; normal alpha now stores optical
density as opacity at an explicit reference distance. Mips average optical depth
and weight linear colour and normalized normals by density. Attribute padding
fills colour fringes without adding occupancy or density.

Camera rays integrate extinction by distance and refine the threshold hit within
the final step. Parallel slab rays preserve their sign. Sun and upward sky rays
sample the whole remaining crown with footprint-dependent density mips. The
initial settings use 24 sunlight and 12 sky samples. `TreeVolumeSettings` groups
step length, visibility threshold, light counts and transmission strength.

This remains an approximate opaque leaf surface, not a physically exact volumetric
multiple-scattering solution. The mesh proxy still supplies conservative cached
shadows/shelter; wind can differ slightly from that rest-pose proxy.

## Findings and limits

The volume gives a fuller crown, but leaf-sized blocks and overly dark interior
areas remain visible in the near view. It is not approved as the gameplay forest
renderer. Keep the mesh path until the appearance is reviewed. A single shared
species volume costs **146.3 MiB** of GPU texture memory; the three trunks add
geometry memory. The packed model archive grew from roughly 1,586 to 1,594 MiB.

One short paired 1920×1080 Performance comparison, VSync off, used a fixed noon
camera, 20-second warmup and eight seconds of samples:

| Inspection path | Frame p95 | Measured GPU p95 | Largest frame |
|---|---:|---:|---:|
| Current mesh | 2.796 ms | 1.889 ms | 130.601 ms |
| Volume canopy | 2.553 ms | 2.089 ms | 127.836 ms |

The runs include screenshot capture. The small frame-time reversal is noise,
not evidence that the volume is faster. This isolated tree comparison does not
establish a forest budget or the general 8.33 ms target. Those measurements precede the version 2 whole-crown lighting correction and are
retained only as historical evidence.

Evidence is retained in `artifacts/review-evidence/trees`. The prototype still
needs appearance review, moving-camera silhouette assessment, multiple-species
memory planning, and night/backlight inspection before any production decision.

Version 2 validation used the packaged client. Above/below noon, opposite-side
morning sun, foreground/background depth and moving-camera wind captures are in
`artifacts/review-evidence/tree-volume-v2`. The wind sequence starts after the
volume reports ready; `wind/canopy-motion.mp4` retains the short movement check.

One final 1920x1080 Performance, VSync-off run used 20 seconds warmup and eight
seconds sampling: frame p95 **2.355 ms**, total measured GPU p95 **2.175 ms**,
CPU render p95 **1.223 ms**. The largest sampled frame was **124.220 ms**; the
run includes PNG capture. These are total inspection-scene costs, not isolated
canopy timings or evidence of forest-scale performance. No repeated performance
sweep was run. The canopy allocation remains **146.3 MiB**, and the archive is
**1594.4 MiB** with 7,302 verified blocks.

## Lower-canopy fill and mature-tree scale

The inspection tree now uses a shared 1.6x uniform scale, roughly 14.9 m tall.
Trunk, crown, shadow/shelter proxy and projected LOD height use the same scale.
Voxel spacing and optical reference distance scale together, so enlargement does
not make the leaves optically thicker. Wind retains its world-space amplitude.
Most crown coverage is above head height; low outer twigs are still retained.
This is a visual prototype, not a change to gameplay scenery or collision.

Two-sided canopy ambient light now mixes 35% ground fill with 65% sky fill.
Occluded sky retains 45% fill, brightening shaded lower leaves while preserving
sun shadows and the existing scene lighting/exposure. These controls are in
`TreeVolumeSettings`. The `tree-prototype-scale` view adds a standing player-sized
render reference during smoke captures.

The earlier timing measurements precede this appearance adjustment. No new
performance sweep was run. The packaged Release visual check is retained in
`artifacts/review-evidence/tree-volume-fill-scale`.

## Additional model comparisons

Choose a model with `--tree-species broadleaf|broadleaf-2|birch|spruce` alongside
`--tree-prototype current|mesh|volume`. The original `broadleaf` remains the default.
Use `--view tree-prototype-scale` for a standing-player size reference in a smoke
capture. Species share the brighter lower-canopy lighting and distance-based
opacity, but retain their own source crown, bark, materials and bounds.

| Selector | Input | Wood | Scale | Volume allocation |
|---|---|---|---|---|
| broadleaf | Original MoreRealisticTree_01 source | Extracted 40k/10k/3k | 1.6x | 146.3 MiB |
| broadleaf-2 | Original MoreRealisticTree_02 source | Extracted 40k/10k/3k | 1.6x | 146.3 MiB |
| birch | Prepared summer white birch | Existing wood LODs | 1x, already about 17.5 m | 146.3 MiB |
| spruce | Prepared blue spruce, winter atlas | Existing wood | 1x, already about 19 m | 36.6 MiB |

Only the selected volume is resident. Switching species releases its texture
allocation and invalidates cached shadows/shelter. Shared mesh resources retain
the renderer's existing cache policy. These options do not replace normal-world
scenery, colliders or winter/summer selection.

Reproduce the additional bakes:

```powershell
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b --python tools/extract_tree_prototype.py -- --tree MoreRealisticTree_02 --output 'D:\Games\Stargate Network\BuildCache\tree-volume-broadleaf-2'
python tools/cook_tree_prototype.py --source 'D:\Games\Stargate Network\BuildCache\tree-volume-broadleaf-2' --model MoreRealisticTree_02 --id broadleaf-2
python tools/cook_tree_prototype.py --model European_White_Birch_Hero_Winter-summer --id birch
python tools/cook_tree_prototype.py --model BlueSpruce_Desktop_Winter --id spruce
```

Prepared-model bakes sample every leaf material with its own alpha cutoff and
wrap/clamp rule. Large branch cards receive more samples according to their area;
all sampling remains deterministic. Source hashes and existing attribution are
recorded in each descriptor. Spruce uses its supplied winter foliage texture;
this experiment does not reconstruct a summer needle atlas.

The first comparisons showed that branch-card sources become too solid at the
individual-leaf optical density. Birch uses a 0.55 density multiplier and spruce
0.35, applied consistently to camera, sun and sky integration. Broadleaf density
is unchanged. Conservative occupancy remains independent of this multiplier.

### Comparison findings

Packaged, fixed-noon mesh/volume captures are in
`artifacts/review-evidence/tree-species`. The second broadleaf is the strongest
candidate: its source-derived leaf volume fills the crown while retaining the
separate branch groups. Birch remains too dark and dense for approval; its mesh
reference is also dark, and lower optical density alone does not solve that.
Spruce retains its conical outline and winter colouring but loses too much fine
needle detail and looks soft at this voxel resolution. Keep its mesh foliage for
normal play. No normal-world replacement is enabled by these experiments.

The final birch and spruce captures use `birch-volume-tuned` and
`spruce-volume-tuned`; the first captures are retained for comparison. The
broadleaf capture is `broadleaf-2-volume`. Corresponding `*-mesh` folders provide
the same camera and scale references.

## Randomized inspection forest

`--tree-forest` enables a mixed grove in `graphics-lab` (volume mode by default).
Seed 7319 creates 66 trees across roughly 70 metres. Species, position jitter,
yaw and 80�120% size variation come from a stable hash. A winding walking gap and
the existing gate clearing stay open. Use `--tree-forest-seed NUMBER` for another
repeatable arrangement. `--tree-prototype mesh` compares the same forest layout.

All four canopy volumes are shared across instances: **475.4 MiB** total texture
allocation, not one copy per tree. Loading still uses the normal upload budget.
Leaving the fixture or changing the layout releases the volumes. Trees use
transformed crown bounds, frustum rejection and a projected rectangle; rays,
lighting, wind and hit depth use the corresponding tree/world transforms.
The existing mesh crowns remain shadow/shelter proxies.

Camera views are `tree-forest-inside`, `tree-forest-near`, `tree-forest-above`,
`tree-forest-far` and `tree-forest-walk`. Inspection cameras hide the weapon.
Use `--view player` for normal controls and walk north from the fixture spawn.
This is a render-only grove: it adds no tree collision or persistent placements.

For example, with the graphics-lab server from the instructions above:

```powershell
.\StargateGame.Client.exe --host 127.0.0.1 --port 47881 --fixture graphics-lab --identity-profile forest-inspection --tree-forest --tree-forest-seed 7319 --view tree-forest-inside --time-of-day 12 --audio off
```

For reproducible evidence, `--tree-forest-tour --smoke-seconds 75 --capture-only`
waits for all species, captures above/near/inside/far, then records 80 JPEG frames
of an eight-second walkthrough. Set `--capture-dir PATH` and `--visual-time 0`
to keep lighting/wind fixed while the camera moves. No terrain regeneration is
required; the normal forest renderer is unchanged.

Validation: Release Windows client/server packages were refreshed and all 9,871
cooked asset blocks verified. The packaged forest tour completed successfully
with empty error logs. `artifacts/review-evidence/volume-forest-final` contains
four 1920x1080 views and an eight-second, 80-frame walkthrough. Close foliage
remains chunky and spruce remains soft; these captures are visual inspection,
not production approval or a forest performance benchmark. The far views also
show the bounded graphics-lab terrain rather than a complete planet.

## Directional ambient visibility

Version 3 assets retain their precomputed upper/lower hemisphere channels on disk.
The deferred renderer no longer uploads or samples these ambient fields. Visible
canopy depth/normals enter the shared screen-space AO stage, and AO Off bypasses
all surface occlusion. Ground-fill weight remains 35%; directional sunlight
extinction and proxy shadows are preserved. The former tree-volume CLI diagnostic
views are replaced by the harness surface views documented in TESTING.md.

Historical evidence below predates this migration:

AO review evidence: `artifacts/review-evidence/ambient-occlusion/forest-on`
contains the lit underside; `forest-map` contains diagnostic views and
`ao-walk.mp4`. See `VALIDATION.md` for the focused tests and timing limits.

## Balanced canopy LOD and canonical grass (2026-11-10)

Normal world rendering now uses `--canopy-lod balanced`. Use
`--canopy-lod baseline` for the previous full-detail sampling budget. This switch
changes canopy sampling only; it does not change grass materials, tree placement,
trunks, collision, texture residency or quality settings. Both modes use the same
conservative occupancy optimization.

The CPU projects all eight corners of the wind-padded crown bounds into the
actual viewport. Camera/near-plane intersections retain full detail. Each crown
is submitted once; smoothstep blending spans 15% around each threshold.

| Crown height | Resident-relative mip | Maximum primary steps | Sun samples |
|---|---:|---:|---:|
| 256 px and above | 0 | 1536 | 24 |
| 112-256 px | 1 | 384 | 24 |
| 40-112 px | 2 | 128 | 24 |
| Below 40 px | 3 | 64 | 24 |

The primary step covers the complete ray interval within its budget. Optical
extinction is integrated over physical segment length with the existing opacity
threshold. Colour, normal and density use compatible mips. Empty-space skipping
uses the separate max-occupancy pyramid and a wind-stretch bound. Dynamic sun
rays, shadow visibility, ambient visibility and wind remain active. Sun rays
retain the configured count (24 by default) at every distance to avoid shifting
lighting samples when the camera crosses a LOD band. Primary integration planes
are anchored in tree space and occupancy skips preserve their alignment. Pixel
footprints also filter distant density and ambient visibility.

The debug HUD and `CANOPY` benchmark line report four LOD counts, actual crown
draws, asynchronous GPU milliseconds, CPU submission milliseconds and tracked
model GPU memory. The existing GPU query ring handles canopy timing without
blocking readback. Shared shader state and shadow bindings are set once per pass;
LOD parameters use one vector uniform per crown. This does not batch distant trees.

Both temperate grass slots now select `rocky-rugged-terrain-pbr`, at 2 metres and
0.16 metres of visual parallax. Their weights are combined before selection.
The scanned understory, meadow normal texture and procedural meadow normal
perturbation no longer modify the ground. Other PBR surfaces, ecological
transitions, weather wetness and procedural blades remain. The eight palette
slots and saved terrain semantics are unchanged. Importer defaults preserve
this selection; source textures and licenses remain available.

Changed shaders and palette descriptors are published in
`artifacts/cooked/dev-override/`. Existing base packs and canopy bakes were reused.
Start with `--dev-assets artifacts/cooked/dev-override` (the paired Visual Studio
profiles already do this), or use the existing clean release publisher for
redistribution. The overlay is not a standalone release pack.

### Measurements and evidence

Matched Release runs: RX 9070 XT, driver 32.0.23017.1001, 1920x1080,
Performance, vsync off, grove seed 7319, noon, visual time 25, 12-second warmup,
15-second measurement. Each view draws the same 66 crowns and tracks 521.4 MiB
of model GPU allocations in both modes. The inspection grove retains its 256-cubed
source mip residency; normal world residency limits are unchanged.

| View and mode | Canopy GPU mean/p95 ms | Frame mean/p95 ms | Canopy CPU mean/p95 ms |
|---|---:|---:|---:|
| Near baseline | 4.554 / 4.657 | 6.970 / 7.001 | 0.336 / 0.442 |
| Near balanced | 4.259 / 4.352 | 6.723 / 6.706 | 0.363 / 0.452 |
| Medium/distant baseline | 1.124 / 1.259 | 2.397 / 3.194 | 0.342 / 0.433 |
| Medium/distant balanced | 0.475 / 0.647 | 2.250 / 2.939 | 0.331 / 0.435 |

The medium/distant canopy measurement improved 57.7%, exceeding the 20% target
for this fixture. Near canopy time improved 6.5%. Total frame mean and p95 also
improved in these matched views. These are measured pass costs, not a promised
whole-game FPS gain. Screenshot readback occurs in the measurement window and
can inflate maximum frame times; raw maxima are retained in the logs. Memory
figures are renderer allocation accounting, not total driver VRAM.

`artifacts/canopy-lod/results.json` contains raw metrics; `comparison-near.png`
and `comparison-far.png` show baseline and Balanced side by side. The `delivery-*`
folders contain the delivered implementation measurements. Earlier folders retain
an intermediate near-view regression that was fixed by retaining conservative
coarse-mip empty-space skipping. They are not the delivered implementation's
performance results.

The tiny-crown horizon view (42 LOD2 and 24 LOD3 crowns) measured canopy GPU
0.730 -> 0.245 ms and submission CPU 0.403 -> 0.412 ms. Frame mean was
2.492 -> 2.570 ms while p95 was 3.308 -> 3.273 ms. This view is CPU/submission
limited: GPU savings did not improve total frame mean. Earlier runs had a larger
CPU regression; shared shader state is now set once per pass and the four LOD
parameters are sent in one uniform. The broad no-regression acceptance claim
is therefore limited to the matched near and medium/distant views, not every
possible camera position.

All ten source canopy variants have individual inspection captures in `species-*`.
`walk-wind` contains a moving-camera run and a wind sequence; `rocky-grass-final`
contains the final grass close-up and GPU material check. Generated-world
captures use normal 128-cubed resident canopies: `world-forest-balanced` reports
228 crown draws, 4.355 ms mean canopy GPU time and 510.1 MiB tracked model memory.
The generated-world baseline attempts returned without a final timing summary,
so they are excluded from performance comparisons. Complete benchmark logs,
including less favourable runs, remain in the evidence folder.

Focused validation: 13 canopy-LOD checks, 33 palette checks, 7 mounted/loose
palette checks, and the canopy density/occupancy/mip/ambient validation scripts.
The full landscape validator also passes all 26 materials, three palettes and
11 existing blade/fern/bush meshes. Projection tests cover camera/near-plane intersections, distance, viewport size,
resident mip limits, continuous selection and full interval traversal. Mounted
palette tests compare all biome metadata and read every packed mip; both grass
slots resolve the same cooked payloads. Release and Debug builds pass.

To reproduce the controlled grove, launch a `graphics-lab` fixture server with
`--allow-test-identities`, then run the Release client twice, changing only the
canopy mode:

```powershell
dotnet src/StargateGame.Client/bin/Release/net10.0-windows10.0.17763.0/StargateGame.Client.dll --connect --port 18779 --identity-profile canopy-lod-bench --fixture graphics-lab --tree-forest --tree-forest-seed 7319 --view tree-forest-far --canopy-lod balanced --time-of-day 12 --visual-time 25 --audio off --vsync off --graphics performance --benchmark-warmup 12 --benchmark-seconds 15 --dev-assets artifacts/cooked/dev-override --capture-dir artifacts/canopy-lod/reproduction-balanced
```

Use `tree-forest-near`, `tree-forest-horizon` or `tree-forest-walk` for other
views. Omit `--visual-time` to inspect moving wind. Inspection species accept
both the original aliases and source IDs from the world canopy manifest.
