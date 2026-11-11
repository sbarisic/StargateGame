# Starting-planet generator

Start the Visual Studio **Server + Client** configuration, then press **F4** or

choose **Planet Generator** in the pause menu. The default paired profiles use

port 17777 and the persistent `regional-v10` universe. Both projects

retain a **Gate pair fixture** profile for the earlier fixed-scene tests.

The menu requires a server started with `--dev-planets`, a local connection, and

one connected player. Fixed fixtures explain why regeneration is unavailable.

## Controls

The left/current column shows saved home settings. The pending column is your

draft. Use the mouse wheel or Page Up/Down to scroll, Tab/Shift+Tab or Up/Down to

move focus, and Left/Right to adjust values. Click the left/right arrows to

adjust with the mouse. Seed and time accept typing; Ctrl+A replaces their text.

Enter in an input does not regenerate. F2 switches to the destination inspector.

Seed is a signed 32-bit integer. Randomize changes the pending seed only.

Biomes are temperate, desert and icy. Settlement type/style use the existing

choices. Relief ranges from 0.25 to 2.00 times normal; tree and rock density range

from zero to twice normal, subject to valid-site spacing and exclusions.

Landscape mode is Auto, Inland or Coastal. Auto makes a seeded 50/50 choice.

Water offset ranges from -3 to +3 metres; it also sets coastal sea level.

Lakes stay below their spill level. Only edge-connected low ground becomes ocean.

The read-only world dimensions are 4096 x 4096 metres.

Diagnostic view selects elevation, drainage, sediment, geology or habitats; None

restores normal rendering. Diagnostic colors affect terrain only.

**Apply Environment** saves the selected time and weather without rebuilding

terrain. Fixed time freezes only the sun. Day Cycle starts at the selected time

and retains the planet's day length. Auto uses biome weather; Clear disables

precipitation and dust. Light Snow, Snowstorm and Desert Dust can be forced on

any biome. These choices survive restart and take precedence over startup

inspection overrides after application. Rain is not implemented.

**Regenerate Starting Planet** always targets the saved home, even from another

planet. It clears home terrain edits, scenery removals, placed structures and

props, then creates the configured generated content and respawns the explorer.

Player identity/loadout, home identity/address and other planets are preserved.

Press **Reload Current Settings** to discard draft edits, or **Reset Parameters**

to reset relief/water/scenery multipliers without changing the seed or biome.

## Recovery and implementation

Regeneration queues behind routine collision streaming, but refuses active gate

travel/connections, world edits, another operation or additional players. New

joins wait until it finishes. The menu reports actual phases and elapsed time.

Cancel remains available until saving begins. Failure before commit preserves

the active planet. Detail streams after collision and arrival terrain are ready.

The SQLite worker publishes one backup at

`<save directory>/<universe>/universe.sqlite.before-regeneration` before the

replacement transaction. Each successful regeneration replaces this backup;

it is the immediately preceding saved state, not an unlimited undo history.

Stop the server before restoring a backup. Normal source assets are untouched.

Generation revision 10 requires a fresh universe. Previous generation revisions
are rejected; no legacy generator or migration layer is retained. Existing save
files are left untouched. Run matching client and server builds. Environment
changes do not change the terrain settings hash.

Focused checks: `dotnet run --project tests/StargateGame.Tests -- --planet-debug`.

`--network-only` skips the standalone generation/persistence checks while

investigating transport. `--smoke-generation --smoke-seconds 180` on the client

runs the visual capture harness against a dedicated developer test server; it

**regenerates that server's saved home** and must use a separate test universe.

## Earlier generator-panel validation (before revision 8)

Debug client/server builds passed. The focused generator suite passed 52 checks;

the river and environment regressions passed 14 and 51 checks. Settlement

generation, persistence and collision integration checks also passed. Network

checks were repeated after the final request-queue changes.

The final isolated client/server capture run exercised all three biomes, snowstorm

on desert and dust on icy terrain, and the menu at 960 by 600. Evidence is in

`artifacts/planet-debug-final-visual/`. Request-to-controllable-frame times were

2.59 seconds for temperate, 1.95 for desert and 2.62 for icy. These are warm Debug

runs with wilderness settings, not cold-start or large-city guarantees. Server

generation times are recorded separately in `server.log`; visual refinement

continues after control resumes.

Tests cover cancellation, backup failure, saved-world reopening, home regeneration

from another location, multiplayer refusal and disconnect/reconnect. They do not

simulate a process kill at every SQLite commit boundary or a real external-host

attack. The paired launch configuration was inspected and the client/server

executables exercised directly; Visual Studio's F5 UI itself was not automated.

## Regional generator (revision 10)

New universes use a 4096 m square centred on the origin. The shared domain has

64 by 64 chunks, each 64 m wide, with metre samples and a one-sample halo.

Fixed graphics and movement fixtures retain their own dimensions.

Preparation reports Landforms, Erosion, Water, Habitats, Settlements and Arrival

terrain. The regional cache is an atomic binary file below the terrain cache's

`regional` directory; at most 32 plans remain on disk. Cancelled regional work

cannot publish its temporary file. Detailed chunks have their own versioned cache.

The 513 by 513 regional grid contains elevation, sediment, drainage, moisture,
exposure and forest coverage. Revision 9 integrates the approved 2D terrain study:

- Inland mountain planets choose seeded broad ridges (one quarter of seeds) or
  branching power-shaped Perlin/fBm ranges. This variety is independent of climate.
- All styles run 160 bounded USPED/channel iterations with cover feedback,
  Strahler tributary hierarchy, distributed hillslope runoff and local relaxation.
- Desert mesas receive separate curved, asymmetric wind-oriented dunes.
- Coastal regions use a variable coastline, connected ocean flooding and offshore
  islands. Inland basins remain separate lakes; desert channels remain dry.
- Erosion progress reports iteration counts and remains cancellable. Atomic
  regional caches use revision 4; content hashes include generation revision 10.

The port adapts SLTerrainGeneration's algorithms into C#. Its seed-local random
sequence differs from NumPy, so new branching/coastal/desert maps follow the
approved construction but do not reproduce prototype pixels for identical seeds.
No Python or native library is required by the running game. Broad-ridge seed
7319 retains its approved base composition with the new erosion.

Detailed sampling refines river beds, lake shores, gate approaches and local
settlement grading. The erosion is a visual landscape model, not a sediment-mass-
conserving geological simulation; iterations are not physical years.

Nearby terrain and collision still use canonical chunks. Far terrain instead

samples the regional plan directly in quadtree tiles (64–1024 m), with 8–128 m

interior samples and shared metre-spaced boundary samples. It carries no scenery

or collision. Detailed tree/rock collision follows players. The render camera

can see across the region; detailed vegetation remains distance-limited.

Continuous forest and sediment values travel with terrain vertices. Canonical

chunks also carry moisture, exposure, soil, geology, habitat, snow and drainage.

Tree clusters and young trees follow these fields; temperate stands no longer

select snowy birch. Meadow grass has four separate 0.35–0.75 m mesh/volume bakes

in 1 m vertical domains. Woodland retains four 0.10–0.28 m bakes. Small procedural

flower clusters and fallen branches use the foliage batcher and have no physics.

The topography, cache and wire formats changed. Start a **fresh universe**;

old saves are left untouched. There is no legacy generator or migration path.

The server accepts `--landscape Auto|Inland|Coastal`. The client accepts

`--landscape-view None|Elevation|Drainage|Sediment|Geology|Habitats` for inspection.

## Focused developer checks

`dotnet run --project tests/StargateGame.Tests -c Release -- --regional` checks

regional determinism, cache reload, a dry arrival, chunk seams and far-tile and

environment transfer. It writes regional field maps, camera positions and timing

information to `artifacts/realistic-planets`. Run the separate regional inspection

script after building its validation output. Extended playtesting remains manual.

## Hydrology adaptation

Revision 9 ports the approved SLTerrainGeneration study into C#. See
`thirdparty/slterraingeneration/README.md` for source attribution and the exact
adaptation boundary. The full erosion/deposition flux calculation now replaces
the earlier capacity-only transport. It uses metre-aware gradients, distributed
runoff, Strahler channel orders and bounded channel shaping over 160 iterations.

Flow-area conservation, deterministic generation and cancellation have focused
checks. This erosion model does not claim global sediment mass conservation.
Game hydrology still owns lakes, river water levels and ocean connectivity.

## Added terrain materials

The reusable import command is:

    python tools/import_terrain_pbr.py --only marble_rock dry_riverbed_rock ground_sand ground_snow

Marble Rock 03 replaces the temperate and icy exposed-rock material. Dry Riverbed
Rock supplies temperate rocky banks and desert exposed ground. Ground 055 S
supplies beaches and the first desert sand layer; Snow 002 supplies soft snow.
These retain the existing eight-layer palettes and runtime material format.
Original sources are unchanged. All four normal maps were checked against their
height gradients and converted once from OpenGL to DirectX. Source credits and
hashes are retained in the manifest and THIRD-PARTY-NOTICES.md.

## Tree clearance

All spawned tree species now receive an additional 1.5x uniform scale through
TreeScenery.SizeMultiplier. This is applied on top of the earlier import-size
calibration and preserves mature/young size variation. Trunks, volumetric
canopies, bounding boxes and authoritative collision all use the same transform.
Root planting depth remains fixed in world metres, so the size increase raises
branches rather than burying the tree further. Scenery revision is now 4.
Low branches on individual source meshes can still need playtest adjustment;
this does not remove branches or change the character collider.

## Earlier revision-8 validation, 2026-11-11

Release client and server builds passed. Focused checks passed for hydrology
(9), regional generation/cache/environment transfer (30), mixed LOD and shoreline
seams (6), mounted material palettes (7), and enlarged-tree fixture clearance (10).
The test project still reports its existing CS8602 warning in GalaxyChecks.cs.

One network integration run verified a dry 11-plot village with 28 roads,
cancellation during generation, a 4 km arrival with collision preparation,
streaming interest at 0/128/256/384 m, and far-terrain transfer without decode
failures. This was camera-interest traversal, not a manual walking playtest.

A cold regional-only generation sample took 3545 ms, retaining 7.53 MiB of field
arrays. The sampled managed-memory increase peaked at 42.72 MiB (10 ms polling);
the process peak was 68.84 MiB. This excludes subsequent settlement preparation,
arrival collision and client asset loading. Raw values are in
artifacts/realistic-planets/profile.json. Do not interpret them as end-to-end load
time or a performance guarantee.

Representative 1920x1080 Performance-quality captures and their logs:

- [Mountain meadow](artifacts/realistic-planets/mountain-meadow/RegionalReview-1080p.png)
- [Forest interior](artifacts/realistic-planets/forest-interior/RegionalReview-1080p.png)
- [Desert dunes and mesa](artifacts/realistic-planets/desert-mesa/RegionalReview-1080p.png)
- [Coastline](artifacts/realistic-planets/coastline/RegionalReview-1080p.png)
- [Icy highlands](artifacts/realistic-planets/icy-highlands/RegionalReview-1080p.png)

The forest view is refreshed after the final 1.5x tree adjustment; the mountain,
coast and ice views show the preceding tree size. The desert view is refreshed
after reducing false moisture along dry channels. The earlier forest image is
retained as artifacts/realistic-planets/forest-interior-before-tree-scale.png.

Remaining playtest risks: distant terrain faceting and material repetition;
patchy or bulky vegetation at some viewing angles; species-specific low branches;
unusual coastal/river junctions; and steep approaches on untested seeds. The
reference's full visual polish is not yet matched. Extended manual traversal,
all quality presets and a seed matrix were deliberately not run.

Final enlarged-tree forest sample: 1920x1080 Performance quality, VSync off,
40-second warmup and 5-second measurement (356 frames). Frame time median
13.544 ms, p95 13.836 ms, maximum 210.037 ms; GPU time median 13.328 ms and p95
13.560 ms. Scene GPU accounting was 928.0 MiB and client private memory 2924.8
MiB. This is one development-machine observation, not a cross-hardware target
or a claimed speedup. The long frame outlier remains visible in the raw log.


## Revision-9 integration validation

The approved mountain, desert and coastal construction is now part of the C#
regional generator. Start the default Server + Client profiles for the fresh
`regional-v9` universe. Existing saves were not deleted or migrated.

Release client/server and test builds passed. The test project retains the
unrelated GalaxyChecks.cs:511 nullable warning. Focused checks passed:

- 11 hydrology/erosion checks: flow conservation, stream hierarchy, full erosion
  repeatability on a small field, metre spacing and cancellation during erosion.
- 30 full regional checks across temperate inland, desert, coast and icy terrain,
  including a cold repeat, cache reloads, dry arrivals and terrain transfer.
- 7 mixed-LOD, shoreline, drainage-agreement and distant-detail checks.
- One integration run: dry 11-plot/27-road village, active cancellation, network
  arrival, streamed camera interest at 0/128/256/384 m and far-terrain transfer.

Cold regional generation took 30.8-36.1 seconds for the four sampled regions.
Each retains 7.53 MiB of regional fields. The process high-water mark during the
four-region check was 215.2 MiB; this is not isolated regional peak allocation.
See `artifacts/approved-terrain/measurements.json`. No speedup is claimed.

Actual 1920x1080 Performance-quality captures:

- [Mountain valley](artifacts/approved-terrain/mountain-meadow/RegionalReview-1080p.png)
- [Desert overview](artifacts/approved-terrain/desert-overview/RegionalReview-1080p.png)
- [Coastal overview](artifacts/approved-terrain/coast-overview/RegionalReview-1080p.png)

The initial desert view was too close to a slope, and the initial coastal camera
was inside a canopy; both captures are retained with their logs. Wider inspection
views use the same generated regions. No terrain was edited for the captures.

One short mountain sample before the final distant-detail refinement
(35-second warmup, 3-second sample, VSync off): frame
median 4.166 ms / p95 4.560 ms, GPU median 3.961 ms / p95 4.109 ms, maximum frame
173.487 ms. This is a fixed inspection camera, not a walking benchmark.

Remaining playtest risks: steep routes on untested seeds, directional erosion
traces, regular dune spacing, lake water budgets and coarse distant terrain/material
repetition. The terrain geometry is integrated; the reference's complete material
and lighting polish is not claimed. No manual in-game walking walkthrough was run.

The wide desert capture exposed lost dune shape in the old 64-128 m distant
mesh sampling. Revision 9 now caps regional-tile interior spacing at 16 m;
streaming tile hierarchy, bounds, shared edges and cache budgets remain intact.
The desert/coast overview captures were refreshed after this fix. The mountain
capture predates only this distant-detail refinement. Earlier wide captures
remain under `artifacts/approved-terrain/before-distant-detail/`.

Final distant-detail desert overview sample: frame median 2.031 ms / p95 2.377 ms,
GPU median 1.729 ms / p95 2.013 ms, maximum frame 115.757 ms. Scene GPU accounting
was 658.3 MiB; resident far tile payloads were 8.99 MiB with zero decode failures.
The scene has no grass or trees, so this is not a vegetation performance claim.

## Lake and shoreline corrections (revision 10)

Erosion now continues descending valleys through the open regional boundary.
The former fixed edge could act as a dam after the interior eroded. Genuine
closed depressions remain lakes; lakes near an edge are not simply discarded.
Lake detection uses eight-neighbour connectivity, includes shallow margins, and
separates basins with different spill heights. Sub-half-metre puddles are skipped.
Detailed basin heights use bilinear interpolation and retain surface relief at
the shoreline, avoiding the old diagonal regional-triangle contours.

Lake and ocean meshes keep their level surface to the shore. Dry exclusion
vertices have negative clipping depth, and shoreline extrapolation in canonical
water meshes uses only the same water body. Rendering and swimming queries share
that canonical shoreline. Distant water also clips dry vertices rather than
including zero-depth dry triangles.

Use a fresh universe with matching client/server builds. The default Visual
Studio server profile now uses `regional-v10`; existing save files are untouched.