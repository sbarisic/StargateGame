# Detailed streaming and destination preloads

Protocol 23 is required on both ends. Regional terrain uses generator 8 and
requires a new universe; old save files remain untouched. The bounded streaming
and direction-aware preload design supports player and prop gate travel.
See [SETTLEMENTS.md](SETTLEMENTS.md) for generated structure ownership and collision.

## Active location

Detailed canonical data uses a radius of 256 m (Low), 384 m (Performance), or
512 m (High), with a 64 m eviction margin. Current collision coordinates remain
pinned, including structure collision owners. Visibility never controls physics.
All physics and prediction continue at 60 Hz, with the existing rollback path.

Outside that working set the server supplies render-only terrain/water meshes
and actual scenery placements. Far meshes use the existing step-8 mesher, including
its canonical wet/shoreline refinement and conforming chunk edges. They contain
no height arrays or collision data. Distant terrain and trees remain visible.
The binary far format indexes repeated vertices before Brotli compression. Positions
and water coordinates round-trip exactly; distant normals use signed 16-bit components
and material weights retain their canonical bytes. Counts, expanded size, identity, hashes and fragments are bounded.

Current terrain preparation and snapshots have priority over supplemental bulk
traffic. Canonical encoding/hash results are cached on immutable chunks. Far
payloads and hashes are cached per location (64 MiB / 256 entries), with two
preparation workers. A client admits at most eight outstanding far requests.

Rendering reuses chunk visibility candidates separately for camera and shadow
frusta. Tree LODs update after 2 m of movement or streaming changes, with 4 m
hysteresis. Repeated building parts share the existing eight-transform batching
path. None of these culls remove collision or needed shadow casters.

## Outgoing destination cache

The dialer starts receiving a separate preload after the server resolves six
accepted symbols. Orb acceptance extends interest to players within 64 m of the
outgoing gate. Existing interest ends after ten seconds beyond 80 m. Incoming
observers receive local gate state and **zero source-world preload terrain**.

Each preload has its own ID, source/destination identity, terrain settings hash,
manifest hashes and building revision. It includes the receiving gate's 3 by 3
canonical chunks, water, intersecting buildings, attachment parents and collision
owners. It never changes the active terrain-transfer object or starts a second
running prediction world. Terrain meshing and managed collision input preparation
run in background tasks; immutable scenery/terrain textures already loaded by the
renderer are shared.

States are requested, receiving, preparing, ready and stale. One speculative cache
is allowed, with conservative admission of managed terrain data and meshes against
128 MiB CPU / 64 MiB GPU limits. One dormant destination physics world additionally
cooks the landing region incrementally before capture. It never steps or predicts;
promotion revalidates building changes and applies the authoritative actor baseline.
Cancellation disposes it.

The incoming biome's terrain palette decompresses on a worker and uploads in the
shared preload budget. Only one incoming palette is retained (approximately
32/128/512 MiB each for decoded CPU data and GPU arrays at Low/Performance/High).
Promotion releases the previous palette and decoded bytes. Same-biome transitions
reuse terrain arrays; temperate transitions also retain reusable foliage assets.
Surface masks, placement records and lighting histories still reset per world.

An incomplete cache does not delay opening. Speculative GPU work is split into
individual vertex-attribute uploads and admitted into at most 0.5 ms of the
remaining shared 2 ms upload budget, after active terrain and grass. A graphics
API call cannot be preempted; F3 and benchmark logs expose actual overruns.

Address edits, failures, disconnects and relocation invalidate obsolete work.
Completed caches survive normal closure for 30 seconds. F4 developer relocation
can reuse canonical data, CPU meshes, immutable collision inputs and uploaded
terrain meshes. Actual relocation still fetches current building revisions,
collision coverage and a fresh authoritative actor baseline. Stale building
revisions never authorize stale collision or actor activation.

Destination packets share bandwidth with ordinary source scenery instead of
waiting for its entire queue to empty. Join and collision traffic retain priority.
Transfer preparation contains only the landing region and building owners; other
explorers' collision remains installed on the server. The arrival fade waits for
the rendered 3 by 3 landing region, not distant terrain or LOD replacement jobs.

## Travel loading check (2026-11-08)

A matched 1080p Performance Home-to-Abydos run using the same initial universe
reduced the captured white-screen-to-visible-arrival interval from 6.525 to
2.238 seconds (2.431 seconds in the earlier optimized run). The final client logged
authoritative commit at 2.061 seconds and the landing frame at 2.144 seconds after
capture. The preloaded desert palette
activated in less than 0.1 ms; initial cold startup still performs a cold load.
The two-second minimum transit and collision checks remain unchanged.

Evidence is under `artifacts/travel-preload`: `before/`, `after-final/`, `network.log`,
`cold-network.log`, and `palette.log`. These are local route measurements, not a
guarantee for cold destinations, congested networks or every full-world scene.

Gate-only destination preparation does not cook terrain around distant saved
props. Saved props remain retained and frozen; their required regions are prepared
before a player is admitted, and untouched saved bodies are preserved on unload.

## Collision preparation

Terrain/scenery triangles and building indices are prepared outside the global
native lock. Native shape cooking and world mutation stay serialized. Managed
terrain inputs have a 64 MiB / 128-entry reusable cache; each physics world retains
up to 32 removed native terrain shapes. Edited entries are invalidated and all
native shapes are disposed with the world. Sleeping props reuse water samples
only while pose and water/topology revision remain unchanged; buoyancy wake rules
still run.

## Inspection

F3 shows detailed/far/preloaded tiles, CPU/GPU preload memory, bytes, decode and
preparation time, upload time, collision reuse, longest native cook and sleeping
water-query reuse. The ordinary graphics preset selects detailed radius.

Useful diagnostic commands (Release client):

```text
--benchmark-warmup 30 --benchmark-seconds 30 --graphics performance --vsync off --view buildings --time-of-day 12 --pause-sky
--benchmark-warmup 30 --benchmark-seconds 30 --graphics performance --vsync off --view forest --time-of-day 12 --pause-sky
--benchmark-seconds 50 --graphics performance --vsync off --dial-check --preload-relocate --view gate
```

`--preload-relocate` is an explicit developer inspection: after the diagnostic
connection closes it uses F4's relocation path to consume the prepared cache.
It is not teleportation through a gate. Measurements and captures are recorded
in `artifacts/streaming/`; see the validation notes below for observed limits.

## Building corners

Village walls, doorways and window walls that meet at right angles now derive
one imported corner trim per joint. Existing saves receive the same fix without
adding persisted pieces. Trim is owned by a real wall, uses the source mesh for
collision, appears in shadows and picking, and disappears when either joined wall
is removed. Explicitly placed nearby trim suppresses the automatic instance.

## Remaining work

Full GPU occlusion/Hi-Z remains deferred. Gate travel now reuses destination caches
and prepares missing arrival collision while the traveler is captured. Native Jolt mesh
cooking is still indivisible and can cause a cold-load spike. New building
materials still use the existing shared model loader on first visible use; this
work does not replace that loader with asynchronous texture uploads. GPU upload
admission is a time budget, not a driver-level hard deadline.

## Local measurements (2026-11-07)

RX 9070 XT, 1920 x 1080, Performance, VSync off, audio enabled. Final fixed-noon
captures use a 30-second warm-up followed by 30 seconds of samples. Times below
are p95 milliseconds, not average FPS.

| Scene | Frame | GPU | CPU render | Draws | Detailed / far tiles |
|---|---:|---:|---:|---:|---:|
| Previous building scene | 4.336 | 4.162 | 1.919 | 463 | 256 / 0 |
| Updated building scene | 4.312 | 4.165 | 1.715 | 401 | 110 / 146 |
| Updated dense forest | 5.729 | 5.591 | 1.855 | 534 | 90 / 166 |

The building comparison reduces render CPU work and draw calls, but total frame
time remains GPU-bound. The baseline used the earlier 10-second warm-up. Forest
inspection now selects its stand from canonical and far scenery together, so
earlier forest measurements used a different camera and are not a valid A/B pair.

Building client private memory was 3,569 MiB; estimated resident scene buffers were
189 MiB plus 153 MiB render targets. Forest values were 3,713 / 186 / 153 MiB.
These are whole-process and partial GPU accounting, not a claim that all memory
belongs to streaming. Building traffic was 4.22 MiB canonical plus 4.98 MiB far;
forest traffic was 10.49 plus 8.40 MiB, including camera-driven replacements.
Both runs decoded all far tiles without errors. Detailed CPU residency was 110
and 98 chunks respectively, while all 256 landscape tiles remained rendered.

Warmed frame maxima were 28.0 ms (building) and 27.2 ms (forest), despite passing
the 8.33 ms p95 target. Cold native collision calls reached 47.6 and 56.9 ms.
Those spikes remain a follow-up; the cache makes revisits cheaper rather than
making native cooking interruptible.

Logs: `artifacts/streaming/buildings-final.log`, `forest-settled.log`, and
`baseline-buildings.log`. Captures: `artifacts/streaming/final-captures/BuildingsFinal-1080p.png`
and `ForestSettled-1080p.png`.

The rendered outgoing preload used 0.62 MiB of destination transfer data and
prepared nine chunks in 187 ms of background elapsed time. It retained 31.73 MiB
CPU and a conservative 11.59 MiB GPU estimate. All nine meshes uploaded and one
developer relocation consumed the cache. The source remained playable throughout.
Server preparation took 497 ms for the retained destination; an earlier cold run
took 1,026 ms. These are local observations, not a controlled cold/warm speedup.
After orb acceptance, already-prefetched connection preparation took 0.050 s of
server simulation time. See `preload-indexed-final.log` and `demo-server-final.log`.

That dialing/relocation run had 5.62 ms frame p95 and a 159 ms maximum. Peak
speculative upload was **1.331 ms**, exceeding the desired 0.5 ms ceiling even
after moving mesh indexing off-thread and splitting graphics calls across frames.
Further subdivision or graphics-driver staging is needed for a hard ceiling.
Native collision cooking peaked at 65 ms on the client and server tick peaks
reached 89 ms during preparation. These spikes are not hidden by warmed averages.

## Regression coverage

Automated suites cover immutable chunk encoding, binary far-tile fidelity and
bounds, native shape reuse/32-entry eviction, detailed streaming traversal,
preload cancellation, zero incoming preload bytes, cache promotion, rotated
building corner trim and demolition, prediction/pushing, barrels/buoyancy,
and player/prop membrane contacts. Galaxy and prefab networking checks also run
with simulated 100 ms RTT and jitter. Logs are under `artifacts/streaming/`.
The graphics smoke exercises resize, all quality presets and reconnect resource
cleanup. Manual minimize/restore and exhaustive long-duration memory profiling
are not covered by these runs.


## Revision 8 regional terrain

The canonical domain is now 4096 m square. Distant rendering requests quadtree
keys with a chunk origin and level 0–4. The server meshes these directly from the
regional plan; it does not generate canonical chunks to answer far requests.
Near and far tiles share metre-spaced boundary vertices. Far data includes water
depth and environmental vertex values, with no trees, rocks or collision.
Far encoded server cache remains limited to 64 MiB/256 entries. Client interest
uses a bounded quadtree cover; old far tiles and near meshes are evicted as that
cover moves. Canonical caches retain collision interest and outstanding work.
Travel cancels the generation lifetime and drops the old visual interest.
