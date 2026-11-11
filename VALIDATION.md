# Validation — LayerProcGen terrain, 2026-11-06

## Orb picking and failed-dial reset — 2026-11-07 follow-up

Failed orb activation now returns the gate to Idle, clears glyphs/chevrons and
connection identity, and releases ownership. The reason remains visible. This
also applies to incomplete input and asynchronous resolution/preparation failures;
stale requests or other players' rejected input cannot reset someone else's dial.
Both endpoints reset after a preparation timeout, and late readiness stays ignored.

Corrected the interaction export's emission-mask V coordinate to match FishGfx's
vertical texture flip. Thirteen targeted picking checks cover nine red-dome points
and its centre from four viewing angles. Network checks press the physical centre
directly and verify replicated resets after incomplete input, self-dial, and timeout.
The interaction asset now participates in the client/server content hash.
Build/test logs: `artifacts/orb-fix-build.log`, `artifacts/orb-fix-static.log`,
and `artifacts/orb-fix-network.log`.
Release builds pass with zero warnings, and all 194 galaxy/dialing checks pass
with 100 ms RTT and jitter. The rendered failure capture at
`artifacts/orb-fix-visual/failed/dial-Reset-0-0.png` shows the cleared DHD and error;
the renderer reports zero gate emission draws and zero gate lights after reset.
Windows client/server packages were refreshed together for the new content hash.

## DHD dialing and persistent galaxy — 2026-11-07

Release client/server builds pass with zero warnings. Windows packages were
refreshed under `artifacts/publish`; only the server package contains the full
galaxy artifact. FishGfx and source game assets are unchanged.

Commands:

```powershell
dotnet build StargateGame.sln -c Release
dotnet tests/StargateGame.Tests/bin/Release/net10.0/StargateGame.Tests.dll
dotnet tests/StargateGame.Tests/bin/Release/net10.0/StargateGame.Tests.dll --galaxy-only --delayed
./scripts/Test-Dialing.ps1
./scripts/Publish.ps1 -Client
```

Results: **809 existing regression checks**, **180 galaxy/dialing checks** in the
final capability run, and the separate extended expiry run pass. The latter
completed 179 checks before the additional culture/capability assertions were
added. Retained capability flags and schema 6 are also covered by the persistence
rerun.
The latter use two real client sessions through the delay proxy (100 ms RTT with
jitter). Coverage includes all 48 equivalent orders, all ordinary named routes,
hidden key 13, all visible picking surfaces, inclusive acquisition boundaries,
explicit aliases, independent same-seed locations, restart identities, concurrent
materialization, duplicate/stale commands, competing dialers, early orb/origin,
source position retention, incoming opening, late joins, self-dial failure, reset,
out-of-reach ownership release, preparation timeout, rejected late readiness,
cancelled preparation, retry, disconnect survival, receiver-initiated closure,
and save/unload after the reuse grace period. Corrupt catalogue rejection and
cancelled atomic replacement preserve the prior artifact. Existing persistence
regressions cover backup migration, save restoration, and process termination.
The extended run also reopens an unloaded destination and verifies the full
120-second automatic expiry plus 0.6-second closing phase. Resize, minimize/restore,
quality changes, reconnect, and repeated developer planet switches pass the
rendered lifecycle check; horizon, gate-light, and environment targets return to
zero at world clear while shared asset resources remain resident.
Logs: `artifacts/galaxy-expiry-tests.log`, `artifacts/galaxy-capability-tests.log`, `artifacts/galaxy-final-network.log`,
`artifacts/dial-persistence-final.log`, `artifacts/dial-final-regressions-v2.log`,
and `artifacts/dialing-lifecycle.log`.

Catalogue build reproduced **41,410,215** canonical pairings and **19,020,803**
valid entries. Artifact size is **686,846,320 bytes** (655.03 MiB). Its payload
SHA256 is `BCB668107F424CDA6E06987B718AA77936DA1B468F5FE15F9674ACE393F30650`.
The measured full build took 5.480 s with 116.6 MiB peak working set; verification
took 0.889 s with 32.2 MiB peak. A subsequent 10,000-key lookup benchmark measured
8.864 µs mean / 13.700 µs p95, and 55.562 µs mean for spatial acquisition queries.
That process peaked at 219.7 MiB after touching mapped record pages; the complete
catalogue is not instantiated as managed objects or database rows.

Real rendered client/server captures are in `artifacts/dialing-final/`:
`horizon-45`, `dhd-close`, and `failed`. They include idle, each accepted key,
origin-ready, preparation, eight opening samples, open, closing, and failed states.
The inspected images show individual orange keys, an unlit idle membrane, the
three-metre blue/silver plume, and foreground/terrain occlusion. The failed
self-address originally kept seven accepted keys; the follow-up above supersedes
that behaviour with a reset and retained error message. Normal gameplay has no
always-active override; old visual regression scripts now request it explicitly.

RX 9070 XT, 1920×1080, Performance, VSync off, 10 s warm-up + 30 s sampling:

| View/run | Frame p95 | GPU p95 | Render CPU p95 | Maximum sampled frame |
|---|---:|---:|---:|---:|
| Gate 45°, complete dial/open/close | 5.226 ms | 4.332 ms | 2.745 ms | 159.144 ms |
| DHD close-up, complete dial/open/close | 2.405 ms | 2.253 ms | 0.785 ms | 160.675 ms |
| Failed-address DHD | 2.770 ms | 2.250 ms | 0.914 ms | 26.133 ms |
| Separate destination-load timing run | 4.289 ms | 4.153 ms | 2.547 ms | 425.055 ms |

These runs meet the 8.33 ms p95 rendering target, but are not hitch-free. They
include PNG readbacks and first-use effects. Startup shader/resource work produced
3.6–4.7 s worst frames before the warm-up window. In the separate-process load run,
home startup took 5.267 s; destination generation/restoration/collision readiness
took 5.941 s. At readiness the server reported 8.042 ms tick p95 and a 276.266 ms
peak. Collision construction still causes loading spikes; no claim of stall-free
destination preparation is made. Headless two-client tests share the native Jolt
registry lock in one process and are not used as server performance measurements.

Remaining validation limits: eight simultaneous connections/two simultaneous cold
loads have enforced limits but no full-capacity soak result; the Linux runtime
has not been exercised in this pass. Visual
captures cover one rendered endpoint; incoming timing was checked by a second
network client. Travel, damage, physical membrane forces, manual dialing, and
intergalactic routes remain intentionally absent. See [DHD_DIALING.md](DHD_DIALING.md).


## Recovered tree scenery - 2026-11-06

Added the six supplied tree assets with deterministic preset selection. Seed 7319
places 241 temperate tree groups, 23 icy trees, and seven desert sapling groups
using existing candidates and density. Terrain heights and rock placement remain
unchanged. Full foliage footprints stay dry and within their owning chunk; roots
avoid collidable rocks and existing gate/approach exclusions remain in effect.

- Release build and Windows client/server publish succeeded. Published asset hashes
  match the checkout; the server still contains no textures or FishGfx assembly.
- Reconnect testing exposed texture-upload idle timeouts. Shared models/textures
  now load before networking and survive reconnects; per-planet terrain/LOD state
  is cleared on disconnect and all shared resources are disposed on application exit.
- Final published smoke passed two connections, four planet-switch requests,
  pause/resume, resizing, and F3 wood wireframes (`artifacts/trees-package-final.log`).
  The server committed desert, icy, temperate, then desert again. Model-load messages
  occur only once; reconnect reuses shared resources. Earlier `trees-package.log`
  records the now-fixed upload timeout and is superseded by this final run.
- All **482 checks passed** in `artifacts/trees-regression-final.log`: six variants,
  reload/order stability, unchanged terrain, water/gate exclusions, upright growth,
  recovered LOD selection, trunk deflection, contact replay, topology invalidation,
  multiplayer joins, cancellation, switching, reconnects, and server loss.
- The heavier loading check exposed a simulation-tick ping limiter that could reject
  healthy clients during stalls. It now uses a monotonic one-second window with a
  four-request bound. Tests verify catch-up bursts and flood rejection.
- `artifacts/trees-repeat-import.log` confirms byte-identical repeated conversion
  of tree meshes, textures, collision, bounds, and provenance. Originals are read only.
- Captures in `artifacts/tree-fixed/` were inspected for leafy, spruce, and bare
  sapling silhouettes, repeating bark, rooted placement, and cutout shadows.
  The earlier `tree-views/` run exposed a FishGfx uniform-name collision and is
  superseded: the renderer now uses its own uFoliageCutoff uniform. Per-material
  repeating texture sampling is opt-in, preserving existing gate/DHD/rock sampling.
- At 1920x1080, Performance, VSync off, ten-second warm-up then four-second orbit
  measurement: P95 frame time was 2.109 ms temperate, 3.114 ms icy, and 1.552 ms
  desert (`artifacts/tree-fixed-results.log`). These inspected views meet 8.33 ms;
  occasional stalls remain (40.799 ms worst frame). Startup and F3 debug costs are
  excluded from the warmed-up numbers. Smoke startup allows additional time for
  first-use shader compilation and texture upload.

Birch and leafy-tree rendering use recovered LODs at 80/180 m; spruce/saplings use
source meshes. Shadow caches invalidate when LODs change. Collision contains only
opaque wood and shares chunk activation/removal with terrain. Foliage is passable.
Wind, cutting, seasons and growth are not implemented. Linux was not rerun.



## Recovered rock scenery - 2026-11-06

Checkpointed the previous project before this change as local commit `5647df8`.
Imported all 13 recovered rock models, albedo/normal textures, and collision meshes.
Temperate uses mossy stones/outcrops; desert uses sandstone; icy uses the detailed
stone meshes with muted colour and snow shading. Small ground rocks are decorative
on every preset. Placement derives only from existing canonical candidates.

Validation:
- Release build: zero warnings/errors. Windows client/server packages refreshed.
- Full regression: 456 checks passed (`artifacts/rocks-regression.log`), including
  multiplayer joins, prediction, collision activation, and developer transitions.
  Focused rock tests passed again after final icy catalogue selection
  (`artifacts/rocks-tests-final.log`).
- All 13 models exercised. Checks cover canonical reload/order stability, unique
  IDs, unchanged heights, preset selection, gate/water exclusion, standing on a
  rock, contact replay, and checkpoint invalidation when its chunk unloads.
- Published client/server smoke completed two connections, pause/resume, resizing,
  and F3 rock wireframes (`artifacts/rocks-package.log`). Dense F3 wireframes are
  intentionally excluded from the normal rendering benchmarks below.
- Re-running the importer produced identical runtime mesh, texture, bounds,
  provenance and collision hashes (`artifacts/rocks-import-repeat.log`). Originals
  are read only. Packaged client asset hashes match the checkout; the server
  package contains no PNGs or FishGfx assembly.
- In-game captures inspected for all presets: `artifacts/rock-final/` (temperate,
  desert) and `artifacts/rock-icy-final/` (final snowy boulders). Albedo/normal maps,
  slope alignment, cast shadows, and UI remain visible. Earlier icy captures used
  oversized ground-detail meshes and are superseded by the final detailed boulders.
- 1080p Performance, VSync off, ten-second warm-up then five-second orbit samples:
  temperate P95 1.077 ms, desert 1.504 ms, final icy 2.005 ms. Logs are
  `artifacts/rock-final-results.log` and `artifacts/rock-icy-final-results.log`.
  Occasional spikes remain (107.563 ms worst temperate frame); these short local
  checks meet the P95 goal but do not establish stall-free traversal.

The one-metre chunk footprint margin deliberately rejects some edge candidates.
Collision uses recovered lower LODs where available and exact source triangles
otherwise; ground pebbles have no collision. Linux was not rerun in this pass.


## Natural daylight renderer - 2026-11-06

Implemented client-only HDR lighting, cached static/dynamic sun shadows,
triplanar terrain normals, procedural sky/clouds, matched fog, shallow-water
composition, tone mapping, and FXAA. UI text remains outside post-processing.
FishGfx and simulation/network source were not changed by this rendering pass.

Release client/server build and Windows publish succeeded. The headless suite
passed all **292 checks** (`artifacts/daylight-tests.log`). The server package
was checked for accidental rendering assemblies/textures: none were present.
Linux was not rerun for this client-only rendering change.
The published Windows client also completed a High-preset cliff-view smoke run
against the published server (artifacts/daylight-package.log), with a 1.229 ms
P95 warmed-up frame time at 1080p.

### Warmed-up 1080p measurements

RX 9070 XT, Performance preset, VSync disabled, seed 7319. Each run warmed up
for ten seconds, saved its screenshot before measurement, then measured eight
seconds while the inspection camera moved. Frame times are milliseconds.

| View | Mean frame | P95 frame | Worst frame | P95 measured GPU passes |
|---|---:|---:|---:|---:|
| temperate-gate-performance | 0.843 | 0.964 | 16.129 | 0.725 |
| temperate-river-performance | 0.859 | 0.991 | 5.925 | 0.718 |
| desert-gate-performance | 0.841 | 0.963 | 1.813 | 0.721 |
| desert-river-performance | 0.989 | 1.130 | 4.571 | 0.848 |
| icy-gate-performance | 0.864 | 1.077 | 13.843 | 0.717 |
| icy-river-performance | 0.822 | 0.950 | 6.752 | 0.667 |

All measured P95 frame times meet the 8.33 ms target. Occasional full-frame spikes
remain; the worst in these runs was 16.129 ms. These are current prototype views,
not a guarantee for future vegetation, every exploration path, or loading frames.
GPU queries exclude attachment copies, UI, and presentation; full-frame measurements
include them. Estimated target/shadow memory is 89.1 MiB at 1080p Performance.
Logs and captures: `artifacts/daylight-verified/` and
`artifacts/daylight-verified-results.log`. Earlier `daylight-final` results predate
the final sampler-index fix; use the verified set for the delivered shaders.

### Visual and lifecycle checks

- Inspected gate/DHD and crate shadows, river transparency/reflection, normal detail,
  snow/sand/grass palettes, horizon fog, and UI sharpness in captured game frames.
- Fixed false terrain shadows at cascade boundaries by replacing divergent sampler
  array indexing with constant-index accesses. Verified the fix on pale desert
  terrain; terrain casting remains enabled and no caster geometry offset is used.
- A two-client run completed 23 planet switches and exercised server-loss cleanup.
  After early warm-up, the switching client's private memory stayed around
  995-1026 MiB rather than increasing with each switch. Evidence:
  `artifacts/daylight-stress.log`, `daylight-stress-server.log`, and `daylight-ui.log`.
- Started with Low quality, cycled High/Performance/Low on each connection, resized
  between 960x600 and 1280x800, and completed two reconnects. All three connections
  rendered and exited cleanly; disposed-scene private memory readings were 1072
  and 1079 MiB. Evidence: `artifacts/daylight-lifecycle.log` and associated captures.
- F3 rendering counters, collision lines, and F12/completed-frame capture paths
  were exercised by smoke runs. Debug wireframes add significant CPU cost and
  are excluded from the clean-scene performance target.
- Computer Use was stopped with Escape during manual validation. No further UI
  automation was performed. Physical menu-key handling, mouse capture transitions,
  minimize/restore, and interactive simultaneous crate pushing were not fully
  revalidated in this pass. Automated pause/resume, resize, quality changes,
  reconnects, two-client planet switching, and the existing physics/network suite
  completed independently.

Controls, rendering architecture, metrics boundaries, and reproducible commands
are documented in `GRAPHICS.md`; normal-map paths/hashes are in asset provenance.


## Gate scale follow-up

The gate is uniformly enlarged by about 9.4%, from 6.127384 m to 6.7056 m across,
using the supplied reference dimensions. Its authored proportions produce an
approximately 5.0 m opening. Embedding scales to approximately 1.37 m, preserving
the buried lower arc. Entity scale is shared by rendering, Jolt collision, and
collision debug lines; the DHD is unchanged.
Release builds pass with zero warnings/errors and all 292 Windows checks pass,
including walking through the gate without jumping. A graphical smoke run checked
the enlarged, embedded gate. Evidence: `artifacts/gate-scale-build.log`,
`artifacts/gate-scale-tests.log`, and `artifacts/gate-scale-captures/GateScale.png`.
Windows client/server packages were refreshed. Linux was not rerun for this change.

## Gate seating and terrain texture follow-up

The gate entity and its structural collider are now lowered by 1.25 m in both
generated and legacy maps. A new regression walks through the generated gate
without jumping. The Windows suite passes **292 checks** after this change.
Release client/server builds pass with zero warnings/errors.
The additional 0.60 m lowering was visually checked against the reference: the
lower arc is fully below the clearing, leaving an open arch. Evidence:
`artifacts/gate-depth-captures/GateDepth.png` and `artifacts/gate-depth-tests.log`.

Grass, cliff, sand, and snow albedo maps were imported from the local extraction
with byte-for-byte copies and recorded source hashes. Runtime loading ignores
packed alpha masks before image conversion, avoiding dark marks in snow and
cliffs. All three presets were inspected in actual graphical smoke runs, including
the corrected snowy surface and the embedded gate base. Evidence is in
`artifacts/textured-captures/`, `artifacts/textured-tests.log`, and
`artifacts/terrain-textures-build.log`. Source paths and the repeatable import
command are in `assets/PROVENANCE.md`.

The platform and streaming stress results below describe the preceding terrain
baseline; the texture follow-up did not rerun the Linux or 20-switch stress suites.

## Builds and automated checks

- Release solution build and Windows client/server + Linux server publication succeed
  with zero warnings/errors. Build, test, publish, and client/server launch scripts ran.
- **291 checks pass on Windows x64 and Linux x64 under WSL.** This includes the
  earlier physics/network checks plus canonical terrain, shared borders/materials,
  water depth, drainage, clearings, approaches, LOD border vertices, catalogue
  stability, cache corruption, bounded fragments, worker ordering, streamed collision,
  rollback topology, developer controls, cancellation with late joining, crate retention, and server loss.
- Eighteen preset/seed combinations (0, 1, -1, 42, 7319, int.MaxValue for each preset)
  produced identical canonical corpus fingerprints on Windows and Linux:
  `96A33BB6D7FD19D6B68075A8AC75AC542AB4887FC7EDE6A4E89DFE02DF266433`.
  This hashes the tested chunks and candidates, not every chunk of every possible seed.
- Independent cold-cache tests compare serial versus parallel LayerProcGen execution
  and reversed request order. Canonical bytes match. Corrupt disk entries and altered
  network payloads are rejected.
- Two headless clients through 100 ms RTT / 20 ms jitter explored separated regions.
  Collision coverage grew from 49 to 94 chunks, both crossed chunk boundaries without
  falling through, then switched planets and returned with settled crates preserved.
- A separate 512 KiB/s terrain-delivery test passed joining, loading/cancellation,
  switching, return, crate retention, and server-loss handling.
- A 20-switch headless stress run with latency/jitter passed. After warmup, process
  private memory stayed between 207 and 223 MiB; managed live memory was 26–29 MiB.
  One generation context remained after each completed transition. The first client
  received 32.52 MiB of canonical terrain payload over that run (JSON/base64 overhead
  is additional). Cache hits avoid retransmitting prepared chunks.

## Graphical and package runs

Two actual FishGfx clients ran against the dedicated server for about two minutes.
The developer client requested 23 switches through all presets. Both remained
connected; the later-finishing client correctly observed the other leaving. Normal
and F3 screenshots show the terrain, gate/DHD assembly, colliders, candidate markers,
and preserved text rendering. Automated pause/resume and real window resizing ran.

The developer client's private memory settled around 807–819 MiB after warmup;
estimated resident terrain mesh GPU storage was about 30–40 MiB (excluding shared
model textures and driver overhead). Mean frame time was 10.39 ms and p95 21.14 ms.
The other client measured 9.77 ms mean and 20.63 ms p95. Maximum samples included
initial synchronous asset loading at approximately 1.7 seconds; loading is not yet
free of frame stalls. These are local smoke-run measurements, not performance guarantees.

Fresh-cache packaged startup measured 1,625 ms on Windows for icy/14321 and 2,532 ms
under WSL for desert/14321, including regional/chunk generation and 49 colliders.
A warm-cache scripted desert startup measured 767 ms. Three cold asynchronous
chunk requests, including their regional dependency, took 50.81–61.55 ms in serial
configuration and 53.38–64.86 ms with parallel generation enabled. This small sample
is a latency check, not a scaling benchmark. The standalone server packages
contain Shared, WorldGen, LayerProcGen, Physics/Jolt, the executable, embedded collision,
licenses, and the matching native runtime. They contain no FishGfx, textures, or model
assets. Linux startup also handled SIGINT. No source installation is needed at runtime.

The stress run exposed a pre-existing ownership problem in JoltPhysicsSharp 2.23.0:
constructors that assign native handles directly leave ownership false. The explicit
ownership adapter now frees physics systems, characters, and mesh settings, and marks
transferred filters as borrowed. Before this fix, private memory grew beyond 1.6 GiB
in 20 switches; the corrected run stayed bounded as recorded above.

## Evidence and limits

Local ignored artifacts:

- `artifacts/terrain-tests-windows.log`, `terrain-tests-linux.log`
- `artifacts/streaming-exploration-final.log`, `streaming-slow.log`
- `artifacts/streaming-stress-final.log`
- `artifacts/terrain-two-a.log`, `terrain-two-b.log`, `terrain-two-server.log`
- `artifacts/terrain-two-captures/` and `script-terrain-captures/`
- `artifacts/terrain-packaged-windows.log`, `terrain-packaged-linux.log`
- `artifacts/terrain-generation-benchmark.log`
- `artifacts/terrain-build-script.log`, `terrain-publish-windows.log`, `terrain-publish-linux.log`

Physical mouse capture, actual Escape/F4 keyboard delivery, and high-DPI behaviour
still need a manual pass; smoke mode intentionally leaves the cursor free. The
existing input paths are preserved. Tests use local processes and a delay proxy,
not a WAN. GPU bytes are an estimate from mesh buffers; no GPU profiler or native
heap profiler was used. The tests establish bounded growth for the exercised runs,
not an exhaustive proof for every seed or gameplay interaction. Gaea experiments
were preserved. Vegetation models, caves, swimming, and gate travel remain deferred.

---

# Validation — Jolt integration, 2026-11-06

## Completed on the final implementation

- `scripts/Build.ps1`: Release solution build, zero warnings and errors.
- `scripts/Test.ps1`: **78 checks passed on Windows x64**.
- The same test executable was published for Linux x64 and run with .NET 10
  under Ubuntu/WSL: **all 78 checks passed**.
- Character checks cover grounding, gravity without input, jump edges, ceilings,
  walk/sprint/diagonal speeds, 0.25 m versus 0.40 m steps, 44° versus 46° slopes,
  sliding down steep slopes, terrain seams, perimeter walls, the gate opening,
  ring obstruction, DHD collision/sliding, and player pass-through.
- Prop checks cover pushing, a settled two-crate stack, sleeping, standing on a
  crate, moving-support carry, and authoritative corrections during contacts.
- Native save/restore reproduces subsequent character and rigid-body state exactly
  for the tested input sequence on each platform. Checkpoints from another topology
  are rejected. Prediction history remains bounded. A forced-GC test covers the
  pinned binding's filter-ownership workaround.
- Real TCP sessions cover fragmented messages, truncated/malformed/oversized frames,
  unique player IDs, capacity, authoritative movement visible to a peer, props
  advancing without input, late input acknowledgement, invalid/future inputs,
  version/content compatibility, reconnects, and server loss.
- Two instances of the actual client session implementation ran through a seeded
  TCP delay proxy: 100 ms nominal RTT with up to 20 ms jitter. Local movement was
  immediate, shared props moved, and both players and crates converged within 2 cm;
  crate rotations converged within 2°. Five additional connection/disconnection
  cycles completed with forced garbage collection and increasing topology epochs.
- `scripts/Publish.ps1 -Client` and `-ServerRuntime linux-x64` produced standalone
  framework-dependent packages. The actual Linux server executable assembly started
  successfully without a display, FishGfx, textures, Blender, or visual model files.
- Both native runtime SHA-256 values and bridge-source hashes match their checked-in
  build manifests. Upstream Jolt/joltc/FishGfx submodules remain unmodified.
- `Run-Server.ps1` and `Run-Client.ps1` launched the final implementation. Two graphical
  clients connected, moved, loaded all 21 Stargate and 41 DHD parts, and exited cleanly.
  Logs contain no client errors; the remaining client's player count returned to one.
- Both graphical clients exercised pause/resume and resizing from 1280×800 to
  960×600 and back. Normal and F3 debug views were captured and inspected. The gate,
  DHD, terrain, crates, remote placeholder body/name, capsule overlay, and sampled
  collision wireframes align with the scene. Existing texture filtering and text
  rendering fixes remain present.
- `git diff --check` reports no whitespace errors. Runtime projects have no references
  to RaylibGame or the source installation. The optional Blender re-export script
  retains its documented source path; it is not part of building or running the game.

Local evidence, intentionally ignored by Git:

- `artifacts/physics-tests.log` and `artifacts/physics-tests-linux.log`
- `artifacts/final-build.log` and `artifacts/final-publish-*.log`
- `artifacts/FinalAlpha.log`, `artifacts/FinalBeta.log`, `artifacts/script-server.log`
- `artifacts/jolt-final/FinalAlpha.png` and `FinalAlpha-physics.png`
- `artifacts/jolt-final/FinalBeta.png` and `FinalBeta-physics.png`
- `artifacts/publish/client-win-x64`, `server-win-x64`, and `server-linux-x64`

## Limits

The interactive window closed before desktop keyboard/capture checks could finish.
Automated smoke runs exercise the same pause/resume functions and real window resizing,
but deliberately leave the cursor free. Physical mouse capture, Escape-key delivery,
and high-DPI behaviour still need a manual check. Repeated session lifecycle and
server-loss handling were verified with the real networking/client-session code.

Latency tests use loopback with injected delay; no separate-machine WAN test was run.
Replay was verified within each platform, not bit-for-bit between Windows and Linux.
The game remains server-authoritative and corrects cross-platform/remote-input drift.
Native compilation was tested with VS 2022 C++ tools and Ubuntu Clang; other toolchains
and Linux distributions were not validated.

## Preserved asset work

The earlier texture refinement welded the temporary unwrap mesh, rebaked full gate
rings at 4096×4096, and enabled linear/trilinear filtering with limited anisotropy.
That work is preserved. Runtime textures reproduce reconstructed base colour, not
Unreal's original shading or normal-map relief. Collision now uses exported structural
triangles instead of the old ring/pedestal approximations. Source assets were not
modified by the physics work. Gate effects, travel, and saved progress remain deferred.


## Persistent worlds and modular building (2026-11-06)

- Release solution builds with zero warnings/errors. Windows client/server and
  Linux server packages include the building/persistence projects. Server output
  needs no FishGfx, model textures, Blender or original installation.
- `artifacts/building-regression-final.log`: ALL 561 CHECKS PASSED, including the
  existing terrain/physics/network/tree/rock tests plus the new building suite.
- `artifacts/building-delayed-final.log`: expanded two-client scenario passes
  placement, doorway/door operation, dependent-demolition rejection, duplicate
  requests, late join, independent destination state, return to a saved building,
  removal and expired-checkpoint rejection through 100 ms RTT / 20 ms jitter.
  These door checks are also included in the final 561-check full-suite run.
- `artifacts/building-physics.log`: exact six-triangle closure, support, doorway
  clearance, closed/open door collision, both stair flights and native epoch
  invalidation. Later full-suite checks also cover cross-chunk owner retention and
  actual tree-trunk collision removal without changing canonical terrain.
- `artifacts/persistence-tests.log`: process-kill/WAL recovery, shared-location
  aliases, exclusive universe ownership, injected SQL write failure with atomic
  rollback, migration backup/restore and future-schema rejection. This is an
  injected storage-error test, not a physical disk-full or power-loss experiment.
- Linux SDK was older than global.json (10.0.114 versus 10.0.400). Linux test/server
  binaries were cross-published by the Windows SDK and executed with WSL's .NET 10
  runtime; no SDK pin or native Jolt build was changed.
- `artifacts/building-linux-final.log` records the WSL building/SQLite/Jolt
  and two-client scenario. `artifacts/building-linux.log` is the earlier passing
  44-check physics/persistence run without networking.

Visual inspection: `artifacts/building-captures/BuildingPreview-1080p.png` shows a
40-piece square/triangle building with windows, an open door, stairs, a tower,
flat roofs and cast shadows. `Building1000-wide-1080p.png` shows the complete
1,000-piece synthetic rendering fixture. These fixtures bypass gameplay placement
rules and contain deliberately repeated buildings; they are benchmark data, not
normal world generation.

RX 9070 XT, 1920x1080, Performance preset, VSync off, 10-second warmup then five
seconds of sampling (`artifacts/building-large-wide.log`): frame mean 1.359 ms,
p95 1.564 ms, maximum 19.001 ms; GPU p95 1.229 ms; CPU render p95 1.141 ms; 359 draw
calls; render targets 89.1 MiB. This is a fixed-camera warmed benchmark, not a
worst-case interactive guarantee. Initial loading still produced a 1.52-second
frame. The separate 1,000-piece CPU fixture measured support evaluation at
128 ms and collision preparation at 64 ms in the full-suite run; its 0.002 ms
physics mean is an idle world and must not be interpreted as active-player cost.

The build palette and red protected-clearing preview were visually inspected.
Computer-use input activity conflicts prevented completing the manual Escape and
mouse-capture interaction sequence; these remain manual follow-up checks. Existing
smoke paths for pause/resume, resize, F3, F12 and reconnect remain available.
Long-duration resource stress and an exhaustive dense-building contact-capacity
stress test were not completed in this pass.

Final Linux result: BUILDING 62 CHECKS PASSED, including the headless server and
two-client door/planet-return scenario. Follow-up malformed-fragment and extreme
coordinate and eight-storey roof checks passed on Windows (58 checks in
building-validation-final.log). The final Release build and targeted tree-clearing
cancellation test also passed after the door-boundary and prepared-shape cleanup fixes.
The doorway attachment LOS check excludes its receiving frame, while unrelated
obstacles still block placement.

Latest Linux follow-up: artifacts/building-linux-guards-final.log reports BUILDING
58 CHECKS PASSED with the final geometry, persistence and physics sources. This
run excludes networking; the earlier 62-check Linux run includes networking.

## Building plaster and overlapping corners (2026-11-06)

Release build: zero warnings/errors. artifacts/building-material-tests.log:
BUILDING 128 CHECKS PASSED, including rotated square/triangle wall joins, extended
end-cap coverage, duplicate-wall rejection, doorway/stair physics, persistence
and two-client placement, door operation, planet return and demolition.
The network fixture leaves clearance for wall shells at the gate protection edge.
Visual inspection: artifacts/building-material-captures/PlasterCorners-1080p.png.
The 40-piece fixture shows plaster instead of cliff stone, continuous surfaces
without the artificial black grid, and overlapping wall ends. Texture orientation
uses a unit horizontal tangent on angled walls; render and collision use the same
wall geometry. Existing kit-1 saved anchors and socket coordinates are retained.

## Animated event horizon and rim fade (2026-11-06)

Release client/server builds and Windows publishes completed. The full regression
run passed 630 checks (artifacts/horizon-regressions.log); the dedicated delayed
two-client contact run passed 21 checks (artifacts/horizon-network.log). Contacts
remain client-side visuals and do not change authority or movement.

The final surface uses independently moving gradient-noise fields with restrained
coordinate distortion, preserving consistent detail scale without repeated wave
rows. Reflective detail fades radially toward the ring; opacity remains one.
Final front capture: artifacts/horizon-delivery/temperate-horizon-front--1080p.png.
The earlier 12-view matrix in artifacts/horizon-final covers front, close, oblique
and rear views on all three presets before the last pattern/fade refinements.
Final lifecycle captures cover the revised material across the three presets.
The final occlusion fixture shows foreground proxies/crates hiding the membrane
and the membrane hiding the rear proxy. Camera-crossing captures also completed.

RX 9070 XT, 1920x1080, Performance, VSync off, ten-second warm-up and five-second
fixed-view sample: horizon off GPU mean 0.739 ms / p95 0.781 ms; final close-up with
16 disturbances GPU mean 0.911 ms / p95 0.961 ms. Mean additional GPU cost was
0.172 ms (p95 difference 0.180 ms). Stress frame p95 was 1.718 ms. These short
fixed-view samples are not worst-case guarantees. Initial terrain-loading stalls
of approximately one second remain outside the warmed measurement.

Bloom targets add 7.9 MiB at 1080p. Resize, graphics-preset changes, four planet
switches and a reconnect completed in artifacts/horizon-lifecycle/client.log.
At 1280x800 horizon targets remained 3.9 MiB and dropped to zero on world clear.
This short run does not establish long-duration memory stability. Manual
minimize/restore remains unverified: desktop automation could not supply input
geometry, then reported intervening user input; no minimize success is claimed.
## Gate emission, local shadows and prop ripples (2026-11-06)

Release solution build: zero warnings/errors (artifacts/gate-light-build.log).
Windows client/server packages refreshed (artifacts/gate-light-publish.log).
Final full regression: ALL 649 CHECKS PASSED (gate-light-regressions.log).
Dedicated contact suite with real delayed clients: HORIZON NETWORK 44 CHECKS
PASSED (gate-light-network.log), including continuous resting-prop contact,
pushing a crate through the visual membrane, convergence and pulse expiry.
The proxy adds 100 ms RTT with 20 ms jitter. An earlier concurrent graphics/test
run failed a timing-sensitive player-contact assertion while the server reported
simulation overload; isolated reruns and the final run passed. Startup overload
messages still occur during terrain delivery; no claim is made to fix those stalls.

Visual captures: artifacts/gate-light-angles covers front, 45 degrees, 70 degrees,
close-up and rear at fixed shader time 6 seconds. gate-preset-check covers desert
and icy front/70-degree views. gate-quality-low/high include moving and resting
render-only props. The final mask revision is visible in gate-light-delivery and
gate-light-lifecycle. White folds remain visible at oblique angles; radial fading
and opaque depth occlusion remain intact. Only the seven active chevron crystal
and strip materials emit; metal-mask padding is constrained to avoid metal texels.
The source blend is only read; generated masks include source/output checksums.

Shadow inspection: gate-shadow-closed and gate-shadow-open use identical fixed
views and dimmed diagnostic daylight. The closed door blocks blue spill; opening
it illuminates the inner door and the ground through the aperture. Warm chevron
light remains occluded by the gate/frame. Moving-prop captures show local shadows
on other props. This is point-light illumination with PCF, not true area lighting.

Paired RX 9070 XT, 1920x1080 Performance, VSync off, ten-second warm-up followed by
eight seconds of measurement; moving-prop fixture and screen-filling close-up:

| Configuration | GPU mean / p95 ms | Frame mean / p95 ms | Render targets MiB |
| --- | --- | --- | --- |
| Gate active, local shadows, 16 ripples | 1.079 / 1.133 | 1.340 / 1.752 | 121.8 |
| Emission/bloom/16 ripples, spill disabled | 0.984 / 1.033 | 1.141 / 1.318 | 97.0 |
| Entire active-gate effect disabled | 0.774 / 0.816 | 0.950 / 1.222 | 89.1 |

Logs: artifacts/gate-perf-on, gate-perf-spill-off, gate-perf-all-off.
Measured local-light/shadow GPU mean difference: 0.095 ms; complete active-gate
GPU mean difference: 0.305 ms. These are sequential short captures, not a guarantee
for every scene. The active run had an isolated 16.902 ms frame; cold loading still
stalls for approximately one second. Warmed p95 is below the 8.33 ms target.
Low/High moving-prop oblique runs measured frame p95 2.013/2.040 ms respectively.

Lifecycle: artifacts/gate-light-lifecycle/client.log records two window resizes,
quality changes, four planet switches and a reconnect. At 1280x800 the bloom
allocation remained 3.9 MiB and Performance light atlas 24.8 MiB across switches;
both dropped to 0.0 on world clear. Per-quality atlas sizes are 6.4/24.8/97.5 MiB.
Quality-change log lines precede that frame's atlas replacement; F3 captures show
the completed frame. This short lifecycle run does not prove long-duration memory
stability. Minimize/restore was not repeated in this pass. No physics, protocol,
save format, dialing or teleportation behaviour was added.

## DHD glyph materials and independent lights (2026-11-06)

- Release solution build: zero warnings/errors. Full regression run: **661 checks
  passed**, including 12 new DHD state checks (individual IDs, key 39, entity
  isolation, atomic rejection, duplicate selection, clearing, and preview parsing).
  Logs: `artifacts/dhd-build.log`, `artifacts/dhd-regressions.log`.
- All 39 exported key albedo/emission pairs match provenance hashes; the standard
  visible set has 38 keys, excluding overlapping key 13. Original bake inputs
  are unchanged. A second Blender export produced identical output hashes.
- Inspected 1080p close views: all off (`artifacts/dhd-unlit-final`), only key 01
  with horizon disabled (`artifacts/dhd-single-final`), and six selected keys
  with the active gate (`artifacts/dhd-multi-final`). Only selected glyphs/rims
  emit. Housing/dome stay inactive. Fine glyph atlas margins are recoloured too,
  preventing white bake padding bleeding back into the markings.
- Final six-key capture: frame p95 1.082 ms, GPU p95 0.451 ms, 121.8 MiB targets.
  All-off capture: frame p95 0.757 ms, GPU p95 0.455 ms, same targets (active gate
  already owns bloom). These are short warmed two-second diagnostic samples,
  not a new broad performance acceptance run.
- Windows client/server packages refreshed. Packaged DHD manifest and all
  referenced textures match source hashes. Source blend remains read-only.
- Per-key illumination is client-owned visual state, with explicit clear on
  world unload/reconnect. Actual picking, dialing, synchronization, centre-button
  activation and DHD light spill remain deferred. No server/physics changes.


## DHD central orb (2026-11-06)

- Release client/server build passed with zero warnings/errors. All 19 focused
  DHD checks passed, including orb independence, overrides, and reset defaults.
- Inspected 1080p on/off captures in `artifacts/dhd-orb-on` and
  `artifacts/dhd-orb-off`, both with the event horizon off. Orb emission stays
  inside the dome material, leaving the surrounding metal dark. Off restores
  the original red albedo. Selected keys remain independently controllable.
- Orb-on plus six lit keys: short warmed sample GPU p95 0.376 ms, frame p95
  0.931 ms, 97.0 MiB targets. All DHD emission off: GPU p95 0.327 ms,
  frame p95 0.841 ms, 89.1 MiB targets; selective bloom targets are released.
  This is a visual diagnostic comparison, not an isolated orb-cost benchmark.
- Windows packages refreshed; orb albedo/mask hashes match source and provenance.
  No network, physics, persistence, or dialing changes. DHD light spill remains
  deferred. The earlier complete 661-check regression was not rerun for this
  isolated material/state addition.


### Orb reference adjustment

Replaced the smooth yellow glow with red lowlights and smaller dark mottled
patches around pale hot regions. Procedural variation is baked in model space
and combined with the recovered emission map. The shared emission shader now
reads linear RGB; existing greyscale key/chevron masks retain their old tint.
Release build and in-game shader compilation passed. Inspected the final 1080p
capture in `artifacts/dhd-orb-mottled-final`: patches remain distinct through
bloom, while the housing stays non-emissive. Warmed diagnostic GPU p95 0.451 ms,
frame p95 0.802 ms. No simulation/state code changed in this adjustment.


The follow-up reference tuning reduces patch contrast and broadens the tonal
transition. Mottling now has a 20% emission floor, a wider smooth threshold and
less colour separation between low/high regions; intensity is 6.5 instead of 8.
Inspected `artifacts/dhd-orb-soft/temperate-dhd-close-on-1080p.png` after a clean
Release build and in-game shader compilation. Texture detail remains visible
without the prior sharp red/white islands. GPU p95 0.452 ms in the short warmed
capture. The Windows package was refreshed with this softer version.


## Glyph readability and desert fog

The inactive DHD marking target changed from linear RGB (0.13, 0.115, 0.09)
to (0.105, 0.093, 0.073), retaining baked wear and existing emission masks.
Desert fog now uses a four-times denser ground layer with a sandy horizon blend;
other preset fog coefficients are unchanged. Surface, water reflection, gate,
DHD emission and membrane bloom use the same fog transmission function.

Release builds passed with zero warnings/errors. Inspected matching desert wide
before/after views (`artifacts/desert-fog-before`, `artifacts/desert-fog-after`),
a close desert gate view, and the temperate unlit DHD (`artifacts/dhd-darker-final`).
Distant terrain fades more strongly while close objects remain readable. In-game
shader compilation succeeded. Short warmed desert wide GPU p95 was 1.160 ms
versus 1.165 ms before, with unchanged target memory. Windows packages refreshed.
No physics, network or simulation changes; no new unit tests for these visual
parameter changes.


## Membrane motion and contact interference

Main-wave speed increased from 0.55 to 0.70. Impact waves now advect the base
white-fold pattern and transform its gradient, combined with a gentler height
wave. Overlap displacement has a smooth bound; filtering uses warped pixel
footprints. Contact detection, authority and ripple lifetimes are unchanged.

Release builds and in-game shader compilation passed. All 35 horizon contact
checks passed. Inspected front/close 16-ripple stress captures and moving/resting
props in `artifacts/horizon-interference-live`. Six live captures at 0.25-second
intervals show changing folds and 8-9 active prop disturbances. The upper membrane
region changed by 10.5-11.7 RGB levels on average between adjacent captures.
A separate `artifacts/horizon-main-live` run has zero contact ripples and changing
base waves across all six captures, confirming that normal animation is running.
Fixed-time inspections now have an explicit frozen-animation HUD label.

Screen-filling 16-ripple stress: GPU p95 1.062 ms, frame p95 1.277 ms.
Live prop fixture: GPU p95 1.092 ms, frame p95 1.862 ms. These are short warmed
1080p Performance samples. Windows packages refreshed; no physics/network edits.


### Pattern-only contact revision

Per user feedback, removed the contact slope and gradient-transform contributions
that still read as an independent wave overlay. Contacts now only displace the
coordinates sampled by the same base normal field; there is no separate contact
normal, reflection or emission component. Increased speed from 0.70 to 1.40 (2x).

Release build and live GLSL compilation passed. Six captures in
`artifacts/horizon-pattern-only-live` have 8-9 active prop disturbances with
unfrozen time. The unobstructed membrane changes by 15.1-15.8 RGB levels on
average every 0.25 seconds, confirming continued base animation during contact.
Short warmed 1080p run: GPU p95 1.052 ms, frame p95 1.865 ms. Windows packages
refreshed. Earlier interference implementation notes above are superseded by
this pattern-only revision; contact detection and lifetime tests remain unchanged.

### Collision-outline wave simulation (supersedes the point-warp revisions)

Implemented a client-only residual wave solver, driven by a shared animated
height/gradient baseline. Rendered OBB/plane slices and swept sections survive
through AA union-mask rasterization. Stationary boxes continuously reflect the
combined surface; capsule entries use contour impulses. Random point pulses and
the analytic circular contact-wave loop are removed. Overlap masks are unioned
before contour forcing, so coincident sections do not add independent impulses.
No protocol, saved-world, authoritative physics or FishGfx revision changes.

Final appearance follow-up: base speed stays 1.40, contact propagation slows from
1.8 to 1.35 m/s, a 0.05 m normal footprint removes small contact bumps, and rim
brightness rises from 0.30 to 0.55. Centre shading remains unchanged.

Validation on Windows / RX 9070 XT (driver 32.0.23017.1001):

- Release solution build: zero warnings/errors. Full regression run: 675 checks.
  Final focused contact run: 42 checks. Two delayed clients: 51 horizon checks,
  including player crossings, sleeping props, pushing crates through the surface,
  convergence and removal of separated boundaries.
- Actual GPU readback tests at 256, 384 and 512 grids: unobstructed animated
  baseline has exactly zero residual; a controlled packet reflects from the wall
  with zero measured transmission; animated baseline produces nonzero continuous
  boundary response. The probe also rejects non-finite field values. Encoded
  readback magnitudes are diagnostics, not calibrated physical energy.
- Front, 70-degree, rear and screen-filling views with four intersecting boxes:
  `artifacts/horizon-wave-final`. Six live frames per view inspect continued
  animation. `artifacts/horizon-wave-delivery` covers final moving/rotating sweeps.
  Desert and icy no-contact views/sequences: `artifacts/horizon-wave-no-contact`.
- `scripts/Test-HorizonLifecycle.ps1` exercised resize, minimize/restore,
  Low/High/Performance twice, three planet switches and reconnect. No renderer
  errors. Disconnect returned horizon and gate-light targets to 0.0 MiB; the
  repeated quality cycle reported the same allocations. Logs and captures:
  `artifacts/horizon-wave-lifecycle`. This is a short lifecycle check, not a
  long-duration memory soak.

Matched warmed 1920x1080 Performance runs, VSync off, four intersecting props,
screen-filling close view (five seconds sampled per run):

| Collision waves | GPU mean / p95 | CPU render mean / p95 | Frame p95 |
| --- | --- | --- | --- |
| Off (same scene and membrane) | 1.049 / 1.099 ms | 0.879 / 1.255 ms | 1.544 ms |
| On | 1.088 / 1.145 ms | 0.941 / 1.277 ms | 1.631 ms |

Observed incremental GPU cost: 0.039 ms mean, 0.046 ms p95. Simulation-only query
cost was 0.003 ms mean / 0.034 ms p95 across frames (60 Hz updates occur on a
subset of these uncapped frames), maximum 0.064 ms. State adds 5.625 MiB in
Performance. Separate front/rear/oblique runs had frame p95 1.51-3.49 ms.
No-contact desert/icy runs allocate no wave targets and report zero solver cost.
These short local measurements meet the 1 ms incremental target; they are not a
guarantee for every scene or GPU. Comparison logs are in
`artifacts/horizon-wave-compare-off` and `artifacts/horizon-wave-compare-on`.

Windows client/server packages refreshed. Shader sources and packaged copies
are checked for matching hashes. Existing texture provenance is unaffected by
the wave simulation; earlier DHD asset changes remain separate.

## Grass, snow and winter weather — 2026-11-06

Implemented visual-only ground cover and winter rendering. Release client/server
builds succeed without warnings. The full regression run passed **702 checks**;
the final focused environment run passed **28 checks**, including the later
shared wind-integral check. The actual two-client test passed with one direct
client and one behind the existing 100 ms RTT/jitter proxy. Its maximum observed
gust difference was **0.00066** on the 0–1 intensity scale. No protocol, save,
world-generator, physics or FishGfx revision changes were made.

Placement checks cover negative coordinates, canonical triangle heights,
location identity, return determinism, stable quality subsets, chunk ownership,
water/rock/slope exclusions, gate approaches and building/scenery exclusions.
Weather checks cover fog transmission, sun/ambient limits, continuous snowfall,
smooth gust changes, delayed clocks, reset behavior and quality budgets.

Actual GPU shelter readback passed at **256², 512² and 1024²**: the interior of a
roofed room is blocked, points above its roof are clear, and points below its
floor are blocked. Removing the roof during the run changes the interior to
clear after the cached map refreshes. Separate closed/open-roof captures are
available. These rooms are render-only fixtures, not edits to the player's save.

Captures cover all three quality presets, ground-level grass, hills, river banks,
the gate approach, snow terrain, trees, rocks, light snow, storm weather and
indoor shelter. Six-frame live sequences show changing grass and snowfall at
0.25-second intervals. Successive grass-region images changed 10.6–14.7% of
pixels by more than 10/255; snowfall against the sky changed 1.0–2.5%. This is a
motion sanity check, not a substitute for subjective appearance review.

A moving inspection camera traversed a 60 m diameter path at roughly 19.5 m/s,
exercising grass tile replacement, terrain LOD changes, snow-volume wrapping and
shelter-map recentering. Lifecycle tests exercise 960×600/1280×800 resizing,
minimize/restore, all quality settings, three planet transitions and reconnect.
Grass/weather allocation reached **0.0 MiB** at disconnect. Common model/texture
resources remain resident. These tests are not a long-duration memory soak.

Measurements use the local **Radeon RX 9070 XT**, driver **32.0.23017.1001**,
**1920×1080**, **VSync off**, ten seconds of warm-up and 4–8 seconds of measured
frames. Seed 7319 is used throughout. GPU query results are asynchronous; CPU and
GPU overlap and must not be summed. The table reports the worst p95 among the
fixed outdoor views for each group, not a combined percentile across runs.

| Quality | Temperate frame p95 | Grass GPU p95 | Storm frame p95 | Weather GPU p95 |
|---|---:|---:|---:|---:|
| Low | 2.429 ms | 0.039 ms | 1.618 ms | 0.003 ms |
| Performance | 2.587 ms | 0.065 ms | 2.061 ms | 0.006 ms |
| High | 2.754 ms | 0.127 ms | 2.028 ms | 0.010 ms |

Performance grass traversal measured **2.684 ms frame p95**, **1.601 ms total GPU
p95**, **0.053 ms grass GPU p95**, and **0.145 ms grass preparation/upload CPU p95**.
Storm traversal measured **2.361 ms frame p95**, **1.240 ms total GPU p95**,
**0.006 ms weather GPU p95**, and **0.023 ms weather CPU p95**. Its largest measured
weather pass, including shelter refresh, was **0.066 ms GPU**. Cached fixed-view
weather passes are cheaper because they usually draw only the flakes.

For a Performance grass view, total GPU mean was 0.975 ms with grass and 0.861 ms
without it; the directly timed grass pass averaged 0.053 ms. For the icy gate,
total GPU mean was 0.939 ms with storm weather and 0.929 ms with weather off.
Differences this small are sensitive to run-to-run scheduling and clock variation.
The weather timer covers shelter and particles; snow material work remains in the
opaque scene timer. Grass CPU timing covers tile scheduling/uploads, while worker
preparation also runs on background threads.

The **8.33 ms p95 target is met in these sampled views and traversals**. This is
not an all-frame guarantee: isolated warmed frame spikes of roughly 17–32 ms
remain. Cold loading/shader/resource setup produced much larger stalls, up to
about 1.4 seconds, and is excluded from the steady-state figures. The F3 physics
wireframe lifecycle run is a correctness test, not a performance benchmark.

Grass GPU buffers are about 35 MiB in the sampled Performance views and 110 MiB
in High views, depending on eligible terrain. Weather's map plus particle buffers
use approximately 0.4/1.5/4.9 MiB for Low/Performance/High. The Low grass logs from
the first matrix conservatively counted 48 bytes per vertex; the final diagnostic
uses the actual 36-byte position/normal/UV/colour layout.

Local artifacts:

- [Capture gallery and benchmark links](artifacts/environment/index.html)
- [Machine-readable measurements](artifacts/environment/benchmarks.json)
- [Full regression log](artifacts/environment/regression.log)
- [Final focused checks](artifacts/environment/final-focused.log)
- [Delayed two-client checks](artifacts/environment/network.log)
- [Low shelter/edit check](artifacts/environment/final-clip-low/icy-weather-shelter-low.log)
- [High shelter check](artifacts/environment/final-clip-high/icy-weather-shelter-high.log)
- [Lifecycle run](artifacts/environment/final-lifecycle/client.log)

Limitations: grass has wind only and casts no individual shadows; distant cover
fades into terrain shading. Snow relief changes shading/UVs only, not silhouettes
or collision. Shelter is the highest surface in an overhead map, so coarse edges
are approximate and wind does not carry flakes through open doors into rooms.
There is no snow accumulation, footprint system, frozen water or building snow
geometry. Existing extracted textures are reused without source-asset changes.

Windows framework-dependent client/server packages were refreshed with
`scripts/Publish.ps1 -Client` in `artifacts/publish/client-win-x64` and
`artifacts/publish/server-win-x64`. Packaged grass and snowfall shader hashes match
the source assets. The final lifecycle repeat passed after quality changes were
changed to retain existing grass tiles while replacement densities are prepared.

## Planet clocks, volumetric atmosphere and water — 2026-11-07

The compatibility policy changed during this release: generator 3 is required.
Generator 2 is refused with a clear regeneration instruction and remains on disk.
Use a new universe (for example `Run-Server.ps1 -Universe atmosphere-v3`). No old
terrain or building migration is claimed. Protocol 5 is required on both peers.

Validation completed on Windows x64 / RX 9070 XT:

- Release solution: zero warnings, zero errors. Windows client/server packages
  refreshed under `artifacts/publish/` and used for the final water captures.
- **756 headless checks passed.** New checks cover deterministic/persistent clocks,
  offline phase progression, aliases, old-world refusal and preservation, deep
  lakes, bounded currents, negative-coordinate queries, canonical water payloads,
  shoreline clipping and dry-edge tapering, wet-bed LOD retention, swimming,
  dive input, rotated sleeping crate buoyancy and checkpoint replay.
- **Five network checks passed** with two real ClientSession implementations,
  one through the existing 100 ms RTT / jitter proxy. Both received authoritative
  swimming and buoyant crate state. Maximum observed UTC disagreement: 31.3 ms.
  These are headless clients, not a two-window visual synchronization test.
- Fixed 00:00, 06:12, 12:00, 17:48 and 18:30 captures completed for temperate,
  desert and icy worlds. Night uses the galaxy inspection view; the other captures
  face the sun. Winter retains overcast, gust lighting and snowfall.
- Lifecycle checks exercised all graphics qualities, resize, minimize/restore,
  planet switches and reconnect. The final run additionally asserts zero
  per-world volume memory after disconnect; atmosphere profile LUTs are deliberately
  retained in a bounded three-profile cache until renderer shutdown.

### Warmed 1080p performance

Performance preset, VSync off, ten seconds warm-up and five seconds measured per
water view. Sky/weather/water time is fixed for repeatability. All values below
are milliseconds; subsystem columns are GPU p95. GPU queries exclude copies,
UI and presentation; frame time includes them. CPU render excludes streaming work.

| View | Frame p95 | GPU p95 | CPU render p95 | Sky | Cloud | Fog | Water | Frame max |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| lake-final | 5.869 | 4.040 | 3.376 | 0.007 | 1.322 | 0.180 | 0.320 | 7.398 |
| river-off | 1.720 | 1.428 | 1.039 | 0.006 | 0.000 | 0.065 | 0.090 | 23.550 |
| river-on | 3.198 | 3.054 | 1.239 | 0.007 | 1.459 | 0.185 | 0.161 | 3.362 |
| underwater-final | 2.575 | 2.450 | 0.653 | 0.007 | 1.413 | 0.165 | 0.148 | 2.711 |

The river off case disables clouds, volumetric fog and screen-space water
reflections while retaining atmospheric sky lookup tables. It is not the old
renderer. Paired river GPU p95 rose from 1.428 to 3.054 ms (1.626 ms). All measured
water-view frame p95 values were below 8.33 ms. The separate GPU p95 columns are
not additive percentiles. A 23.55 ms isolated frame occurred in the off case;
this does not establish spike-free performance. The larger lake submits about
870 draws and has more CPU cost than the river.

Cold profile initialization still blocks the render thread for roughly 1.3–2 s
in these captures. This includes LUT precomputation and first-use driver work;
it is excluded from warmed results and is not an acceptable seamless-travel
latency target. Cached profile revisits do not recompute LUTs.

Evidence: `artifacts/atmosphere/regression-final.log`, `network-final.log`,
`build-final.log`, `publish.log`, `lifecycle-final/`, `final/`, `release-check/`,
`measurements.json`, and the local screenshot gallery `artifacts/atmosphere/index.html`.
The three-preset sky matrix predates the final shoreline taper; final river/lake/
underwater captures use the packaged final geometry and shaders.

### Remaining limits

This is a working prototype, not exhaustive hydrology/visual qualification across
all seeds. Shorelines retain metre-scale shapes; some slopes and lake mouths need
further art tuning. Screen-space reflections can miss objects or show fine
artifacts at grazing angles and screen edges; the sky is the fallback. Water has
no geometric waves or caustics. Buoyancy uses a local plane with sampled bank
coverage. Underwater transport is approximate.

Fog uses RGB scattering with scalar extinction and no temporal history. Dense-mist
exclusion is limited to the nearest 32 closed roof cells, not a general enclosed
volume solver. Distant terrain shadows are coarse; dynamic shadows use the normal
cascades. Cloud multiple scattering and history reconstruction are approximations.
The renderer uses high texture-unit indices and has only been qualified on the
listed GPU, not minimum OpenGL hardware. Linux native buoyancy, prolonged traversal,
all enclosure/window cases, every seed's river junctions, and hour-long GPU memory
soaks have not been newly qualified in this release. Existing automated migration,
crash-save, building and networking regressions passed; they are not a disk-full
retest of every new rendering path.

## Tree Pack 3D and forest placement - 2026-11-07

Five user-supplied OBJ models were imported with full near branches, two distance
LODs, two-sided foliage lighting and wood-only collision. All 30 mesh parts were
checked for finite data, unit normals, enclosing bounds and valid textures; all
10 source OBJ/MTL hashes still match the originals recorded by the importer.

Seed 7319 now resolves 3,213 temperate trees (all eight temperate variants),
1,232 icy trees (the four winter variants), and 10 sparse desert saplings.
Checks cover deterministic cache/order reloads, negative-world chunks, unique
IDs, upright trunks, dry roots, gate exclusion, dense stands, open meadows,
canopies crossing chunk boundaries, wood collision and scenery tombstones.

Release client/server build: zero warnings/errors. The full regression run passed
778 checks, including two-client building/editing, demolition, late join and planet
returns. Building networking uses a persisted cleared work area so its placement
fixture does not depend on a particular forest distribution. An additional focused
54-check environment/water run passed after adding explicit old-scenery refusal
and save-preservation assertions. Windows client/server packages were refreshed.

Final warmed 1920x1080, Performance, VSync-off, RX 9070 XT captures, fixed noon,
visual time 12, five-second measurement windows:

| View | Frame mean / p95 / max (ms) | GPU mean / p95 (ms) | Draws |
| --- | --- | --- | --- |
| Temperate dense forest | 6.603 / 6.751 / 7.610 | 6.501 / 6.626 | 1,374 |
| Temperate tree 4 nearby | 4.617 / 4.756 / 4.976 | 4.484 / 4.607 | 741 |
| Icy forest, automatic snowfall | 3.868 / 3.994 / 4.135 | 3.735 / 3.874 | 128 |

Distant tree transform batches occupy about 76.7 MiB of additional reusable mesh
storage, included in the F3 geometry estimate. FishGfx remains unchanged. Colour,
sun shadows, gate shadows and snow shelter use the same transforms. The initial
unbatched dense view was CPU-heavy; final batching reduces its 4,315 draw calls to
1,374. Initial measurements ran alongside regression work and are not a clean
isolated before/after performance comparison. Final measurements were isolated.

Logs and captures: `artifacts/tree-pack-final`, `artifacts/tree-pack-winter`,
`artifacts/tree-pack-regression-final.log`, `artifacts/tree-pack-compatibility.log`,
and `artifacts/tree-pack-publish.log`. All five new variants were inspected in
initial captures; the final pass rechecked intact near branches and forest/winter
views. Fixed-view results do not establish worst-case performance across every
seed or long traversal. Source pack materials are untextured polygon foliage,
not photogrammetry. No tree wind/deformation was added.

Scenery version 2 requires a fresh universe, for example `--universe forests`.
The cache key and physics/content compatibility include the new catalogue.
Old saves are refused with regeneration instructions and remain on disk; trees
are never silently rerolled around saved structures.

## Movement and predicted crate pushing (2026-11-07)

Release build: zero warnings/errors. Full regression: 803 checks passed, including
prediction reconciliation, topology changes, persistence and network editing.
The focused movement suite covers interpolation at 30/60/120/144/240 render Hz,
box rotation, shared player/body timing, correction decay, discontinuity resets,
clock jitter, remote interpolation, and physical pushing of two grounded crates.
The two-client network scenario uses 100 ms RTT with +/-10 ms per-direction jitter.
It verifies immediate local prediction and final player/prop convergence within
2 cm and 2 degrees after shared pushing. In one 82-sample contact segment, raw box
motion had seven held samples versus zero rendered holds; maximum player and box
steps were both 6.42 cm. Actual headless sampling was around 16 ms, not 144 Hz.

The graphical route was run at 1920x1080, Performance, VSync off on RX 9070 XT.
During 5.4-6.8 seconds, 273 steady-push samples had a median 3.91 ms frame interval.
Raw player positions held on 188 frames and raw box positions on 186; both rendered
positions moved on every sample. The crate travelled 4.83 m. Capture frames/stalls
above 50 ms were excluded from this motion comparison. Screenshots show the crate
in contact ahead of the player; they alone are not evidence of temporal smoothness.

The final three-second traversal window measured frame mean/p95/max
4.474/8.417/202.626 ms, GPU mean/p95 4.085/6.056 ms. This moving, streamed run includes
capture/streaming work and narrowly misses the 8.33 ms p95 target. Initial loading
also stalled; this change does not claim to eliminate renderer/streaming hitches.

Logs: `artifacts/movement-build.log`, `artifacts/movement-tests.log`,
`artifacts/movement-regression.log`, `artifacts/movement-latency.csv`.
Graphical captures, frame trace and logs: `artifacts/movement-visual`.
Repeat with `scripts/Test-Movement.ps1`; run focused tests with the Release test
executable and `--movement-only`. Windows client/server packages are refreshed.
Both ends must be restarted because the shared push-strength compatibility hash
changed. Existing world saves remain compatible with this movement-only change.

## Visual Studio startup profile (2026-11-07)

Reproduced Debug startup with the VS launch profile: the default universe had
terrain 2/scenery 1 locations and exited with the expected regeneration refusal.
Changed only the development launch universe to `dev-terrain3-scenery2`, retaining
`--dev-planets`. Debug profile startup and a second launch both reached Listening
on port 7777 with 49 terrain colliders. The same location ID survived the restart.
The old default save remains preserved. No movement/physics change was needed.

## Snow volume and uphill water fixes (2026-11-07)

Reproduced the flat ground-snow ellipses and dark distant water sheets in the icy
7319 world. Before/after captures share the same retained diagnostic universe,
noon override, visual time 12 and Performance settings. The repaired water retains
fog when above a dry camera; ground snow is now a scattering volume rather than
stretched sprites. Captures: `artifacts/snow-water-before/baseline` (original) and
`artifacts/snow-water-before` (updated). River close-up and gate views inspected.

Release solution build: zero warnings/errors. Environment checks: 28 passed.
Atmosphere/water checks: 54 passed. Logs: `artifacts/snow-volume-environment-tests.log`
and `artifacts/snow-volume-water-tests.log`. No authoritative simulation or save
format changed in this update.

GPU readback checks in `artifacts/snow-volume-shelter` passed with the fixture roof
present and removed: sheltered interior, exposed animated density, matching wind
at 0/1024 metres, zero density above the height cutoff and when disabled. The
existing precipitation shelter probes passed too. Fixed-time shelter captures
are static by design. Live captures in `artifacts/snow-volume-live/motion-*.png`
use unfrozen animation; the shader probes separately verify density evolution.
Icy and temperate underwater views also ran in `artifacts/snow-water-underwater`.
Underwater reflections retain the existing approximate environment fallback.

Warmed RX 9070 XT, 1920x1080, Performance, VSync off, live storm gate view:
frame mean/p95/max 5.806/5.890/9.666 ms; GPU mean/p95 5.699/5.767 ms.
Fog (including snow volume) GPU p95 0.186 ms; water 0.056 ms; falling snow 0.007 ms.
No additional render-target memory. A fixed-time comparison measured 5.826 ms
frame p95 before and 5.811 ms after; that difference is within run variance.
These short warmed runs exclude startup/loading and are not a worst-case bound.

Lifecycle check `scripts/Test-EnvironmentLifecycle.ps1` passed: resize,
minimize/restore, Low/High/Performance changes, three planet switches and reconnect.
Disconnect reported zero world-owned weather/volume target memory. Logs and
captures: `artifacts/snow-volume-lifecycle`. Windows packages refreshed with
`scripts/Publish.ps1 -Client` (`artifacts/snow-volume-publish.log`).

## Player horizon ripple repair (2026-11-07)

Release client/server build succeeded with zero warnings/errors. The focused
horizon/network run passed 53 checks, including both-direction crossings by two
clients at 100 ms RTT/jitter, quiet initial observations/corrections, entry-only
player overlap, expiry, epoch resets, prop pushing and post-contact convergence.
New CPU assertions verify player entries supply a 24-vertex GPU contour and do
not become persistent obstacles. Log: `artifacts/player-wave-network.log`.

The GPU diagnostic walks a ground-height player through the real contact and wave
renderer with no props. At 256/384/512 grids it observes exactly one entry, no
persistent obstacles and a visible disturbance. Its encoded height reaches the
10 mm readback ceiling at each quality (a lower bound, not an exact peak).
Existing no-obstacle baseline, packet reflection, blocked transmission and resting
prop-boundary diagnostics still pass at all three resolutions. Logs:
`artifacts/player-wave-fixed`. Original weak-field measurements are preserved in
`artifacts/player-wave-before`; the probe scale changed for the repaired amplitude
check, so their energy values must not be directly compared.

Live opposing-player visual fixture and six captures inspected in
`artifacts/player-wave-visual`. 1920x1080 Performance/VSync-off, RX 9070 XT:
warmed frame mean/p95 3.876/3.978 ms; wave GPU p95 0.026 ms. Short fixture timings
are not worst-case performance guarantees. Client-only visual change: no world
regeneration, server-authority or networking changes. Windows packages refreshed;
see `artifacts/player-wave-publish.log`.

### Manual player crossing follow-up (2026-11-07)

Real Debug client movement, server authority and prediction reproduced contact
events and nonzero GPU waves; the previous velocity-only pulse had delayed onset.
Player entries now displace the existing surface immediately. Fixed asymmetric
capsule vertices and sideways aperture entry as well. Base animation remains 1.40.

`artifacts/manual-horizon-firstperson` contains a real-input walk/overlap/cross/
return run with GPU readback and six first-person captures. Diagnostics affect
frame times; these logs are not performance measurements.

`artifacts/manual-horizon-shading`: all three quality grids produce 31.8-32.2 mm
peak displacement within 35 ms. Comparing the actual membrane shader at identical
time/view changes 1,122 / 1,569 / 2,284 pixels at 256 / 384 / 512 resolution.
No-obstacle, reflected-packet and stationary-prop GPU checks also pass. CPU contact
checks cover capsule symmetry/convexity, sideways entry, both directions, no
repeats while overlapping, correction suppression and resets.

Follow-up verification: 57 horizon/network checks passed with two delayed clients
(`artifacts/manual-horizon-network.log`). Debug solution build and Release Windows
client/server publish completed (`artifacts/manual-horizon-debug-build.log` and
`artifacts/manual-horizon-publish.log`).

Commit verification (2026-11-07): Release solution build completed with zero
warnings/errors, and the complete regression suite passed all 809 checks.
Logs: `artifacts/commit-release-build.log`, `artifacts/commit-regressions.log`.


## Spatial audio � 2026-11-07

Implementation and controls: [AUDIO.md](AUDIO.md). Protocol is now 7. FishGfx
and miniaudio pins remain unchanged. Source assets were not edited.

- Release solution: zero warnings/errors in `artifacts/audio-final-build.log`.
- Native/managed audio suite: 30 passed (`audio-final-tests.log`), including actual
  offline stereo energy, moving sources, low-pass attenuation, playback seek,
  stale handles, streams, cue limits and unavailable/missing resources.
- Gameplay sound checks: 194 passed (`audio-event-tests.log`). These include
  two-sided wall obstruction/open gaps, cosmetic queue overflow without a
  disconnect, safe socket closure, invalid/duplicate events, and 180 exact
  physics comparisons with contact observation on/off.
- Existing full regression run: all 809 checks passed (`audio-galaxy-tests.log`).
- Two-client delayed galaxy/dialing suite: all 196 checks passed
  (`audio-dialing-network.log`), including accepted transition retention,
  duplicate suppression, failed reset, disconnect, late join and timeout paths.
  The harness now steps prediction at 60 Hz instead of its former 100 Hz;
  a socket-close GetStream race exposed during this check was also fixed.
- Two rendered endpoint capture (`artifacts/audio-endpoints`): exactly seven
  incoming lock sounds, one opening and shutdown at the receiver; no source
  DHD glyph sounds there. Source and receiver have separate WAV mixes.
- Failed self-dial (`artifacts/audio-failed`): one abort cue and a captured
  `Reset-0-0` frame with no symbols, membrane or active lights.
- Client and server Windows packages and Linux server refreshed. Dependency
  and file audits confirm neither server package includes Audio or its native
  runtime; the client has the DLL, all 46 recordings and retained notices.

Warmed 1920x1080 Performance, VSync off, RX 9070 XT, ten seconds warm-up then
30 seconds sampling. The controlled runs reuse the same saved world, noon,
fixed visual time 20 and DHD inspection view. Audio-on includes diagnostic
mix capture. Values are milliseconds unless stated otherwise.

| Run | Frame p95 | GPU p95 | Render CPU p95 | Audio update p95 |
|---|---:|---:|---:|---:|
| Audio on | 2.449 | 2.313 | 0.674 | 0.0211 |
| Audio off, isolated repeat | 2.453 | 2.315 | 0.724 | � |

Logs: `artifacts/audio-controlled-on/client.log` and
`artifacts/audio-controlled-off-final/client.log`. This is not evidence that
sound improves rendering; the difference is within run-to-run noise. The
single-client frame target of 8.33 ms p95 and audio-update target of 0.5 ms p95
were met in this scene. Sample maxima were 180.3/178.6 ms and initial loading
still incurred multi-second stalls; these are not hidden by the p95 result.
An earlier off run overlapped build/test activity and is not used above.

The audio-on run recorded 45.509 seconds: peak 0.2020, RMS 0.01046 and zero
samples above full scale (`artifacts/audio-mix-analysis.json`). Mixer thread
CPU was 328.12 ms over that capture, about 0.72% of one core; it excludes
resource-worker decode CPU. Estimated active audio/BVH/capture memory was
21,126,744 bytes (20.15 MiB), with zero reported underruns or dropped starts.
Memory includes the 8-second diagnostic ring; it is not an allocation profiler.
Wrapper underrun counters do not certify absence of hardware glitches.

Concurrent rendered clients are a separate load: source audio p95 0.0247 ms,
receiver 0.0316 ms; source frame p95 9.192 ms and receiver 8.254 ms. This does
not meet the single-client frame target on both simultaneous instances and
must not be substituted for the isolated performance result.

`Record-Audio.ps1` produced the 30-second `artifacts/audio-video/gate-audio.mp4`
with video and native mixed audio. Extracted frames confirmed the preparing and
open surfaces. This capture is excluded from performance measurements. Its mux uses an approximate
wall-clock offset, not a sample-accurate audiovisual synchronization instrument.

Remaining manual acceptance: subjective headphones/speakers balance, physical
device unplug/replug, and a comprehensive live movement/underwater/shelter tour.
The automated checks and captures above do not certify those. Source SoundCue
metadata is only partially readable; runtime gains/loops are explicit tuning.
Scraping is a subtle conditioned substitute recording. No reverb, HRTF, music,
voice chat or teleportation sound is implemented.


## Tab address book and GUI glyphs � 2026-11-07

- Release solution build passes with zero warnings/errors (`artifacts/address-book-build.log`).
- Twelve address-book checks pass (`artifacts/address-book-tests.log`): visit ordering,
  pinned selection across worlds, universe isolation, invalid addresses, persistence,
  corruption recovery and ordinary-client current address access without developer routes.
- Live home-to-Earth relocation preserves Home Base in the closed-book HUD and adds
  Earth to history. Captures cover the open book at 1920x1080, HUD, and 960x600 layout.
  Final packaged-client captures are in `artifacts/address-book-final/`; the inspection
  flag `--address-book-check` also selects the current world and captures its HUD.
- All 39 source glyphs were inspected in `artifacts/gui-glyph-review.png`.
  `GlyphRenderer` shares a padded alpha atlas across book, HUD and accepted DHD
  sequence displays. Mipmapped linear filtering avoids nearest-sample jagged edges.
- Tab/Escape/X close the modal; book input is consumed before DHD/build handlers,
  preventing click-through. Selection does not submit commands or teleport.
- Windows client/server and Linux server packages refreshed. Source assets and
  FishGfx are unchanged. No new wire fields or protocol bump are needed: existing
  `galaxy-info` now sends the scoped current address/name to ordinary players,
  while named developer destinations remain developer-only.


## Persistent low ground mist — 2026-11-07

Implemented an independent low mist layer for icy planets. The terrain/water-only
height map excludes roofs and trees; density remains present in light snow.
The shared fog integral handles lighting, water and snowfall attenuation.
Room exclusions now invalidate on geometry changes and include the floor-level
20 cm previously omitted by their lower bounds.

Release solution build completed with zero warnings/errors. Existing environment
checks passed (28), as did atmosphere/water checks (54). Ground-mist GPU readbacks
passed with roofs present and removed at 256², 512² and 1024²: persistent density,
height cutoff, smooth falloff, animation, periodic wrap, disabled density, missing
surfaces, coverage fade, floor-level room exclusion and no mist above the roof.
The sampled lower-layer density gave a 5.84 m local half-transmission estimate.

Captured six views at noon and midnight for all three quality presets (36 views).
Artifacts: `artifacts/ground-mist-after`, `artifacts/ground-mist-matrix`,
`artifacts/ground-mist-motion` and `artifacts/ground-mist-storm`.
The disabled mist reproduced the original snow-view scene pixels exactly in
rows 100–979. Weather-off, volumetrics-off, mist-off, temperate and desert runs
reported zero ground-mist map allocation. One disabled gate comparison timed out
connecting before rendering; its isolated retry passed.

The lifecycle run started on an icy planet and passed quality switching, resize,
minimize/restore, planet relocation and reconnect. Disconnect reported zero
weather and volume target memory. Logs: `artifacts/ground-mist-lifecycle`.

Initial matched Performance fog-pass increases were 0.012–0.026 ms across six
views. Live traversal measured ground-map GPU p95/max of 0.041/0.071 ms and
preparation CPU p95 of 0.129 ms. The full moving scene measured 9.622 ms frame p95;
this includes existing streaming and lighting work. Measurements use 1920×1080,
VSync off, RX 9070 XT, and short warmed runs, not worst-case bounds. Map memory
is 0.25/1/4 MiB for Low/Performance/High. Detailed capture measurements are in
`artifacts/ground-mist-matrix/measurements.json`.

The local Windows client package was refreshed at
`artifacts/publish/client-win-x64` (`artifacts/ground-mist-publish.log`).
Performance optimization and further benchmarking are deferred at the user's
request. No networking, physics, terrain generation or save-format changes.

## Dense meadows, reduced drainage and physical props — 2026-11-07

Delivered generator 4 and protocol 8. Start a new universe, for example
`StargateGame.Server.exe --universe meadow-v4`. Previous saves are left untouched;
this build rejects their old generator instead of regenerating their terrain in place.
FishGfx remains pinned.

### Implementation and regression results

- Persistent GPU tuft records replace distance-triggered grass mesh rebuilding.
  Low/Performance/High use 24/36/48 tufts per square metre, four blades per tuft,
  with detailed ranges of 8/12/16 m and simplified ranges of 32/48/72 m.
  Coverage, meadow colour and wind are shared with the filtered distant material.
  The procedural detail texture has a reproducible generator and hash manifest.
- The 32-seed corpus per preset measured median channel-length reductions of
  **64.90% temperate**, **66.96% icy** and **70.75% desert dry channels** against
  the previous 160-cell network. Tuned thresholds are 1,280/1,360/1,600 cells.
  Tests verify downhill downstream continuity. JSON measurements and before/after
  overhead SVGs are in `artifacts/meadow/rivers-*`; grey is the original network,
  blue is the retained network. Lake selection is unchanged; closed lakes do not
  acquire artificial flowing outlets.
- Imported wooden crates and red/black barrels include base colour, normals and
  metallic/roughness maps. Source transforms and display offsets are removed.
  The shared barrel collider is a 32-sided, four-ring bevelled convex hull;
  physics, picking, buoyancy and membrane slicing consume the same definition.
- Character ground contacts identify wooden/metal props and hard surfaces before
  canonical terrain classification. Streamed scenery uses triangle user data to
  distinguish tree/rock contact from terrain. Cadence uses motion relative to the
  support. Eight imported recordings supply wood, leaves, stone, mud and gravel;
  water and filtered snow/sand/metal fallbacks remain available.
- The complete Release regression executable passed **810 checks**
  (`artifacts/meadow-regressions-complete.log`). New meadow/prop checks including
  the two-client latency test passed **144 checks**. Both clients converged to
  authoritative prop poses within 3 cm and 2 degrees after barrel pushing under
  100 ms RTT plus jitter. Native rolling, buoyancy, support materials, convex
  picking and rotated membrane contours also passed.
- All **31 audio unit tests** passed, including non-repeating step variants.
  Existing focused runs passed movement (75), horizon (48), audio events (194),
  environment and atmosphere/water (54) checks. Imported cue hashes were checked
  against provenance. Device playback/capture of wood steps succeeded; this is
  not a claim of a complete subjective audition of every surface transition.
- An initial full run missed a streaming preparation timing assertion while
  graphics checks were running. Its isolated retry passed. A later full run
  exposed a building fixture that walked away from the reserved approach and
  never reached an editable position; the fixture now exits through the reserved
  southern approach. Building checks passed (129), followed by the complete run.

### Graphics and performance

RX 9070 XT, 1920×1080, VSync off, Performance, fixed noon grass view; 10-second
warm-up followed by approximately 20 seconds of samples:

| Measurement | Grass off | Grass on |
|---|---:|---:|
| Frame p95 | 3.732 ms | 4.024 ms |
| Total GPU p95 | 3.592 ms | 3.902 ms |
| Render CPU p95 | 2.068 ms | 2.023 ms |
| Grass GPU p95 / max | 0 / 0 | 0.180 / 0.264 ms |
| Grass preparation CPU p95 / max | 0 / 0 | 0.070 / 0.706 ms |

The enabled view had 172 tiles and 304,896 submitted triangles. Its original
memory counter reported 11.1 MiB; the counter was then corrected to include the
1.33 MiB mipmapped detail texture (approximately 12.4 MiB for the same resources).
Upload bytes and shared-budget preparation time are visible in F3. A separate
isolated upload-time percentile was not recorded. These CPU measurements include
routine grass preparation, not a worst-case streaming traversal.

Logs: `artifacts/meadow-grass-off.log`, `artifacts/meadow-grass-final-2.log`.
An additional enabled run had frame p95 **30.348 ms**, maximum **109.863 ms**, and
GPU p95 **18.168 ms** (`artifacts/meadow-grass-final.log`). Its cause was not
established. The clean repeat meets the 8.33 ms target for this view; it does not
establish a worst-case bound or explain away the recorded spikes. Cold startup
and streaming are excluded from the warmed measurements.

Captures:

- `artifacts/meadow/grass-final-2/Explorer-1080p.png`: dense hillside and river bank.
- `artifacts/meadow/grass-off/Explorer-1080p.png`: matching disabled comparison.
- `artifacts/meadow/props/Explorer-1080p.png`: wooden stack and both barrel variants.
- `artifacts/meadow/lifecycle/`: Low/High/Performance, resize and reconnect captures.

The lifecycle run completed quality switches, resizing and reconnect. Shared
blade templates/detail remain reusable; world records and coverage are cleared.
The props view measured audio update p95 0.0274 ms with zero underruns/dropped
sounds. Wider manual traversal, every material's listening balance, and a full
multiplayer audiovisual walkthrough remain unverified.

### Delivery

Windows Release client and server packages were refreshed with catalogue
verification (`artifacts/meadow-publish.log`). Runtime models, material maps,
step recordings and the meadow texture are included. Asset source files remain
untouched. Model CC-BY credits are recorded in `THIRD-PARTY-NOTICES.md`; the new
footstep files have no supplied license file, so provenance records their rights
as unspecified rather than borrowing the model/code licenses.


## SG-1-inspired opening and shutdown (2026-11-07)

References and render-only capture controls are in GRAPHICS.md. The authored
transition now has inward formation, a hollow rolled liquid surge, retraction
and settling, plus a full-aperture white flash and mottled shutdown. It remains
a procedural approximation of filmed water, not a fluid simulation. Existing
server timing, audio scheduling, physics and settled membrane settings are unchanged.

Release client build passed with zero warnings/errors. The focused
`--gate-transition-only` suite passed all 74 transition/contact checks, including
scheduled visibility, timestamp agreement, bounded illumination, shutdown
completion and contact-history resets. Runtime shader compilation succeeded.
The previous broad regressions are recorded above; they were not rerun for this
render-only revision.

A real client/server `--dial-check` completed accepted symbols, origin, preparation,
opening, open and an accepted orb close. Source players stayed in place. Evidence:
`artifacts/kawoosh-network.log` and `artifacts/kawoosh/network/`.
Separate fixed-step front, 45-degree and rear sequences inspected the rolled
silhouette, fixed ring fit, flat rear coverage, settled surface and disappearance.
The final flash/breakup is captured in `artifacts/kawoosh/front-final/`; earlier
45-degree and rear sequences predate only that closing-flash adjustment.
`artifacts/kawoosh/peak-bench/Explorer-1080p.png` shows the final peak material.
`artifacts/kawoosh/front-preview.mp4` presents the final front sequence with brief
holds before opening, at the open surface, and after closure. It is a render-only
capture without audio, not a real-time multiplayer recording.

Warmed RX 9070 XT, 1920x1080 Performance, VSync/audio off, fixed noon and sky,
`horizon-45`, 15-second samples after 10 seconds warm-up:

| State / log | Frame p95 / max ms | GPU mean / p95 / max ms | CPU render p95 ms |
|---|---:|---:|---:|
| Peak opening, `kawoosh-peak-bench.log` | 4.873 / 66.679 | 4.675 / 4.746 / 52.271 | 2.913 |
| Settled repeat, `kawoosh-open-repeat.log` | 4.836 / 6.098 | 4.590 / 4.712 / 4.807 | 2.185 |
| Settled first run, `kawoosh-open-bench.log` | 14.953 / 143.380 | 5.818 / 9.367 / 24.244 | 8.083 |

The clean comparison's GPU mean difference is 0.085 ms and p95 difference 0.034 ms.
These are whole-scene differences, not isolated gate-pass timings or a guaranteed
cost bound. The first settled run was noisy; its cause is unestablished. The peak
run also had a recorded spike. The clean runs meet the 8.33 ms p95 target in this
view, not in every scene. Sequence PNG captures were excluded from this comparison.
Both fixed-state runs report 185.7 MiB scene targets; transitions add no render targets.

Windows client/server packages were refreshed through `scripts/Publish.ps1 -Client`;
see `artifacts/kawoosh-publish.log`. Reference videos are analysis-only ignored
artifacts and are not included in the packages. Separate two-client jitter and
manual audio synchronization checks were not repeated for this visual change.


## Realistic tree integration and larger tree placement (2026-11-07)

The four Nicholas-3D trees use 50,000 triangles nearby and approximately
16,700 / 8,000 / 3,000 beyond 40 / 90 / 180 m. All ten active trees now use
1.5 times the previous scale ranges. Leave-Tree-01a and RealisticTree_01/02
sink an additional 0.6 m times instance scale. Meshes, LODs, bounds, and wood
collision share each placement transform. Tree Pack 3D remains retired.

Validation evidence is in `artifacts/realistic-tree-validation/`:
- `build.log`: full Release solution build, no warnings or errors.
- `tests.log`: `StargateGame.Tests.dll --trees-only` passed after scaling;
  deterministic placement in every biome, all ten models, retired-pack exclusion,
  four LOD boundaries, packaged collision, trunk contact, clearing, and replay.
- `assets.json`: imported triangle totals; mesh lengths, finite vertices, normals,
  shared bounds, and material texture references were checked.
- Actual 1080p client/server captures checked all four imported models.
  `capture-scaled.ps1` refreshes `trees`, `realistic-tree-1`, and `forest` after
  the size/root-depth change. Other model-specific captures precede scaling.
- `publish-server.log` and `publish-client.log`: local Windows packages refreshed.

Tests use a separate local server, port 7827, and isolated saves. The existing
running development server was not restarted; both peers must restart with
matching updated binaries. Linux packages were not refreshed in this pass.


### Fixed planting depths (2026-11-07 follow-up)

Supersedes the scale-dependent sinking above. Each tree is positioned relative
to terrain sampled directly beneath its trunk, with a fixed world-space offset:
Leave-Tree-01a 1.5 m; RealisticTree_01/02 0.15 m; all other active models 0.10 m.
The previous downhill-neighbour adjustment and scale multiplier are removed.
The 1.5x tree sizes and four-level new-tree LODs are retained.

`artifacts/realistic-tree-validation/fixed-depth-build.log` records a successful
full Release build. `fixed-depth-tests.log` passes the tree suite, including a
new check of exact planting depth across all generated tree sizes and slopes
in all three biomes. Fixed-depth capture and Windows package logs are retained
with the same prefix. The user's running development server was left running.


## Streaming and automatic building corners (2026-11-07)

Protocol 10 adds separate outgoing destination preloads and render-only far tiles.
See STREAMING.md for the implementation, commands, captures, measurements and
remaining limits. Terrain generation and databases are unchanged.

Release client/server builds and Windows publishes passed. Targeted results:
18 streaming cache checks, 88 prefab/corner checks, 18 delayed prefab network
checks, 207 galaxy/network checks (also with latency/jitter), 75 movement checks,
144 meadow/prop checks, 57 horizon contact checks, and 14 bounded traversal checks.
The graphics smoke passed resize, Low/High/Performance changes and reconnect.
Corner captures confirm derived trim on the retained 37-piece house; no saved
pieces were added. See artifacts/streaming/*-final.log and earlier suite logs.

RX 9070 XT, 1080p, Performance, VSync off: final warmed frame p95 was 4.312 ms
(building) and 5.729 ms (forest). Local preload/F4 reuse passed, with nine tiles,
0.62 MiB received, 31.73 MiB CPU and 11.59 MiB conservative GPU cache accounting.
Incoming-side tests received zero source-world preload bytes. Cold native cooking
and relocation still produce spikes. The measured 1.331 ms speculative upload
maximum exceeds the requested 0.5 ms ceiling; no hard-ceiling claim is made.
Manual minimize/restore and exhaustive long-duration memory profiling remain
unverified. Full GPU occlusion and actual gate travel remain deferred.

## Procedural settlements (2026-11-08)

See [SETTLEMENTS.md](SETTLEMENTS.md) for the current generator-6/protocol-12
requirements, 96-seed placement census, template coverage and measured captures.
Old universes are preserved, not migrated to new terrain. Tests and inspection
saves use separate directories below `artifacts/settlements`.

- Release solution build: zero warnings/errors.
- Settlement corpus: 32 fixed seeds per preset, deterministic footprints/IDs,
  dry flat foundations, no overlapping footprints, narrower road widths, canonical
  road encoding, chunk-order stability, generated-edit rejection and record bounds.
- Native template checks: all eight houses support the capsule, block at closed
  doors, permit passage at open doors, and provide ceiling/shelter geometry.
  All four desert compound entries passed native capsule traversal.
- SQLite creation/reopen and two-client generated-door synchronization passed.
  Delayed test uses the existing 100 ms RTT/jitter proxy; initial cold preparation
  did time out in an earlier attempt and is retained as a loading limitation.
- Existing building 111, prefab 88, streaming-cache 18, non-network galaxy 165,
  and river 14 checks passed. Full galaxy-network/preload results are listed below
  when available; the non-network count alone does not validate live connections.

The captures are fixed-camera rendering measurements. They do not establish a
worst-case 60-building traversal, all stair/roof combinations, or complete manual
walkthrough coverage of every imported modern exterior/landmark.

Windows client/server packages were refreshed with the 114 templates, 228 near/far
model manifests and supplied attribution. Final captures include slimmer lanes,
rotated houses, desert ruins, a MegaKit city and an enclosed icy room at midnight.
No indoor snowfall or dense exterior fog is visible in that room capture.

The 8.33 ms p95 target is **not met in all views**. Final 1080p Performance frame
p95 was 8.492/6.320 ms for Seahill ground/overhead, 2.610/2.126 ms for desert ruins,
11.491 ms for the city and 12.134 ms for the icy interior. A 60-second warmup and
15-second city sample still measured 12.122 ms frame / 8.888 ms GPU p95, with a
207.138 ms maximum frame. See SETTLEMENTS.md for complete measurement context and
artifact directories. No additional speculative optimization was added.

Final delayed galaxy/network run: **207 checks passed** in
`artifacts/settlements/galaxy-complete.log`, including zero source-world bytes for
incoming clients, outgoing 3x3 preload bounds, prepared-cache relocation reuse,
late joins, timeout/reset and unloaded destination cleanup. Endpoint assertions
now wait for both independently delivered states; closing tests use a bounded
state wait instead of a fixed 900 ms wall-clock sleep. Runtime gate durations
are unchanged. Release builds and Windows publishing succeeded.

## DHD hover, complete prefabs and dusk (2026-11-08)

DHD picking now runs after mouse look and predicted presentation, using the exact
camera ray rendered that frame. One pick supplies both hover and press commands;
server reach and occlusion checks remain authoritative. The hover geometry uses
the full DHD transform. `--view dhd-hover --dhd-hover-check` sweeps the camera and
checks the displayed key against the current camera pick in an unobstructed scene.

Civil twilight now retains diffuse sky fill and a small lower-hemisphere bounce
instead of dropping rapidly toward the night floor. Settings live in
`AtmosphereIllumination.cs`. Direct sunlight, fixed exposure and gate/DHD emission
are unchanged; noon and deep-night fill endpoints remain unchanged. Winter
weather still applies its illumination multipliers.

Release solution build: zero warnings/errors. Prefab checks: 122 passed, including
atomic door-wall placement, opening, frame clearance and assembly demolition at
six rotations. Environment checks: 44 passed. DHD checks: 63 passed.

Runtime captures in `artifacts/usability/captures` include matched 17.8-hour
before/after DHD views, 18.3-hour twilight, midnight, both palette pages, small
window layout and return to placement. The live hover sweep visited 24 distinct
keys without a current-frame mismatch. These are visual/interaction checks,
not a new frame-time acceptance measurement or an end-to-end input latency test.

The delayed two-client complete-prefab run passed all 18 checks, including atomic
placement, door use, late join, planet return, dependent-piece rejection, complete
assembly demolition and persisted revision/receipt checks
(`artifacts/usability/network.log`).

Windows client/server packages refreshed successfully with `scripts/Publish.ps1
-Client`. Use both updated packages together for the changed prefab content hash.

## DHD address guidance and home clock (2026-11-08)

Removed the orange hover outline and hover control label. The selected book
address now drives a local, three-symbol white emission chain with a three-second
pause. Final peak emission is 0.045, reduced to 10% of the preceding revision.
Accepted orange keys and server dialing remain independent. Bloom resources stay
allocated through the pause while the selected-address DHD remains visible.

Release build passed. DHD/environment checks: 75 passed, including ordered peaks,
three simultaneous weights, pause/repeat and selection/location reset. Galaxy
checks without network: 168 passed, including home at 11:00 across UTC changes
and repository restart, with other worlds retaining their day/night cycles.

Live captures in `artifacts/dhd-hints/captures/home` show all six peak positions
and the quiet pause. Client logs report localHour=11.00 without a time override.
The final capture has no hover outline/label and shows the dim three-key chain.
This is a visual and functional check, not a new performance acceptance run.

Compact address HUD: Release client build/publish passed with zero warnings or
errors. `artifacts/dhd-hints/hud-captures/home/HintCheck-1080p.png` verifies the
right-aligned name and complete seven-glyph second row. `--address-hud-check`
supplies UI-only partial/complete sequences; it does not send dial commands.
Crosshair dialing glyphs and all reason/status text have been removed from the
normal HUD. Server validation and diagnostic logging are unchanged.

## Daylight balance and travel planning (2026-11-08)

Release solution build passed with zero warnings/errors in the final isolated
build. Environment checks: 44 passed. Windows client publication refreshed at
artifacts/publish/client-win-x64. Existing ACES-style tone mapping and sRGB
conversion are retained; clear-day input radiance, fill and temperate cloud
coverage were adjusted instead of increasing exposure.

Fixed-time live captures: artifacts/sunlight-review/balanced-11 (gate),
balanced-forest (11:00), balanced-dusk (18:00), and balanced-night (00:00).
The gate view shows warmer sunlit crate tops and clearer ground shadows; night
remains dark. The forest view still shows pre-existing distant terrain gaps and
very pale tree materials; this tuning does not repair terrain/asset problems
or claim a match to the reference game's assets. Cloud wind starts from the
server clock on each launch, so separate sky captures are not pixel-identical.
These short capture runs are visual checks, not performance acceptance; no
new 8.33 ms p95 claim is made.

STARGATE_TRAVEL.md now contains a proposed implementation sequence for one-way
player/prop transfer, readiness, momentum, partial-prop collision, durable
ownership and failure recovery. No teleportation code or protocol change has
been implemented. The native partial-prop collision strategy must pass its
prototype checks before complete prop travel can be delivered.

Final clouds-off comparison: artifacts/sunlight-review/balanced-clear/balanced-clear-1080p.png. Compared with clouds-off-before, sunlit ground and wood retain more colour and less bleaching; cast shadows remain visible.

## One-way travel delivery (2026-11-08)

This supersedes the earlier travel-planning status above. Protocol 13 implements
persistent identity and player/crate/barrel transfer. The source opens without
waiting for destination preparation. All captured entities wait at least two
elapsed seconds; receiver readiness can extend that hold.

Final targeted checks: 40 travel checks and nine persistence checks passed.
The persistence downgrade fixture now removes schema-7 identity/transit tables
before replaying migration; production migration behavior was not changed.
Evidence: artifacts/travel-checks-final.log and travel-persistence-final.log.

Previously completed checks retained for this delivery: movement 75, DHD 75,
event horizon 48, audio 194, prefab 122, streaming cache 18. Warm two-client
travel with 100 ms RTT and jitter verified player/prop holds, source observer
continuity and reconnect at the committed destination. Cold loading opened the
source in 2.187 seconds including its opening animation and extended player
transit to 18.531 seconds. A blocked exit returned player and prop exactly once.
See artifacts/travel-network-final.log, travel-network-cold-final.log and
travel-network-blocked.log. These already-passed runs were not repeated solely
for packaging. Pinned Windows/Linux native runtime builds also passed.

Live entry, fully-white hold and destination frames are retained under
artifacts/travel/visual-20261108-211037. Measured RX 9070 XT, 1920 x 1080,
Performance, VSync off: frame p95 7.319 ms, GPU p95 6.045 ms over the recorded
45-second window after 10 seconds warm-up. Server release took 2.050 seconds.
The run still had a 2327.294 ms maximum frame during loading, 4392.3 MiB process
memory and a 0.896 ms peak speculative upload. These spikes remain limitations;
the p95 result does not establish stall-free travel.

Full packet-loss coverage, process termination at every journal stage, and all
simultaneous closure/reconnect combinations remain unverified. No exhaustive
acceptance claim is made. Release packages live in artifacts/publish; diagnostic
build folders are disposable and are not required to launch those packages.

Package refresh completed: Windows client, Windows server and Linux server at
artifacts/publish/{client-win-x64,server-win-x64,server-linux-x64}. Publication
logs contain no compiler warnings/errors and verify all 19,020,803 catalogue
entries. Packaged client/server binaries, native Jolt libraries and the world
shader match their build/source hashes. Windows package startup and first-frame
render smoke passed; Linux package started with 16 native terrain colliders and
listened successfully. The short Windows smoke contains one initialization
frame and is not a performance benchmark. Evidence: artifacts/travel/package-smoke,
travel-package-linux-smoke.log and travel-publish-*-final.log.

### Vegetation and cooked loading validation (2026-11-09)

Focused vegetation/asset checks passed (52 checks), covering deterministic
placement, ecology/exclusions, representation records, asset bounds, traversal
rejection and corrupt blocks. The cooker checks also pass for base-alpha
preservation, border dilation, colour/normal mips, deduplication and repeatable
incremental publication. Release client and server builds pass.

Visual capture-only checks cover scanned blades, imported accent lighting and the
cooked tree path at fixed 11:00. The graphics fixture now distributes tree species
across its stand instead of accidentally selecting only the small bare shrub.
This changes only the disposable developer fixture, not generated planets.
The final meadow gain is 0.65; the first matched-vegetation capture predates that
last reduction. The corrected cooker preserves opaque alpha at level zero and
only adjusts mip coverage when it differs; bottom-up texture orientation matches
loose loading. All 6,210 cooked blocks are verified before atomic publication.

Evidence: D:/Games/Stargate Network/BuildCache/vegetation/validation and
checks-final.log, cook-alpha.log, publish-final.log. The package is about 1,579 MiB. Further
performance sweeps were stopped at the user's request. Earlier short fixture
metrics are not acceptance measurements for this final revision, and no loading
speedup or 8.33 ms target is claimed. Broad multiplayer/lifecycle testing and
full-world moving-camera comparisons were not repeated for this visual change.

Windows client/server packages were refreshed in `artifacts/publish`; the
published client verified all 6,210 blocks. The final 52 focused checks pass.


### Gate-zone building and arrival fade (2026-11-09)

Removed client/server blanket gate-clearing and spawn-approach build exclusions.
`--gate-build-only` passes five focused checks: persistent location identity,
placement, two-client topology agreement, placement within the old 28 m exclusion,
and replicated demolition. The run also rejected attempts overlapping crates.
Normal reach, obstruction, dry-ground, support and occupancy checks remain.
Arrival readiness no longer requests detailed scenery or waits on unrelated model
jobs; it checks the resident receiving region after the destination frame renders.
This change was code-reviewed and compiled; a new live teleport capture was not
run. No performance sweep was run.

### Blocked prop exit retry fix (2026-11-09)

A blocked receiver searched all 63 grounded positions and 63 airborne positions
on every simulation tick. Each clearance check constructed another native convex
hull. A focused Debug reproduction measured 215.24 ms mean per blocked barrel
search, exceeding the 16.67 ms tick interval by more than an order of magnitude.

Exit probes now share one native hull per slice, retain a managed candidate
cursor, and yield after four candidates or a cooperative 1 ms budget. The server
shares that budget across pending props, schedules oldest probes first, and waits
250 ms after an exhausted search. The ten-second blocked-exit timeout uses
monotonic elapsed time instead of simulation ticks. Grounded placement, airborne
fallback, clearance revalidation, reservations and durable safe return are retained.
One indivisible native query may exceed the budget. Overload diagnostics now show
tick, travel, exit-search timings and the pending-travel count.

Validation used isolated `BuildCache/exit-retry` outputs and owned temporary worlds:

- Debug/Release builds passed (existing nullable warnings in GalaxyChecks remain).
- Focused crate/barrel tests pass for bounded progress, full-hull clearance,
  grounded/airborne parity, changed blockers, exhausted cursors and zero budget.
- Barrel search slices: Debug p95 1.129 ms, Release p95 1.117 ms. These are
  cooperative slice timings, not total frame timings or a hard 1 ms ceiling.
- Existing gate-physics checks and destruction persistence checks pass.
- Real two-client blocked-receiver test passes: player returns safely, the barrel
  returns exactly once, and the gate connection remains valid. No overload warning
  occurred with pending travel in that run. Cold generation/loading still produced
  overload warnings before capture; those are separate from blocked-exit retries.

Logs: `artifacts/exit-retry-before.log`, `exit-retry-sliced.log`,
`exit-retry-release-sliced.log`, `exit-retry-gate-physics.log`, and
`exit-retry-network.log`. No save schema, wire format or gameplay dynamics changed.

## Unattended physics and nearby gate-pair fixture (2026-11-09)

Fixed an early return that stopped a receiving world on the tick its last item
transfer completed. Connected endpoints now keep stepping without players, and
awake or collision-paused props keep their world active after closing. Empty
worlds retain installed collision while streaming terrain around moving props.

`--unattended-travel` passes real network crate/barrel capture and arrival checks:
no receiving players, outward release momentum, advancing fixed ticks, collision
response, unique ownership, and continued airborne motion/gravity after closing.
Grounded crates can stop quickly through friction; this is distinct from a frozen
physics world. Log: `artifacts/unattended-physics-network.log`.

VS defaults now use the transient gate-pair fixture on loopback 17777, with the
client at noon. The gates are 12 m apart, same-facing, on flat terrain. Either
DHD routes any valid six-glyph destination to the other endpoint. Six plus orb
and the normal six-plus-origin-plus-orb sequences both work. Endpoint state,
DHD picking, chevrons, horizon timing, spray, mist and audio are independent.
The eight shadowed gate-light budget is shared between the two fixture gates;
normal one-gate lighting retains all its previous lights. Fixtures never open
normal universe saves. Protocol 18 includes the same-world arrival message.

`--fixture-gates` passes two-endpoint baselines, arbitrary addresses in both
 directions, crate/barrel transfer and unique registry membership, late join,
closing, wrong-way destruction, same-world player travel and continued movement
input after arrival. The same native physics, clipping, shape/exit queries,
carry controller, effect timeline and plume geometry are used. The fixture has
its own transient endpoint/transfer controller instead of galaxy persistence.
Logs: `artifacts/gate-pair-build.log`, `artifacts/gate-pair-network.log`.

The actual Release renderer completed automatic DHD dialing/open/close with audio
and both gates visible, using isolated outputs and an owned ephemeral-port server.
Captures and logs: `artifacts/gate-pair-visual-20261109-222543/`.
Inspected `gate-live-0038.jpg` (staggered opening) and `gate-live-0120.jpg`
(both settled). Recorded 1080p Performance frame p95 2.976 ms, GPU p95 2.169 ms;
this small-scene capture run is not a full-world benchmark. First-use horizon
and spray shader compilation caused 440/127 ms stalls and remains separate from
warmed frame cost. Existing development servers were not stopped or restarted.

## Receiving prop emergence (2026-11-10)

Protocol 19: both client and server must be refreshed. No terrain or database
regeneration is required. FishGfx and Jolt remain pinned.

Focused Release checks passed:
- `--emergence-only`: 37 checks for leading-edge placement, exact mapped momentum,
  rotated crate/barrel colliders, rear/foreground obstruction, clipping snapshots
  and rollback, exposed pickup, no automatic motion, and restart ownership.
- `--travel-only`: 149 checks, including the expanded 64-prop clipping snapshot
  within the 64 KiB frame bound, identity and persistent transfers.
- `--fixture-gates`: two clients, crate/barrel emergence in both directions,
  stalled receiver replication, manual closure waiting, reverse destruction,
  unchanged player transfer, and the 38-minute hard cutoff. The cutoff uses an
  internal test-only connection-age adjustment, not a real 38-minute wait.
- `--unattended-travel`: persistent galaxy source/receiver worlds, two source
  observers, no receiver players, crate/barrel capture and emergence, unique
  ownership, gravity/contact response and continued simulation after closure.

Logs: `D:\Games\Stargate Network\BuildCache\prop-emergence`. No performance campaign
was run. The focused network checks use loopback; they do not establish behavior
under packet loss or the full latency/jitter acceptance matrix.

Release Windows client/server packages refreshed under `artifacts/publish`.
Published cooked content verification passed for all 6,273 blocks. The Release
renderer completed one short clipping inspection; `visual/Explorer-1080p.png`
shows the crate and barrel clipped at the membrane. This was a static visual
fixture, not a captured multiplayer emergence movie. No broad performance run
was performed. The existing nullable warning in GalaxyChecks.cs remains.

## Tree prototype and first-party review, 10 November 2026

Focused results and review scope are in [CODE_REVIEW.md](CODE_REVIEW.md).
The tree path remains an inspection prototype, with captures and one short
paired timing comparison documented in [TREE_PROTOTYPE.md](TREE_PROTOTYPE.md).
Terrain generation, placement, collision assets and saved worlds are unchanged.
Both Windows packages require updating together because the simulation source
formatting changes the existing source-based physics compatibility hash.

### Volume tree lighting correction � 2026-11-10

- `python tools/test_tree_prototype.py`: passed descriptor v2 dimensions/budget,
  source trunk counts, exact loose/cooked leaf mips, sparse density conservation,
  conservative occupancy, padding invariance, weighted linear colour and normal
  normalization.
- Release Windows client/server packages rebuilt; catalogue verified at
  19,020,803 entries and all 7,302 cooked blocks verified.
- Packaged 1080p captures in `artifacts/review-evidence/tree-volume-v2` include
  below/above noon, opposite-side low morning sun and foreground/background
  crate occlusion. The sunlit upper crown no longer turns dark with the camera
  below it; foreground crates remain in front and rear crates are occluded.
- Prototype remains inspection-only, with the same 146.3 MiB volume allocation.
  Close-up voxel shapes and conservative rest-pose shadow proxies remain limits.

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

Lower-canopy fill/scale follow-up: packaged Release client rendered the 1.6x tree with a standing-player reference; shaded lower leaves retain ground/sky fill. Capture: artifacts/review-evidence/tree-volume-fill-scale/Canopy-1080p.png. Windows client/server publication and all 7,302 asset-block checks passed. No additional benchmark run.

### Additional tree volume species � 2026-11-10

- Added second source-derived broadleaf, prepared summer birch and prepared blue
  spruce comparisons. Source assets, scenery placement and collision are unchanged.
- Focused tree checks passed for all species: payload ownership, mip dimensions,
  nonempty density, resource budgets and wood material roles. Cooked-asset checks
  passed alpha, linear/normal mip filtering, deduplication and reproducibility.
- Windows client/server packages rebuilt. All 9,871 cooked blocks verified; archive
  1,613.7 MiB. Only one selected canopy is resident (146.3 MiB for broadleaf/birch,
  36.6 MiB for spruce).
- Six packaged mesh/volume captures and two density-adjusted captures exited
  successfully with empty error logs. Evidence: `artifacts/review-evidence/tree-species`.
- Broadleaf is promising. Birch remains dark/dense; spruce is too soft to preserve
  its needle detail. Both remain inspection experiments. No performance sweep run.

### Randomized volume forest inspection, 2026-11-10

- Seed 7319 produces 66 trees using four shared species volumes (475.4 MiB).
- Release Windows client/server publication and 9,871 cooked block checks passed.
- Packaged final tour exited successfully with empty error logs; retained above,
  near, inside and far views plus an eight-second 1920x1080 walkthrough in
  `artifacts/review-evidence/volume-forest-final`.
- Inspected transformed trunks/crowns, canopy overlap and interior views. Near
  foliage remains chunky and spruce remains soft. No performance sweep run.
- Inspection only: persistent scenery, collision and normal planet rendering
  remain unchanged.

### Ambient occlusion and canopy visibility, 2026-11-10

Implemented spatial horizon contact AO, bilateral filtering and reconstruction,
ambient-only HDR resolve, persistent quality controls, diagnostic views and F3
cost/memory. Imported ground foliage ambient is no longer multiplied by direct
sun visibility. Canopy descriptor v3 stores shared upper/lower hemisphere
visibility; all four species were baked and packaged.

Validation:
- Release Windows client/server publication passed; final client rebuild clean.
- 9,899 cooked blocks verified; package 1,614.8 MiB.
- Focused canopy tests passed: empty visibility, roof direction, repeatability,
  mip dimensions, SHA-256, budgets, source trunk geometry and existing density/mips.
- Packaged captures inspected: forest lit and AO-only, crate contacts on/off,
  building wall/floor, open gate/DHD, and water transmission. An eight-second
  AO-only moving-camera recording contains 80 frames at 1920x1080.
- Low/High/Performance switches and 960x600 -> 1920x1080 resize completed without
  render errors. Pause controls fit both sizes; viewmodel/UI remain outside AO.
- Renderer disposal/recreation exercised across separate fixture sessions.
- The first water inspection command used unsupported --view water; corrected
  --view lake completed successfully. This was a harness invocation error.

One warmed 1080p, VSync-off comparison (20 s warmup, 5 s samples):
                       AO Performance       AO Off
AO passes mean/p95     0.318 / 0.365 ms       0.087 / 0.119 ms
Frame p95              3.404 ms              7.231 ms
Frame maximum          161.476 ms            204.155 ms
The off run was noisier across other CPU/GPU subsystems; these frame numbers do
not establish an overall AO frame-time delta. Maxima include screenshot work.
The AO timing includes resolve/filter/horizon work but not the extra opaque MRT
bandwidth. No further benchmark sweep was run. An unused resolved-depth allocation
and copy were removed afterward; the final resolve shares the original depth.
Final AO targets/auxiliary outputs use about 40.5 MiB at Performance 1080p,
plus 1.86 MiB shared canopy ambient data when all four prototypes are loaded.

Evidence: artifacts/review-evidence/ambient-occlusion. Screen-space AO still lacks
offscreen geometry; thin/subpixel foliage can lose detail. Volume crowns remain
rounded at close range. These earlier checks do not certify the
8.33 ms forest target, all biome/weather combinations or full travel regression.


## Normal-world canopies and distant grass, 10 November 2026

- Release client build/publish passed. Package verification checked all 15,584
  blocks; the archive is 1644.1 MiB. Dedicated server code is unchanged.
- All ten leafy registry variants passed source identity, wood-only trunk,
  unscaled-placement and volume-dependency checks. Bare Broadleaf_Desktop has
  no canopy. Hemisphere visibility dimensions/checksums and directional AO pass.
- The 51 focused environment checks pass, including negative-coordinate grass,
  water/building/gate exclusions, exact outer-to-near record promotion and the
  maximum outer-subset density across all three quality presets.
- Packaged 1080p fixed-noon captures completed in the ordinary gate-pair temperate
  forest and the icy/desert graphics fixtures, without prototype replacement flags.
  Four temperate and four icy canopy resources loaded. AO shaders ran in all three
  presets. Captures were visually reviewed; desert checks cover terrain/atmosphere,
  not a close-up of the bare wood model.
- Client sessions completed normally; server error logs were empty. Evidence:
  artifacts/review-evidence/world-vegetation (three captures and compact logs).

No performance sweep or full multiplayer/physics regression was run for these
render-only changes. Smoke timing logs include startup, shader compilation and
capture work; they do not establish the warmed 8.33 ms target. Distant trees still
use mesh LODs. Crowns remain rounded at close range, especially spruce.
