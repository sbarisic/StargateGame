# Procedural settlements

Generator 6 adds ruins, villages and compact cities to the existing planet planner.
Use a new universe (`./scripts/Run-Server.ps1 -Universe settlements-v6`). Old saves
remain on disk and are rejected without conversion. Client/server protocol is 12;
11 was already used by the character work. No new native dependencies are required.

## Placement and controls

Primary outcomes are wilderness 35%, ruins 30%, village 25%, city 10%. These are
selection probabilities, not guaranteed placement rates. Sites must fit dry terrain
without more than 2 m of cut/fill. Water has a 16 m buffer and the gate has a 40 m
reservation. Unfit sites are rejected. Cities request 30–60 buildings and can be
reduced to 20 on constrained terrain; villages request 8–20. Ruins use 1–3 sites.

Each planet selects one palette: MegaKit, Seahill, desert or modern. Individual
houses never mix MegaKit and Seahill. Medieval houses use six deterministic
orientations (0, 30, 150, 180, 210 and 330 degrees). Rotated footprints, grading,
door placement and approach paths use that same transform. Village lanes are
2 m wide and city streets 4 m, with wider squares and junctions. Narrow feathered
shoulders join the surrounding terrain. Roads reuse biome soil/stone materials. Graded plots retain canonical terrain
vertices at every LOD, so simplification cannot lower the ground beneath foundations.

The plan runs after drainage/gate selection and before canonical terrain/scenery.
Plot grading, road coverage, vegetation exclusions and footsteps share canonical
data. Footsteps classify village paths as gravel and city streets as stone.
Native floors inside the medieval buildings classify as wood.

Server overrides (new locations only):

```
--settlements auto|wilderness|ruins|village|city
--settlement-style auto|mega|seahill|desert|modern
```

Client inspection: `--view settlement`, `--view settlement-above`, or
`--view settlement-interior`; add `--settlement-overlay` for green footprints,
gold entrances, cyan road edges and red rejected candidate centres. F3 includes
site/assembly/road counts. Overrides are retained in the planet generation identity;
reopening a location does not reroll it.

## Templates and assets

The importer produces 114 complete static assembly templates plus a separate
Seahill door prefab. The existing MegaKit door is reused. Initial coverage includes:

- Four MegaKit and four Seahill houses, with floors, walls, roofs and usable doors.
- Four desert courtyard compounds, with openings and separate wall/tower collision.
- Twelve ruin assemblies, three per palette, using grounded broken/partial sections.
- Grouped Brownstone, Downtown and Industrial exterior assemblies; compact city
  placement selects the smaller buildings. Large modern shells stay closed.
- Supplementary Egyptian, Palace, Eurasian, obelisk and pier pieces. Small matching
  landmarks can occupy a square; importing a landmark does not guarantee placement.

Five oversized source groups are excluded pending authored scale/layout review.
Abydos source assemblies still need material reconstruction and are not imported.
The first medieval templates are single-storey buildings; multi-storey houses and
new stair layouts are not claimed. Desert courtyards are open, not roofed shelters.

Source transforms, units and display offsets are normalized into metre-scale,
base-centred assemblies. Materials, UVs and normal maps are retained. Seahill's
unusually dense source modules are simplified offline while preserving UV/material
boundaries. Near meshes keep narrow window bars intact; unsupported refractive
glass panes are omitted rather than rendered as opaque shards. Glass transmission
is deferred. Far meshes preserve simplified silhouettes beyond 180 m. Houses have
simple floor/wall/ceiling collision with actual doorway openings; modern closed
shells use closed collision. No enterable building uses one solid convex hull.

Reproduction from the supplied asset folders, with Blender 5.2 and Python:

```
blender --background --python tools/prepare_settlement_meshes.py
python tools/import_settlements.py
blender --background --python tools/build_settlement_lods.py
```

`assets/settlements/templates.json` is the lightweight generation metadata.
`prefabs.json` contains collision; `provenance.json` records source hashes,
conversions and exclusions; `lods.json` records near/far triangle counts.
Supplied author/license records are retained in `assets/settlements/licenses/`.
Original assets are not modified. Generated templates do not enter the build menu.

## Interior furnishings (layout revision 4)

New locations furnish the four MegaKit houses, four Seahill houses and four desert
compounds. Modern shells, ruins, landmarks and player-built structures are excluded.
This revision requires a fresh map/universe; saved locations are not migrated.

Each enclosed-house site assigns a dining building at four houses, a shop at six,
and storage at eight. The largest houses receive these services; remaining houses
are homes. Homes combine sleeping, dining and storage areas. Desert compounds use
outdoor dining, trading and storage groups without beds or indoor cupboards.
Selections depend on planet seed, plot ID and furnishing revision, not load order.

`assets/furnishings/import-manifest.json` specifies complete source selections,
uniform scale calibration, facing, support surfaces and usable interior floors.
`python tools/import_furnishings.py` imports these assets independently of the
building importer and the removed Seahill source pack. It retains original files,
copies attribution, and emits runtime meshes, collision, catalog and provenance.
The catalog combines furniture-set beds/storage, rustic wood furniture, and selected
tavern seating, containers and tableware. Small decorations are baked into table
variants and have no separate bodies or network records.

Placement tries bounded wall and dining-group candidates, reserves door swings and
tower access, and rejects overlaps and unsupported clutter. A 0.2 m navigation grid
inflates furniture footprints by 0.5 m to preserve one-metre circulation to functional
areas. Optional groups are skipped when they cannot fit. Static collision uses boxes
for storage and separate tops, legs and backs for tables and seating.

Furniture remains immutable generated scenery. It uses ordinary prefab records and
stable IDs, with at most 24 furnishing records per plot. The generated budget is
3,072 records; the world limit remains 4,096. Furniture reuses prefab batching and
frustum culling, is hidden beyond 100 m, and does not load building far-LOD assets.
The furnishing catalogs and collision are included in client/server content hashes.
Bump the layout revision for subsequent changes that require another fresh map.

Verification entrypoint:

```
./scripts/Test-Furnishings.ps1 -Render
```

This uses a separate build artifact directory, runs furnishing and settlement
integration checks, then exports six representative layouts from the actual C#
generator. The optional Blender step renders roofless and interior views using
runtime meshes. Captures and fixture JSON go to `artifacts/furnishings`. Review
the captures before treating asset facing and visual arrangement as verified.
The test script does not start the game client. Normal settlement inspection
views remain available for a later in-game walkthrough.

Furnishing validation completed on 2026-11-09: the Release solution build,
14,441 furnishing/physics/persistence assertions, 48 settlement integration
assertions and six delayed two-client assertions passed. Import reproduction
produced identical outputs. Twelve offline views were rendered and reviewed.
The small-house bed/dining solver now backtracks as a group, and the tavern barrel
selection was corrected after visual inspection. Logs, fixture data and the
validation summary are in `artifacts/furnishings`. This validates offline placement,
native collision, transport and persistence; it is not a graphical game walkthrough
or performance benchmark.

## Ownership, saves and streaming

Each assembly is a generated `StructureRecord` with a static prefab root and
separate door and furnishing children. IDs derive from location plus stable plot,
door and furnishing identifiers.
Location creation and generated records commit in one SQLite transaction. Doors
retain their open state through restarts. Server validation rejects demolition,
attachment and other modifications, while retaining normal door reach, occlusion
and swing checks. Generation is capped at 1,024 records and uses no simulated debris.

The existing building paths provide collision ownership, picking, sunlight/gate
shadows, weather shelter and audio obstruction. Model bounds include roofs for
culling. Outgoing preloads retain the receiving region's intersecting structures
and required owners; they do not request the whole city. Incoming endpoints do
not request the source world. Generation, collision, rendering and visibility
remain separate; changing quality does not change placements.

## Validation

Run `dotnet run --project tests/StargateGame.Tests -c Release -- --settlements-only --corpus`
for the 96-world fixed-seed corpus and native template/persistence checks.
`--settlement-network --delayed` exercises two clients, generated doors, immutable
structures and persistence through 100 ms RTT plus jitter. `scripts/Test-Settlements.ps1`
finds real-terrain inspection seeds and records fixed-time ground/overhead views.
It waits for server readiness and keeps its saves/captures below `artifacts/settlements`.

The final 96-world corpus uses seeds `7319 + i*7919`, i=0..31, per preset.
Each preset requested 9 wilderness, 14 ruins, 8 villages and 1 city. Successful
primary placements were:

| Preset | Ruins | Villages | Cities |
|---|---:|---:|---:|
| Temperate | 14/14 | 7/8 | 0/1 |
| Desert | 14/14 | 6/8 | 0/1 |
| Icy | 14/14 | 7/8 | 0/1 |

The sampled city seed was too steep/wet in all three presets. Forced real-terrain
inspection finds cities at other seeds; the planner does not override safety to
hit a requested population. Dry-plot relief and water buffers were the main
rejection reasons. Maximum plan times in this run were 1.387 s temperate, 1.373 s
desert and 0.511 s icy; these are generation times, not frame times. Full per-seed
counts and reason totals are in `artifacts/settlement-corpus.json`.

Passed: all eight native house floor/door/ceiling checks, all four desert courtyard
entry checks, canonical graded-plot vertices at distant LODs, atomically materialized
records, restart identity/open-door persistence, flat-terrain city counts for all
four palettes, bounded welcome frames, rotated footprint separation, road transfer
round trips and chunk-order independence. The delayed two-client door check passed;
an earlier cold run timed out during preparation. A transport keepalive now
keeps idle resource preparation from expiring the socket, without sending gameplay
events; an 11-second idle/resume regression passed. Loading stalls remain a
limitation rather than a claim of hitch-free loading. Generated enclosed medieval
rooms exclude dense exterior fog using their rotated collision footprint.

Existing regressions passed: 111 building checks (including persistence), 88 prefab
checks, 18 streaming-cache checks, 207 galaxy/network checks with latency/jitter,
and 14 river checks. The final galaxy run verifies bounded outgoing preloads,
zero incoming source-world bytes, prepared cache reuse, endpoint timeout/reset,
late joins and location unload. Tests wait for both independently delivered endpoint
updates instead of assuming simultaneous arrival or a fixed 900 ms wall-clock delay.
The river comparison retains its recorded generator-4 reference of 2.119 m rather
than retaining the old generator just for the test.

### Captures and measurements

RX 9070 XT, 1920x1080, Performance, VSync off, 25-second warmup and 8-second
sample. These are fixed-camera rendering views with the player remaining at the
gate, not a worst-case city physics traversal. No isolated-machine performance
claim is made on this shared development workstation.

| Final view | Frame p95 | GPU p95 | CPU render p95 | Maximum frame |
|---|---:|---:|---:|---:|
| Seahill village, ground | 8.492 ms | 6.486 ms | 3.605 ms | 192.639 ms |
| Seahill village, overhead | 6.320 ms | 5.144 ms | 2.465 ms | 325.006 ms |
| Desert ruins, ground | 2.610 ms | 2.074 ms | 1.368 ms | 146.989 ms |
| Desert ruins, overhead | 2.126 ms | 1.811 ms | 0.465 ms | 160.959 ms |
| MegaKit city, ground | 11.491 ms | 10.108 ms | 6.653 ms | 214.780 ms |
| Icy house, midnight interior | 12.134 ms | 10.112 ms | 3.603 ms | 148.988 ms |
| MegaKit city, 60-second warmup / 15-second sample | 12.122 ms | 8.888 ms | 5.235 ms | 207.138 ms |

The Seahill ground, city and icy-interior views miss the 8.33 ms p95 target. Loading/streaming
spikes remain visible in the maxima; no hard frame-time guarantee is claimed.
The longer city run did not meet the target either; warming up alone does not
resolve the observed cost. Its log is in `artifacts/settlements/steady-city/`.
Final scene GPU accounting was 239-363 MiB and process CPU memory 3.85-4.45 GiB
in these views. These are whole-scene totals, not settlement-only allocations.
The 228 near/far manifests and all their mesh/texture references were verified.

Final ground/overhead screenshots and logs are in
`artifacts/settlements/final-inspection/`, with city and midnight interior checks
in `artifacts/settlements/final-city/` and `artifacts/settlements/final-night/`.
The enclosed icy room capture has no visible indoor snowfall or exterior mist.
Earlier captures in
`artifacts/settlements/inspection/` also cover a modern city, desert courtyard
village and icy MegaKit village; they predate the final window/terrain-LOD fixes.
Their frame p95 values were 3.089/5.047 ms (modern ground/overhead),
2.053/1.939 ms (desert village), and 7.529/4.425 ms (icy village).

Remaining validation limits: no exhaustive walkthrough of all modern shells or
landmarks, no 60-building city traversal benchmark, and no long-duration
minimize/restore or repeated-planet memory soak. Source modern shells remain
closed; the initial enterable houses are single-storey. Ruins are small authored
fragment assemblies, not destructible versions of every complete building.

Release solution/test builds completed with zero warnings/errors. Windows client
and server publish directories were refreshed, including settlement assets,
documentation, licenses and the verified full galaxy catalogue. Final logs:
`artifacts/settlements/final-build.log`, `final-corpus.log`, `final-network.log`,
`galaxy-complete.log` and `publish.log`.

