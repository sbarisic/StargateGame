# Planet terrain and streaming

The server generates immutable terrain. Clients receive canonical samples and
use them for rendering and Jolt collision; they do not need to reproduce noise.
One planet is active per session. Its identity is independent of future gate addresses.

## Coordinates and generation

Maps cover [-512, 512] metres in X/Z with Y up. Chunk coordinates are [-8, 7]
on each axis. Each 64 m chunk has 65 square-grid vertices, plus a one-sample
halo for normals. Heights and water levels are integer millimetres. Terrain
uses the same two triangle diagonals for interpolation, fine rendering, and collision.

`PlanetPlan` constructs a 257-square regional grid at 4 m spacing. Domain-warped
noise, ridges, broad valleys, and preset shaping produce the regional elevation.
A priority flood resolves depressions, assigns an acyclic downstream tree to
boundary outlets, and accumulates flow in reverse order. Ties use stable cell indices.
Detailed samples use ridged/warped noise and one bounded thermal relaxation pass
with a 35-degree talus and a 0.35 m maximum adjustment. Every sample uses the
same world-coordinate neighbor stencil; no step depends on which chunk ran first.

Channels follow regional downstream segments. Channel depth is 0.35 m with
smooth banks; the encoded maximum is 0.45 m. Desert channels are dry. Water is
a separate, opaque, shaded surface with no physical fluid simulation. This is a
small runtime drainage model, not a replacement for Gaea's erosion toolset.

The gate site is selected near the centre by dryness, local relief, and distance.
It has a filled 22 m diameter interior at median elevation, an 8 m quintic blend,
and a reserved southern approach. The gate, DHD, spawns, and crates are placed
relative to that site. The gate is scaled to 6.7056 m across and embedded about 1.37 m so its base and collision sit
below the ground, leaving a walkable opening. There is no circular raised rim. The seed corpus checks
flat, dry spawns and a walkable approach, including extreme and negative seeds.

Generator version 6, settlement settings/template hash, seed, preset, dimensions, sampling, and quantization define
the settings hash. Change the generator version when changing canonical output.

## LayerProcGen integration

`StargateGame.WorldGen` has no FishGfx or Jolt dependency. The upstream framework
is pinned to `7fb6100ddcffa79ff993d4abbfb61602501cfcc8`; FastNoiseLite C# is v1.1.1
at `7ccfbc16eb1c932568f177d63a9ba51d89bbe516`.

`GenerationService` owns top-level dependencies and isolates planets within the
framework's singleton manager using separate coordinate ranges. A regional layer
provides a whole-planet plan to the chunk layer. Four independent mesh layers
produce 1/2/4/8 m meshes. Independent layers avoid the upstream multilevel layer's
implicit adjacent-chunk dependencies requesting client data before it arrives.
Dependencies are released after use; the last service unloads the manager.
Framework profiling output is disabled by the application; errors retain upstream
console logging. The small vendored manager lifecycle patch is described in notices.

World-generation workers only produce managed data. FishGfx mesh creation and
disposal occur on the render thread. All Jolt creation, shape preparation, steps,
and topology changes occur on their simulation owner. Collision shapes are
prepared two chunks at a time before readiness is acknowledged.

## Streaming and storage

Rendering requests nearby chunks first and eventually fills the bounded horizon.
LOD spacing is 1 m within 128 m, 2 m within 256 m, 4 m within 512 m, and 8 m
beyond. Boundary cells retain every canonical metre sample and triangulate an
interior fan, giving adjacent LODs the same border polyline. Border normals come
from canonical halos. The previous mesh remains until its replacement is ready.
Uploads stop after approximately 2 ms of work; an individual upload is indivisible.
Models and textures are retained across planet switches, while terrain meshes are replaced.
Terrain surfaces use the included extracted grass, cliff, sand, and snow albedo maps,
projected in world space on three axes to avoid stretched cliffs and chunk seams.
Mipmaps and anisotropic filtering control distant shimmer; source provenance and
the reproducible import script are documented in `assets/PROVENANCE.md`.

Canonical memory storage is bounded by 256 chunks per planet. Current and pending
destination stores may coexist. The disk cache lives in the user's local application
data under `StargateGame/terrain-cache`, is capped at 1 GiB, and evicts oldest-unused
files. File keys include settings and coordinates; each payload carries a SHA-256
checksum. Invalid cache entries regenerate. Clients additionally validate cached
payloads against the authoritative manifest. Client cache writes use a bounded
background queue. Native checkpoints and mutable crate states never enter this cache.

Protocol 8 retains 64 KiB TCP frames. Manifests describe chunk coordinates,
SHA-256 hashes, and decoded lengths, capped at 80 KiB per chunk. `terrain-fetch`
requests only missing canonical chunks for a prepared topology. `terrain-request`
requests visual chunks, with a small outstanding window. Payload pieces are at
most 32 KiB and require exact offsets and hashes. Simulation messages use a
separate priority queue ahead of terrain. TCP still has head-of-line blocking;
prediction and safety boundaries handle stalled delivery rather than relying on
terrain always arriving in time. Packets carry settings identity across switches.

## Collision and epochs

The server keeps a three-chunk radius around each player, one chunk around every
crate and the gate, and prefetches an extra player ring. The union is shared with
every predicting client, including players exploring separate parts of the map.
Terrain collision uses a Jolt mesh per chunk, with capacity for all 256 chunks.
The perimeter and authored gate/DHD collision remain resident.

A preparation names the complete target set. Clients verify data, prepare shapes,
then acknowledge readiness. The server prepares its own shapes and announces an
activation tick at least 12 ticks ahead. At that tick, ordered collider changes
advance the world epoch and an immediate baseline resets prediction history.
Older snapshots cannot restore an old epoch. Client prediction stops before an
uncommitted activation. If bodies move outside a proposed removal set during
preparation, that preparation is cancelled and coverage is recomputed. Horizontal
movement stops at unsupported terrain; old support is retained until it is safe.

Player joins/departures update character topology in the existing world, preserving
prepared terrain shapes. All previous native checkpoints are invalidated. A joining
client waits through an active planet update before receiving the current manifest.

## Developer transitions and scenery

Only the first loopback client can request transitions when `--dev-planets` is set.
One operation prepares a destination while the old world continues. All clients
must be ready before activation. Cancellation leaves the old planet active.
At activation, the server records crate transforms, velocities, and sleep state,
replaces the world, resets inputs, and spawns players at the destination gate.
Inactive crate states remain in server memory without simulation. Returning to a
planet restores them. These states are lost on server shutdown.

Placement candidates use hashes of seed, category, world-space candidate cell,
and the cell's single candidate index. Neighbor priority determines spacing across
chunk boundaries. Water, steep slopes, the clearing, and the approach exclude
candidates. Trees and rocks have stable positions, yaw, and scale suggestions.
`SceneryPlacement.Resolve` applies a versioned catalogue with stable asset ordering,
density, scale, slope limits, and optional collision-proxy names. Synthetic catalogue
tests verify that asset selection does not alter heights. `RockScenery` now selects
the recovered models: mossy stones/outcrops for temperate worlds, orange sandstone
for desert worlds, and detailed stone boulders with neutral colouring and snow
cover for icy worlds. A quarter of
rock candidates select small decorative ground stones on all presets.
Original terrain samples, candidate IDs, and terrain cache keys are unchanged.

All rock transforms are resolved from server-delivered canonical chunks, including
slope alignment and shallow embedding. Full footprints must remain dry and inside
the owning chunk with a one-metre margin. This conservative ownership rule can
leave gaps near chunk edges, but prevents collider removal under neighboring bodies.
Existing candidate spacing and gate exclusions remain in force. Recovered sandstone
meshes measure about 0.2-0.4 m: explicit 4-10x landscape scales create low outcrops.
Temperate/icy stones retain near-authored sizes; decorative stones use 0.9-2.3x.

Rendering reuses shared model/texture resources, with per-instance frustum culling
in colour and static shadow passes. Chunk bounds include rock silhouettes. Larger
rock collision triangles are included in the same Jolt mesh as the owning terrain
chunk, so readiness, activation epochs, rollback invalidation, and unloading remain
atomic. Recovered lower LODs provide collision where available; other models use
their authored triangles. Tiny details have no collision. F3 includes rock collision
wireframes. The compatibility hash covers collision data, rock bounds, and placement
code; old clients cannot join with a different scenery collision set.

Rebuild runtime assets with `python tools/import_rocks.py --source <terrain_library>`
(Python, numpy, Pillow). Converted assets ship in the checkout; game launch requires
neither Python nor the original installation. See `assets/PROVENANCE.md`.

`TreeScenery` resolves the existing tree candidates independently of rock selection.
Temperate planets use the leafy tree, white birch, bare sapling group, and four
Nicholas-3D realistic trees; icy
planets use all three winter spruce variants and white birch; desert planets use
only sparse bare saplings in the generator's existing near-channel candidates.
Trees stay upright, vary in yaw/scale, and embed their roots into uneven ground.
All ten active tree models use 1.5 times their previous placement scale. Planting
depth is fixed in world metres below the ground directly at the trunk: 1.5 m for
the recovered leafy tree, 0.15 m for RealisticTree_01/02, and 0.10 m for the other
models. Scale and neighbouring terrain heights do not change these offsets.
Rendering, bounds, and wood collision use the same transform at every LOD.
Root footprints must stay dry; crowns can cross chunk boundaries. Roots avoid collidable rocks.
Gate and approach exclusions and the original candidate density remain unchanged.

The offline `tools/import_trees.py --source <terrain_library>` importer preserves
alpha in foliage atlases and supplies wood-only collision. Imported birch/leafy
LODs switch at 80 and 180 metres; spruce and saplings retain their source meshes.
The four realistic trees use 50,000 triangles nearby, about 16,700 beyond 40 m,
8,000 beyond 90 m, and 3,000 beyond 180 m. Their shared planting pivots, textures,
and wood-only collision come from `tools/import_realistic_trees.py`. The retired
Tree Pack 3D models remain excluded from generation and runtime loading.
Visible and shadow passes use the same selected LOD, with static-shadow invalidation
on changes. Chunk bounds include all tree LODs. Cutout foliage is double-sided and
does not block movement. Wood collision is merged into the owning terrain body,
sharing its readiness, topology epoch, and disposal. Tree data/code are included
in compatibility hashes. F3 shows resident tree counts and wood wireframes.
Wind animation, seasonal changes, tree cutting, and procedural tree growth are deferred.

F4 exposes preset/seed selection and cancellation. F3 displays mesh and collider
counts, pending work, cache usage, plan generation time, received bytes, estimated
mesh GPU memory, upload time, collision epoch, and candidate markers. See
`VALIDATION.md` for measured results and remaining manual checks.

Client model/texture resources are uploaded before opening the network connection
and reused across reconnects. Disconnect clears planet meshes, LOD selections,
and shadow caches; application exit releases the shared assets. This avoids
connection idle timeouts during large texture uploads. Server ping limiting uses
a bounded wall-clock window so collision preparation cannot turn normal pings
into a simulation-tick rate violation.

## Generator 4 drainage and test props (2026-11-07)

The selected accumulation thresholds are 1,280 temperate, 1,360 icy and 1,600
desert cells, each representing 16 square metres. These were tuned against 32
fixed seeds per preset: median channel-length reductions versus the old 160-cell
rule are 64.90%, 66.96% and 70.75%, respectively. This is length, not lake count.
Headwater branches shorter than 48 m are pruned. Surviving heads taper width and
carving over 24 m. Selected flowing lake outlets remain connected; closed lakes
below their spill level do not acquire artificial outlet rivers. Lake selection
is unchanged. Moisture and vegetation query the reduced channel network.

The generator/settings hash invalidates old caches. Start a new universe with
`--universe meadow-v4`; old saves are refused and preserved. Visual Studio's
server profile uses `dev-settlements6-layout4` to avoid reopening incompatible saves.

Generated props are two 1 m / 10 kg wooden crates and two sealed 15 kg barrels
(0.726 m diameter, 1.184 m high). Their shared collider description drives Jolt,
picking and membrane slicing. Barrels use a bevelled four-ring, 32-sided convex
hull with a 1 cm convex radius. Buoyancy clips the native hull and samples its
round footprint. Imported model origins match the collider centre.


## Bounded streaming (2026-11-07)

Canonical client terrain is now bounded by graphics quality plus collision requirements. Render-only far tiles preserve distant terrain, water and scenery. Generator 6 requires a new universe and retains old save files unchanged. See [STREAMING.md](STREAMING.md) for protocol 12, resource limits and diagnostics.

## Settlement terrain (generator 6)

[Settlement planning](SETTLEMENTS.md) runs after drainage and gate selection, before
canonical heights and scenery. Road coverage is a separate byte per canonical
sample in SGC4 chunks. Grading, rendering, footsteps and vegetation exclusions
consume the same retained plan. Plot cut/fill is capped at 2 m; buildings and
roads avoid water with a 16 m bank buffer and preserve the gate approach.

## Rocky meadow and nearby grass (2026-11-09)

Temperate meadow uses Rocky Rugged Terrain as its base. The second palette slot
is a scanned-grass understory, blended into low soil between stones. The rock
height range is 16 cm and the grass overlay adds up to 3.5 cm of apparent relief.
This is parallax occlusion mapping only: terrain silhouettes, collision, planting
positions and saved worlds do not change. Explicit texture gradients and five
binary refinement steps reduce the previous stepped ray intersections. Relief
fades with distance and at grazing angles. `--terrain-parallax off` disables it.

Six non-wheat clumps from Nicholas-3D's Realistic Grass Pack replace the older
eight grass species. Existing ferns and dry bushes remain. Materials use a muted
olive grade, two-sided alpha testing, alpha-coverage mipmaps and anchored wind.
Short procedural blades fill underneath the clumps. Originals and attribution
are preserved; `python tools/import_realistic_grass.py` reproduces the import,
and `python tools/build_assets.py` cooks it for runtime use. The general foliage
importer also invokes this importer when the source pack is present.

Extra clumps concentrate within 6 m of the camera. Stable ranked subsets retain
20% of that population at 16 m, 2.5% at 32 m and 0.5% at 48 m, before quality and
range fades. Transition bands fade individual clumps rather than changing their
positions. CPU filtering uses an 8 m conservative margin around the shader's
distance policy to avoid submitting most invisible instances. Water, buildings,
gate approaches, steep ground and canopy rules still govern placement.

`scripts/Compare-TerrainRelief.ps1` captures the Rocky Rugged setting using an
owned loopback gate-pair server and isolated prebuilt outputs. It restores its
runtime manifest and stops only the server it started. Source manifests and
existing development servers remain untouched.
