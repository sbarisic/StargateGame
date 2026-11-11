## Unified local testing

Start the client without connection flags for **Local Testing** or **Connect to Server**. Local Testing offers Sandbox, Planet Explorer, and Travel; F6 opens their shared controls. AI workflows use `scripts/Test.ps1 run`, `start`, `action`, `capture`, and `stop`. See [TESTING.md](TESTING.md) for commands, isolation, readiness, recipes, and packaging.

# StargateGame

This is the source repository. Models, textures, audio and generated asset packs
remain local and are excluded from Git, including old asset history. See
[REPOSITORY.md](REPOSITORY.md) for asset setup and the migration layout.

A first-person multiplayer exploration prototype using C#/.NET 10, FishGfx, and
Jolt Physics. A dedicated server loads independent 4096 × 4096 metre regions from
a persistent 19,020,803-location galaxy. Physical DHD dialing opens synchronized
connections with a visual kawoosh. Settled outgoing gates transfer players, crates
and barrels after a minimum two-second transit hold and destination readiness. See [DHD_DIALING.md](DHD_DIALING.md)
for controls, addresses, catalogue tools, and runtime behaviour.

## Run

The client requires Windows x64, .NET 10, and OpenGL 4.0. The dedicated server
supports Windows x64 and Linux x64. Building requires the .NET SDK selected by
`global.json`. Normal builds and play need neither a C++ compiler, Blender, nor
the original game installation. Native physics binaries are included. Converted assets must be restored locally
as described in [REPOSITORY.md](REPOSITORY.md).

In Visual Studio, open `StargateGame.sln` and select **Server + Client** in the
Start dropdown. F5 starts both projects; Ctrl+F5 starts them without debugging.
The shared `StargateGame.slnLaunch` profile starts the server first. The client
automatically joins the noon-lit **gate-pair** test world at `127.0.0.1:17777`.
Two gates stand side by side, 12 m apart, with a DHD each and a crate/barrel nearby.
Enter any six distinct available destination glyphs at either DHD, then press
the orb; the other gate receives the connection. The origin key is optional here.
Travel remains one-way until closing and redialing from the other end. The fixture
resets each server run and never opens universe saves. Select the **Persistent
universe** project launch profiles to return to normal saved-world testing on 7777.
Reload an already-open
solution if the new project/profile is missing. Multi-project launch profiles must be enabled.

Alternatively, open two PowerShell terminals:

```powershell
./scripts/Run-Server.ps1
./scripts/Run-Client.ps1
```

Direct connection and server configuration:

```powershell
./scripts/Run-Client.ps1 -Server 127.0.0.1 -Name Explorer
./scripts/Run-Server.ps1 -Bind 0.0.0.0 -Port 7777 -MaxPlayers 8
```

Executable arguments are `--host`, `--port`, `--name`, and `--connect` for the
client; `--bind`, `--port`, `--max-players`, `--seed`, `--preset`, and `--dev-planets`
for the server. Defaults are seed 7319 and the temperate preset. TCP port 7777 must
be reachable. The game does not configure firewalls or routers. Stop the server
with Ctrl+C. Close game/server processes before rebuilding their loaded DLLs.

| Input | Action |
|---|---|
| WASD / mouse | Move / look |
| Shift / Space | Sprint / jump |
| Escape | Release mouse and open menu |
| Enter / Escape in menu | Resume |
| Q in menu | Disconnect |
| G in menu | Cycle Performance / Low / High graphics |
| H in menu | Switch Mesh / Hybrid grass |
| I | Open personal weapon inventory |
| 1–4 | Equip a weapon slot; press the equipped slot again to holster |
| 0 | Empty hands, retaining owned weapons |
| E on a weapon crate or furnishing chest | Open shared weapon storage within 2.5 m and line of sight |
| F3 | Physics wireframes, character support, terrain and graphics diagnostics |
| F12 | Save a screenshot under `%LOCALAPPDATA%/StargateGame/captures` |
| Tab / Ctrl+A / Enter on connection screen | Next field / replace / join |

Push crates by walking into them; jump onto them to stand on a moving support.
Players pass through each other. Opening the menu sends neutral controls while
the server keeps simulating. Buildings, scenery removals, doors, crates, and planet clocks are saved per location. Player positions are not retained between sessions.

Weapon inventories have four non-stacking slots; weapon crates and furnishing
chests have sixteen shared slots. Taking a rifle uses the first free personal slot
without equipping it. Storing an equipped rifle leaves empty hands. Containers
start empty, and non-empty containers and their structures cannot be removed.
See [HYBRID_GRASS_WEAPONS.md](HYBRID_GRASS_WEAPONS.md) for implementation,
compatibility, measurements, and validation limits.

## Settlements and existing worlds

Terrain generator **6** adds deterministic ruins, villages and compact cities,
including graded plots, narrower roads and usable doors. Medieval houses vary
in orientation. Generated buildings cannot be demolished or extended.
See [SETTLEMENTS.md](SETTLEMENTS.md) for assets, controls and validation.

**Start a new universe.** Older terrain versions are rejected; their save files
are not rewritten or deleted. The optional persistent VS profile uses
`dev-settlements6-layout4`; the default VS profile uses the transient gate-pair lab.
Client and server must both use protocol **18**.

```powershell
./scripts/Run-Server.ps1 -Universe settlements-v6
# Force a style for inspection; normal worlds use deterministic selection.
./scripts/Run-Server.ps1 -Universe village-demo -Settlements village -SettlementStyle seahill
```

Buildings and scenery edits remain in their original universe. Terrain caches
remain separate from world saves. Use matching client/server packages.

Planet days last 60–120 real minutes and continue while the server is offline.
Each saved location retains its clock; address aliases share that clock. In deep
water, WASD swims, Space rises, Ctrl dives, and Shift swims faster. There is no
oxygen or stamina limit. Crates float and drift with gentle river currents.

## Graphics

The client starts at 1920 x 1080 with planetary day/night cycles, atmospheric
scattering, volumetric clouds and fog, normal-mapped terrain, reflective rivers
and lakes, tone mapping, and FXAA. UI text
is drawn after post-processing. Performance graphics and VSync are enabled by
default; use `--graphics low|performance|high` and `--vsync on|off`, or the matching
`-Graphics` / `-VSync` options in `Run-Client.ps1`. See [GRAPHICS.md](GRAPHICS.md)
for architecture, diagnostics, quality settings, and the repeatable 1080p benchmark.

## Build, test, and publish

```powershell
./scripts/Build.ps1
./scripts/Test.ps1
./scripts/Publish.ps1 -Client
./scripts/Publish.ps1 -ServerRuntime linux-x64
```

Packages appear under `artifacts/publish/`. Copy a complete package to another
machine; run `StargateGame.Server.exe` on Windows or
`dotnet StargateGame.Server.dll` on Linux. Framework-dependent packages need .NET 10.
The server contains no FishGfx assembly, textures, or visual model files; collision
meshes are embedded in the physics assembly. Each package includes its native runtime and notices.

Tests cover character/prop physics, native rollback, garbage collection, protocol
validation, real TCP sessions, reconnects, and two actual client session implementations
behind a proxy adding 100 ms RTT with up to 20 ms jitter. They do not require graphics.
For a graphical smoke run, pass `--smoke-seconds 9 --capture-dir <directory>` to
a client connected to a running server. It walks, pauses/resumes, changes the window
size, captures normal/debug views, and exits. `--smoke-peer` also faces another player.
Smoke mode leaves the cursor free and ignores physical mouse motion.

FishGfx remains pinned to `f5e6404a17a75198ade77620e874dc39517d5cca`, with FishUI
at `18285683769e38ac0842af1f63efc9d033b5bacd`. Initialize a fresh checkout using
`git submodule update --init --recursive`. No project/runtime references point at RaylibGame.

## Simulation and networking

- **Shared:** world descriptions, terrain triangles, controls, structured states,
  protocol version 13, and bounded TCP framing.
- **WorldGen:** regional drainage, canonical millimetre terrain data, deterministic
  placement candidates, caching, LayerProcGen workers, and CPU mesh construction.
- **Physics:** JoltPhysicsSharp 2.23.0, collision assets, character controller,
  authoritative/predicted world simulation, and local native checkpoints.
- **Building:** canonical square/triangle geometry, sockets and support rules.
- **Persistence:** server-only SQLite location registry and saved world changes.
- **Server:** 60 Hz simulation, 20 Hz snapshots, up to eight players.
- **Client:** FishGfx rendering/input, predicted local player and crates, remote
  player interpolation, and shared fixed-step interpolation for the player and crates.
  Reconciliation errors decay visually with a 35 ms half-life.

World coordinates use metres and Y-up. The capsule is 1.8 m tall with a 0.32 m
radius. Walk/sprint speeds are 4/7 m/s, jump speed is 6 m/s, and gravity is 18 m/s².
Walkable slopes stop at 45°, step height is 0.30 m, and downward floor snap is 0.35 m.
Terrain rendering/collision share the exact triangle grid. Static structural mesh
colliders preserve the gate opening; the terrain approach makes it accessible.
The crates are 1 m cubes weighing 10 kg, with friction 0.6 and restitution 0.05.

Clients submit sequenced, tick-addressed inputs, never body positions. Ping/pong
estimates server time and schedules inputs 2–12 ticks ahead. The server holds a
missing movement command for six ticks, then uses neutral movement; jump edges
are never repeated. Late inputs are discarded and acknowledged. Invalid, duplicate,
or excessively future inputs are rejected. Simulation continues without input.

The client retains up to three seconds of local checkpoints. Exact snapshot
confirmations retain live contacts. Other snapshots restore local state, apply
authoritative character/body states, invalidate changed contacts, and replay pending
inputs. Rendering interpolates one fixed tick behind prediction; camera and boxes
share that timeline. Input is sampled each frame and submitted for every fixed tick.
Clock corrections are slewed rather than restarting the tick phase on each ping.
Joining/leaving changes a world epoch and
rebuilds the prediction topology. Missing history resets the baseline. Corrections
of 1 m or more snap immediately; smaller corrections are smoothed only when rendered.
Other players' unknown future actions can still cause corrections during shared pushes.

Physics source/configuration and collision/world-content hashes must match during
joining. TCP JSON frames are capped at 64 KiB; queues and timeouts are bounded.
Native checkpoint data never crosses the network. This direct-connect prototype
has no accounts, matchmaking, persistence, transport encryption, or server rollback.

The default Visual Studio launch uses `--fixture gate-pair` on loopback port 17777.
The optional persistent profile uses `--dev-planets --universe dev-settlements6-layout4`.
Existing universe saves remain untouched by fixture runs.
Published servers and `Run-Server.ps1` keep their existing defaults. Loopback clients
on a developer server get the **F4** location panel. Tab cycles named addresses;
enter six hyphen-separated glyph IDs (Ctrl+A replaces them), then Enter to inspect
that destination. Only your session moves. **C** cancels loading; Escape closes
the panel. Worlds retain their buildings and saved crates. Both endpoints keep
simulating during a connection even without players. After closing, unattended
moving props keep simulating until they settle; idle worlds can then sleep.

**F3** also shows resident terrain/collision counts, generation/cache counters,
collision revision, and tree/rock candidate markers. Recovered rocks now populate
temperate, desert, and icy terrain deterministically. Trees include five additional Tree Pack 3D models among leafy/birch
forests on temperate planets, denser winter spruce/birch forests on icy planets,
and sparse bare saplings near desert channels. Seeded forest patches retain open
meadows; scenery version 2 requires a fresh universe. Foliage uses cutout shadows; wood has collision.
Large rocks have streamed collision, while small ground stones are decorative. Shallow rivers retain ordinary
walking; swimming, currents, buoyancy, caves, terrain editing, and gate travel
are not implemented. Gaea experiments remain in `gaea_maps/`.

Additional validation runs:

```powershell
./scripts/Test.ps1 -Streaming -Explore -Delayed
./scripts/Test.ps1 -Streaming -Stress -Delayed
./scripts/Test.ps1 -Streaming -Slow
./scripts/Run-Server.ps1 -Seed 42 -Preset desert -DevPlanets
```

See [terrain architecture](TERRAIN.md), [native rebuild and ownership notes](native/README.md),
[asset provenance](assets/PROVENANCE.md), and [validation results](VALIDATION.md).

Future gate gameplay is documented in the [galaxy system design](GALAXY_SYSTEM.md),
[three-pair addressing design](STARGATE_ADDRESSING.md), and
[travel design](STARGATE_TRAVEL.md). These include design rationale and implementation status for dialing and travel.

Reference research includes the [numbered glyph catalogue](STARGATE_GLYPHS.md),
[DHD asset mapping](DHD_GLYPH_MAPPING.md),
[Stargate ring mapping](STARGATE_RING_MAPPING.md), and
[existing-address compatibility proposal](STARGATE_KNOWN_ADDRESSES.md).
The [manual-dialing design](STARGATE_MANUAL_DIALING.md) specifies ring movement,
symbol locks, origin activation, and standard versus expanded DHD capabilities.

## Creative building and persistent worlds

Press **B** for the build palette, use the mouse wheel to select pieces, **R** to
rotate, and **Page Up/Down** to adjust foundation elevation. **E** operates doors.
Building is allowed in the Stargate clearing and approach. Square/triangle foundations and floors,
walls, windows, doorways, doors and stairs are available without resource costs.

The server now saves each location's buildings, cleared trees/rocks, door states
and crates. Saves survive planet switches, cache deletion and server restarts.
Use `--save-dir PATH --universe NAME` for separate universes. Clients and servers
must both use protocol 13. See [BUILDING.md](BUILDING.md) for controls, support rules,
storage, architecture and tests. Existing trees, rendering and dual startup remain.

The Stargate event horizon is now always visible by default, with animated blue/silver
ripples and visual disturbances when players cross it. Settled outgoing membranes teleport; incoming/rear entry does not block
players. Use `--event-horizon off` to disable it; see GRAPHICS.md for visual checks
and tuning.


Active gates now have seven glowing chevrons, shadowed orange/blue light spill,
and white membrane highlights that remain visible at oblique angles. Physics
props continuously reflect the membrane waves around their collision outline while touching the
surface, including at rest. The gate is still visual-only and walk-through.
`--event-horizon off` disables the complete active-gate effect; see GRAPHICS.md
for material controls, diagnostic views and local-shadow settings.

DHD glyphs now use subdued weathered markings and support independent amber
illumination, ready for later dialing. All keys default off. For a visual preview,
launch the client with `--dhd-lit 1,6,8,22,30,39`; see GRAPHICS.md for the per-key API.

The central DHD orb now glows with a warm textured core by default. Use
`--dhd-orb off` for the inactive red dome; its state is independent of glyphs.

Temperate worlds include wind-animated grass; icy worlds have detailed snow,
overcast light, denser fog and gusting snowfall. Use `--grass on|off` and
`--weather auto|off|light-snow|storm` for comparison. Defaults are grass on and
automatic weather. Graphics quality controls grass range/density, flake count
and shelter-map resolution. Terrain uses biome-specific PBR palettes; meadows and
woodlands mix short grass with thirteen imported plant species. Deserts include
warm haze and blowing sand (`--dust off` retains baseline haze). See
[GRAPHICS.md](GRAPHICS.md) for imports, tuning and validation.

## Audio

Spatial gate, DHD, movement, prop, building and planetary sounds are enabled by default.
Use the Escape-menu volume sliders, M to mute there, or `--audio off`.
See [AUDIO.md](AUDIO.md) for controls, cue tuning, import provenance and validation.
Client and server must both use protocol 13.

## Address book

Press **Tab** to open or close the address book. Click a visited world in Travel
History or **Select current** to pin its six destination glyphs. With the book
closed, the glyph address appears at the top right with the name underneath.
Escape or X also closes the book. Mouse wheel, Up/Down, and Previous/Next browse
long histories. The book releases the cursor and stops local movement input;
multiplayer simulation continues. It does not dial or copy an address.

History and selection persist per universe in
`%LOCALAPPDATA%/StargateGame/addresses.json` (up to 512 visited locations).
Normal clients receive their current address without the developer catalogue.
Gate travel uses the white transition described in [STARGATE_TRAVEL.md](STARGATE_TRAVEL.md); developer relocation
also records a real location visit. Six destination glyphs are stored without a
foreign origin symbol: finish the sequence with the current DHD's origin key.

## Dense meadows and physical props

Temperate meadows now use dense nearby blades, simpler distant blades and matching
terrain detail. `--grass off` disables all three for comparison. The two wooden
crates and red/black barrels replace the generated world's four debug boxes;
barrels roll, float and produce collision-shaped membrane disturbances.
`--view props` provides a repeatable inspection camera during graphical smoke runs.

Footsteps select the supplied wood, leaves, mud, gravel and stone recordings from
the supporting collision surface. Snow, sand and metal retain filtered fallback
sounds until dedicated recordings are supplied. See AUDIO.md and VALIDATION.md.


Streaming uses protocol 13 with outgoing gate-region preloads and bounded detailed
terrain backed by far scenery. Existing village wall joints derive corner trim.
See [STREAMING.md](STREAMING.md) for controls, measurements and limits.
