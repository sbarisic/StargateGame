# Local testing and AI development

Use `scripts/Test.ps1` for builds, scenarios, recipes, and focused checks. The menu and CLI use the versioned catalog in `src/StargateGame.Testing/Contracts.cs`. Defaults are Windows x64, Release, seed 7319, one client, performance graphics, and isolated temporary state.

## Manual testing

The latest corner/foundation/stair update is untested. Asset generation was run,
but no native/managed builds, tests or playtests were run. Rebuild matching client
and server with updated assets. The current native bridge must match its source;
this follow-up adds managed collision layers without adding native exports.

Use the existing Sandbox for manual acceptance:

- Fill floor corners from either supporting wall, including rotated structures.
  Try duplicate and partially overlapping floors and genuine wall-body crossings.
  Rejections should name the blocking piece.
- Add/remove walls, windows, doors and corner pieces on foundations. Check flush
  outer edges, unchanged bare edges, thresholds, shadows, and saved/reloaded houses.
- Walk, run, crouch and jump on both staircase flights; enter from the sides and
  walk underneath the return flight. Compare steady traversal with the incline
  beside the stairs at (-28, 0, 8), which has the same flight slope.
- Drop crates onto stairs: they must rest on individual treads, not the player
  ramp. Check picking/visibility against the actual stair geometry.
- Keep moving during edits and streamed collision replacement. Check preparation,
  commit and replay logs; all stair collision layers must switch together.
- Continue checking doors from both sides, including genuine sweep obstruction.

The 2026-11-11 picker/end-cap/stepping changes are untested. Stepping now passes
desired velocity to Jolt before its internal slope cancellation, retaining 30 cm
step-up, 35 cm floor following, and the existing camera filter. Jumping, ascending
airborne motion and swimming disable both stepping and floor following.
Before testing these native changes, run `python tools/build_native.py` and then
rebuild matching client/server binaries with asset cooking. Normal .NET builds
alone use the packaged native library. Neither native nor managed builds were run
as part of this change, at the developer's request.

Every client launch writes an immediately flushed log to `%LOCALAPPDATA%/StargateGame/logs/client-<UTC timestamp>-<PID>.log`. It includes console output, startup failures, full exception stacks, build/working directories, and launch arguments. When a debugger is attached, thrown exceptions are also logged before application catch handlers run; these entries can include handled exceptions and do not by themselves indicate a crash. Logs stay in this directory when Local Testing switches profiles.

Visual Studio can launch the client directly in Debug or Release. Building the client also builds its Server and Harness project dependencies and writes `testing-runtime.paths` beside the client. Local Testing uses those exact outputs, without copying asset packs or looking for an older Release build. Asset-cooking builds also record the development overlay so additional clients see the same current assets. Keep both dependencies enabled in the solution configuration. Published packages use their bundled runtime instead.

Direct client startup loads the development asset overlay recorded in `testing-runtime.paths`, just like harness-launched clients. An explicit `--dev-assets` path takes precedence; `--loose-assets` disables automatic overlay selection.

The main menu has **Local Testing** and **Connect to Server**. Select a local scenario to start immediately. Optional biome, preset, landscape, and settlement controls appear below the scenarios. The menu remembers the last manual selection; CLI runs resolve catalog defaults independently.

- **Sandbox:** two functional gates/DHDs, pickup props, stairs/platform, a shared equipment crate, a furnished house with doors, forest and water views. The technology station displays transport rings and a ZPM hub. These two assets are visual inspection only.
- **Planet Explorer:** generated terrain. Biome, landscape, settlement/style, seed, time, and weather are independent controls. Curated presets are mountain-meadow, forest-interior, desert-mesa, coastline, and icy-highlands. The server derives each regional bookmark from the generated landscape; no earlier seed-search run is required.
- **Travel:** a private persistent universe with production addressing. Known icy/desert routes are returned in status. Destinations load on demand. `prepare-destination` explicitly warms a route; `-DestinationDelay` and `-Delayed` exercise existing delayed paths.

Press **F6** for the local testing panel: scenario selection, camera bookmarks, explicit player movement to stations, reset controls, capture, another client, time, graphics quality, and the existing F4 planet generator. Camera inspection never teleports the player. Return to the `player` bookmark to restore the gameplay view.

**Reset Player** restores the spawn and clears velocity. **Reset Station** restores Sandbox props, gates, buildings, or fixture containers; immutable inspection stations need no object reset. **Reset Scenario** creates a fresh world generation and fresh test saves. Resets and switches retain client processes and shared rendering resources, restarting only the owned server. Requests with an older generation are rejected.

Remote connections retain host, port, and player-name fields. The local testing panel is available only for the menu's owned local session. Local control cannot attach to or modify a remote server.

## Commands

```powershell
./scripts/Test.ps1 list -Json
./scripts/Test.ps1 check                         # quick: harness, tactical movement, address book
./scripts/Test.ps1 check -Group building,ui,audio
./scripts/Test.ps1 check -Group harness-processes # rendered lifecycle/failure checks
./scripts/Test.ps1 run -Scenario sandbox -Recipe sandbox-check
./scripts/Test.ps1 run -Scenario sandbox -Recipe gate-roundtrip
./scripts/Test.ps1 run -Scenario planet-explorer -Preset icy-highlands -Recipe visual-check
./scripts/Test.ps1 run -Scenario travel -Recipe travel-roundtrip -Cold
./scripts/Test.ps1 run -Scenario travel -Recipe audio-endpoints -Clients 2
./scripts/Test.ps1 check -Group full             # explicit broad suite
```

Builds are incremental and run once before launch. `-BuildRoot <directory>` selects an exact build; `-ContentCache <directory>` selects the incremental asset cook cache. `-NoBuild` requires matching binaries and cooked content in that root. No command searches historical build folders. A live session records its build root; stop it before rebuilding there, or use another root.

```powershell
$build = 'D:/Builds/Stargate/testing'
./scripts/Test.ps1 run -BuildRoot $build -Scenario sandbox
./scripts/Test.ps1 run -BuildRoot $build -NoBuild -Scenario sandbox -Recipe gate-roundtrip
$s = ./scripts/Test.ps1 start -BuildRoot $build -NoBuild -Scenario travel -Clients 2 -Json | ConvertFrom-Json
./scripts/Test.ps1 status -Session $s.session -Json
./scripts/Test.ps1 wait -Session $s.session -Condition capture-ready -Timeout 120
./scripts/Test.ps1 capture -Session $s.session -Client 2 -Name receiver.png
./scripts/Test.ps1 stop -Session $s.session
./scripts/Test.ps1 stop -Session $s.session       # safe to repeat
```

`run` closes its owned processes on success, failure, or cancellation. Use `-KeepOpen` only when deliberately retaining a run. `start` is the explicit persistent interactive operation; normal input works after you focus its window. AI-launched windows are visible without requesting focus. Normal input is suppressed in automated clients; typed commands provide bounded movement/look control.

## Typed actions and recipes

An action file uses the same wire schema as named-pipe control. Omit session/generation for the CLI to fill them from the selected manifest, or provide the recorded values to detect a stale caller. Coordinates are metres and look angles are radians.

```json
{
  "action": "camera",
  "generation": 1,
  "client": 1,
  "data": { "bookmark": "technology" },
  "timeoutSeconds": 60
}
```

```powershell
./scripts/Test.ps1 action -Session $s.session -Request action.json
```

A recipe file contains ordered steps. Generation is refreshed after a reset/switch. Operations are serialized. Capture waits for the requested view/settings and loaded visible content, then a subsequent rendered frame.

```json
{
  "name": "sandbox-inspection",
  "steps": [
    { "action": "camera", "data": { "bookmark": "technology" } },
    { "action": "capture", "data": { "name": "technology.png" } },
    { "action": "teleport", "data": { "station": "equipment" } },
    { "action": "reset-station", "data": { "station": "equipment" } },
    { "action": "settings", "data": { "hour": 18, "quality": "high" } },
    { "action": "capture", "data": { "name": "evening.png" } },
    { "action": "sample", "data": { "seconds": 3 } }
  ]
}
```

Run it with `./scripts/Test.ps1 run -Scenario sandbox -Recipe recipe.json`.

| Action | Data and behavior |
|---|---|
| `status` | Manifest, client states, server routes, location, authoritative player, entity/prop IDs, inventory/container, gates, loading/pending work, rendering counters, audio cues. |
| `camera` | `bookmark`, or finite `eye` and `target` objects `{x,y,z}`. `player` restores gameplay camera. |
| `move` | `seconds` 0–30, `forward`/`strafe` -1–1, optional `yaw`, `pitch`, `jump`, `crouch`, `walk`. Zero duration cancels input. Timeout/failure clears input. |
| `teleport` / `setup-prop` | Explicit authoritative setup. Player station or collision-checked `position`; prop `item` persistent GUID and `position` within its current world. Setup is labelled in traces. |
| `dial` | DHD entity ID, glyph `key`, optional `action` (default press); uses ordinary DHD ray picking and server validation. Orb is key 0. |
| `carry` | `action`: grab/drop/throw and `item` persistent GUID for grab. Uses ordinary gameplay commands. |
| `build` | `command` using the production BuildCommand schema; harness supplies request ID/current revision. Includes door operations. |
| `weapon` | Production inventory/container action, optional `item` and `container` GUIDs. |
| `settings` | Allowlisted rendering controls from `list.renderingSettings`: time, quality, weather, grass, clouds, volumetrics, reflections, mist, parallax, lighting, horizon previews and diagnostic fixtures. |
| `gate-preview` | Frozen render-only `phase` and phase-relative `seconds`; `off` restores live state. Does not dial a gate. |
| `audio-record` | `name` starts WAV capture; `stop:true` finalizes it. Cue counters accompany state. |
| `sample` | Bounded 0.1–60 second GPU timing sample; result contains frame count, p95, and maximum. |
| `capture` | A filename in the selected client's artifact directory; screenshot and adjacent JSON state snapshot. |
| `reset-player`, `reset-station`, `reset-scenario` | Explicit reset scopes; station needs `station`. |
| `switch-scenario` | ScenarioOptions object, e.g. `{ "scenario":"planet-explorer", "preset":"coastline" }`. Explicit biome/seed/landscape override preset values. |
| `add-client`, `disconnect`, `connect` | Session-owned client lifecycle. |
| `prepare-destination` | Six-glyph address; production resolution and destination preparation. |
| `relocate` | Six-glyph address using the existing developer inspection command; setup, not travel coverage. |
| `window` | `state`: minimize/restore, for rendering lifecycle checks. |
| `assert` | Dotted state `path` and exact JSON `equals`; no code execution. |
| `wait` | `condition`: server-ready, playable, capture-ready. |

Each result has request/session IDs, scenario generation, target client, completion outcome, data, and error details. The control protocol accepts no arbitrary C# or Lua. Unknown actions/settings fail. Missing renderer/gameplay capabilities must not be counted as passed coverage.

## Readiness, isolation, and artifacts

- **Server ready:** loopback socket has its OS-assigned port and scenario setup is complete. The port comes from structured startup status, never from reserve-and-release probing.
- **Playable:** connected, authoritative spawn/collision available, travel arrival acknowledged, and a playable frame presented.
- **Capture ready:** requested camera/settings applied, visible asset/terrain work complete, and a later frame rendered. Diagnostic fixtures additionally wait for their probes.

Timeouts report the last state and unfinished condition. Animation samples, sustained movement, and benchmarks retain explicit durations; startup has no fixed sleep.

Each session writes `%LOCALAPPDATA%/StargateGame/Testing/sessions/<id>/`: launch and resolved settings, build/content hashes, manifest, JSON-lines requests/results, per-client/server logs, optional recipe copy, captures/recordings, and `result.json`. Capture JSON includes camera, world/player/gates, options, and build/physics identity. Artifacts remain after cleanup.

Client profiles, identities, address books, settings, and server saves are separate from normal play. Warm runs use `test-cache` below the chosen build root. `-Cold` gives the generation a run-local terrain cache; normal caches are never deleted. Full reset abandons old world state and creates a new generation; old generation files remain only as artifacts.

The coordinator owns server and added clients through a Windows job object. A menu client remains externally owned; closing it ends its coordinator. Cancellation, startup failure, client/server failure, and coordinator exit release owned children. The current-user-only named pipes are enabled only by explicit harness arguments. Client commands execute on the game thread; server setup executes on the simulation thread. Gameplay networking remains separate.

The original single-gate flat baseline is an advanced render-only preset: `Test.ps1 run -Scenario sandbox -Preset graphics-lab -Recipe visual-check`. It is listed separately from the Explorer presets.

## Existing coverage and consolidation

`list -Json` is the current registry. Focused groups dispatch directly without unrelated pre-checks. Results list exactly the executed groups; there is no default “ALL passed”. `quick` is intentionally small. Stress, explore, slow streaming, full suites, and Blender reviews remain explicit choices.

| Previous entry point | Replacement / retained purpose |
|---|---|
| Test-Graphics, Test-Environment, Test-Atmosphere, Test-Landscape | Thin aliases to Test-Visual → Test.ps1; presets/settings/camera/capture/sample steps. |
| Test-Vegetation, Test-ForestLab, Test-GroundMist | Same orchestration; forest bookmark and mist shelter probes remain distinct cases. |
| Compare-TerrainRelief | Same orchestration, rocky material/parallax and grass-density comparisons; no source manifest mutation. |
| Test-Horizon | Advanced render-only gate effects/prop/character/wave/occlusion/shadow fixtures. |
| Test-EnvironmentLifecycle / Test-HorizonLifecycle | `lifecycle` / `horizon-lifecycle`; quality, minimize/restore, disconnect resource assertions, reconnect and reset. |
| Test-Dialing | `gate-roundtrip` or `dialing-failure` in Travel. |
| Test-Audio / Test-AudioEndpoints | `audio`, `audio-failure`, `audio-endpoints`; shared setup with separate cue assertions. |
| Headless gameplay/network/persistence/UI/audio/Python checks | Independently selected `check -Group ...`. |
| Test-Ui / Test-Movement | `check -Group ui,address-book,ui-render`, `ui-live`, and `movement-visual` retain DPI/layout captures and the pushing trace. |
| Test-Settlements / Test-Furnishings | Seed-search/export groups and Explorer settings; optional `check -Group blender` requires `BLENDER_EXE`. These are not scenario-launch prerequisites. |
| Record-Audio | Manual Sandbox audiovisual recording through start/action/stop; optional ffmpeg prerequisite. Automated dialing recordings use the audio recipes. |

Old orchestration flags such as fixed `-Port`, separate client/server binary paths, startup `-Warmup`, and historical `-Configuration` directories are replaced by `-BuildRoot`, readiness timeouts, and explicit recipe steps. Use `-Settings @{orbit=$true}` / `@{traverse=$true}` for inspection movement, or typed `move` for gameplay. Motion aliases capture eight samples. Retired Tree Pack selectors are removed; inspect `status.treeModels` and use `tree:<model-name>` for current content.

## Packaging

```powershell
./scripts/Publish.ps1 -Client -OutputDirectory artifacts/publish
```

The client package includes matching Windows server/coordinator binaries under `testing`, the galaxy catalog, cooked asset packs and native dependencies. It needs the Windows x64 .NET 10 runtime, as do the existing framework-dependent packages. Local Testing resolves binaries relative to the package and does not require source paths. Incremental asset cooking is shared with normal publication.

## Validation baseline

Representative development-machine measurements (2026-11-11), not speed targets:

- Cold icy-highlands visual run: 68.40 s, including startup, capture, and three-second sample.
- Cold production Travel with the same carried prop out and back: 109.62 s, including cleanup.
- Warm Sandbox full reset alone: 3.34 s from request to ready, retaining the client process.
- Warm Sandbox lifecycle run: 25.26 s including quality captures, minimize/restore, disconnect/reconnect and full reset.
- Explorer temperate → desert → coastline in one client: 76.14 s including captures and switches.

These measure whole recipes; inspect timestamped events for startup/reset substeps. The representative validation also passed two-client Sandbox doors/containers, carried-prop travel, current model capture, live UI and 100/150/200% DPI captures, mist shelter/roof probes, seven-lock receiver audio isolation, and cancellation/crash cleanup. Python asset tests passed; optional Blender was reported skipped without BLENDER_EXE.

Broad packet loss, travel commit-stage crashes, and multiplayer soak matrices are deferred. Rings/ZPM gameplay is not implemented.


### Door and demolition manual acceptance (protocol 25)

Implementation does not run these checks. Rebuild matching client/server before
checking both sides of doors, genuine player/crate blockers and the logged
blocker identity. Confirm frames and fixed world geometry never block the swing,
while walls still block interaction through them. Compare looking toward/away from doors using the aggregate
`BUILD query` diagnostics. Browse all palette pages and confirm the six hidden
entries stay absent while saved instances remain present. Hold/cancel demolition,
move aim between an assembly's children, open the picker or pause during a hold,
and keep the button down after completion/rejection. Verify one request per hold,
visible mesh tint/outline without flicker, dependency/generated-building errors,
window resizing and saved/reloaded results.

## Sandbox medium village

Sandbox (gate-pair fixture revision 9) creates twelve furnished mega-kit houses
east of the gate using four variants, staggered positions and 0/30/330 degree
rotations. The original interior house/bookmark stays at (25, 0, 25). Two gently curved through-lanes and two short dead-end spurs connect to a winding
approach. Eight houses have curved door paths; four keep grass approaches to
avoid a dense street grid. Paths begin at the rotated door threshold.
All widths are 70% of revision 8: approach 2.8 m, lanes 2.45 m, door paths 1.12 m.
Road shoulders and fixture grass clearance are reduced proportionally. Curves
are sampled into shared road segments for consistent paving and grass masks.
House spacing leaves room for rotated roofs, paths and door swings. Gates,
equipment, stairs and water remain separate; forest crowns are cleared nearby.
Road masks follow the full lane/path network and explicitly suppress grass.

The three supplied `assets_itch/textures/paving_floor_*` sets are imported by
`python tools/import_paving_materials.py`, also invoked by the main terrain PBR
importer. Terrain palettes contain eight biome layers plus three paving layers.
Paved roads use these dedicated layers, including normal maps, AO, roughness,
height relief and self-shadowing. Paving repeats every two metres and changes
pattern between village street bands. GPU memory reporting includes all layers;
the existing asynchronous mip upload and cooked-asset pipeline handles paving.
The local source credits and file hashes are recorded in the terrain manifest.

The fixture definition carries the layout, and SandboxStations uses it for
buildings, functional doors, furnishings and interior resets. Restart/reset
Sandbox with matching client/server builds to generate revision 9. No builds,
tests or playtests were run; material generation is part of implementation.

## Sandbox hold-Q spawn menu

Hold Q in live Sandbox to open the prop picker and release the cursor. Select the
rendered Box or Barrel thumbnail, then click a visible world surface outside the
panel. Release Q to close and resume mouse-look. Escape closes until Q is released.
The menu suppresses movement, firing, building and pickup input while open and is
unavailable during pause, travel or other modal panels. Spawn requests use the
rendered camera's cursor ray; the server repeats the ray against live collision
within 40 m, checks camera proximity/visibility and free prop volume, and inserts
an upright dynamic prop through the existing replicated registry. Existing limits
of five requests per second and 64 live props apply. Failures appear in the menu.

Generate the two thumbnails with Blender background
`tools/assemble_building_prefabs.py -- --spawn-icons --refresh-icons`.
Both PNGs were generated from the actual prop meshes and materials. The new
`spawn-prop` build action requires matching protocol-26 client/server builds.
No builds, tests or playtests were run.


### Sandbox physics gun and Q-menu clicks

Hold Q and click the rendered Box or Barrel thumbnail to spawn immediately at
the last cursor position over the world (initially the crosshair). Clicking the
world while Q is held spawns another selected prop. A nearby surface is required.

Press G in Sandbox to switch between the rifle and the supplied Chrome ray gun.
Hold left mouse to grab a dynamic prop within 100 metres; aim to move it, use the
wheel to change distance, and E + mouse movement to rotate it (holding the camera still). Release left mouse or press
right mouse to drop it. The hold uses a strong critically damped force controller and collision sweeps. Fast movement or obstruction does not time out a physics-gun hold. Buildings,
terrain, stargates, and other fixed geometry cannot be grabbed. Opening Q or a
modal screen releases the prop.

Client and server must both use protocol 28. Generate the raygun assets with
`blender --background --python tools/import_physics_gun.py`, then publish the
asset overlay or cook the release pack. The model is Scenario's Chrome ray gun,
CC-BY-4.0; full attribution is retained in
`assets/models/physics-gun-provenance.json` from the supplied package credits.

Implementation did not run builds, tests, profiling, or playtests. Manually check
thumbnail spawning, grabbing/dropping, distance and rotation, static-object
rejection, and collision against walls before accepting the feature.

Physics-gun grabs explicitly resume completed-transit props and wake them immediately.
Wheel input accumulates between simulation ticks in both directions; hold distance
can decrease to 0.5 m (physical player/world collision still applies). Check inward
and outward scrolling after grabbing a resting prop and a prop that crossed a gate.


### Sandbox cables

Hold Q, select the rendered Cable tool, then release Q. Left-click two surfaces
to place a cable. While choosing the second point, the wheel adjusts extra slack
in 5% increments (0–100%, initially 15%). Right-click cancels an unfinished cable
or removes the highlighted cable; Escape exits. The Q panel selects black, blue,
red, yellow, or green rubber, and 1, 2, or 4 cm diameter (default black, 2 cm).
Click within 20 m; endpoint separation is limited to 100 m, with 256 cables per
location. Surface markers and the hanging preview use the current selections.

Endpoints on props and doors follow their rendered transforms. The client-only
30 Hz rope solver uses gravity, damping, length constraints, and raycast contacts
against static and dynamic collision geometry. Tubes have capped ends, smooth
normals, distance-dependent radial detail, and dynamic shadows. Cables apply no
forces and cannot block players or props. Pulling endpoints apart stretches the
cable without breaking it; the placed rest length is retained. Approximate
raycasts can miss thin geometry or fast motion. Simulation pauses beyond 150 m
and uses a shared 4,096-query frame budget with round-robin scheduling.

Cable messages, revision snapshots, and SQLite writes are separate from building
edits: placing/removing cables does not rebuild collision, change physics epochs,
or reset player prediction. Protocol 29 and save schema 10 require matching
client/server builds. Persistent worlds store cables in their universe database;
Sandbox's otherwise ephemeral fixture stores cable rows in
`%LOCALAPPDATA%/StargateGame/Server/saves/sandbox-cables/cables.sqlite`, keyed by
fixture location. Attachments whose owners no longer exist are removed; cables
do not resurrect spawned props or user buildings when the fixture itself resets.
There are no device ports, electricity, signals, or logical connections.

Generate the icon with Blender running `tools/render_cable_icon.py`, then publish
the development asset overlay. The icon and overlay were generated during
implementation; no builds, tests, profiling runs, or playtests were run.

Manual acceptance: exercise placement/cancel/removal and styles; move/rotate
attached props and open doors; inspect floor/corner/crate draping, stretching,
shadows, and distance transitions; reconnect and join with a second client;
reload, demolish owners, unload chunks, and transport props through gates.
Keep walking during edits and compare performance with many cables.

Cable rendering now rounds solver corners with a subdivided quadratic curve and
uses 12-sided near tubes (6 at distance). Picking and shadows use the smoothed
shape. Default diameter is 5 cm; the menu offers 2, 5, and 10 cm. Saved cables
from the initial thin style are upgraded once to 5 cm on location load.
Requires matching protocol-30 builds. No builds or playtests run for this change.

While the Cable tool is equipped, middle-click cycles diameter through 2, 5, 8,
11, and 14 cm, then back to 2 cm. Default remains 5 cm. The HUD displays the
current size; the placement preview updates immediately, including between
endpoint clicks. The Q-menu size buttons use the same list. Existing placed
cables retain their sizes. Requires matching protocol-31 builds. Builds and
playtests remain deferred to the developer.

Cable end rings now sit flush on their attachment surface, with their end-face
planes parallel to that surface. Short normal-aligned exit sections blend into
the smoothed hanging curve. Both ends use the owner's current presented normal,
including rotating props and opening doors; preview, picking, and shadows use
the same mesh. Check side/top/angled attachments and short cables at each diameter.
This is a renderer change; no protocol or saved-cable changes are required.
Builds and playtests remain deferred.

Resting cables use radius-aware contact probes and normal-only corrections,
with contact damping and alternating length-constraint sweeps. After 0.75 seconds
of sub-2 mm movement per step (within a 4 mm settling envelope), they sleep and
stop uploading unchanged meshes. Attachment movement, nearby prop/player motion,
collision replacement, and resuming a hidden or distant cable wake the solver.
The existing 4,096-ray frame budget includes the added support probes.
Manual checks: let hanging and ground-supported cables settle at each diameter;
move an attached prop, move/remove a supporting crate, open an attached door,
edit the supporting terrain, and leave/return to the area. Check that idle
cables stay still and disturbed cables resume draping. No builds or playtests
were run for this change; no protocol, save, or native bridge changes are needed.

### Deferred rendering inspection

The harness `settings` action accepts `ao` (`auto`, `off`, `low`, `performance`,
`high`) and `aoView`: `lit`, `ao`, `albedo`, `normals`, `geometric-normals`,
`geometric-depth`, `depth`, `material-class`, `indirect`, `sun-diffuse`,
`transmission`, `specular`, `emission`, `hdr`, `roughness`, `metalness`,
`specular-strength`, `surface-auxiliary`, `sun-visibility`.
The additional scalar views expose every packed G-buffer channel; surface auxiliary
stores transmission for foliage and snow coverage for solid materials. Old material/combined/raw/filtered/
ambient names are removed. AO is the effective visibility used by deferred
lighting, including receiver weighting. White means unoccluded; Off is white.
These deferred views have no diagnostic command-line switches.

Example settings action:
`{ "action": "settings", "data": { "ao": "high", "aoView": "ao" } }`.
Set capture `includeUi:false` to capture before HUD overlays.
Use the existing capture action, which waits for material shaders, model/terrain
uploads, canopy uploads, grass preparation and capture readiness. `normals` shows
shading normals; `geometric-normals` shows the AO guide. Depth displays camera
distance divided by 100 m. `hdr` shows linear HDR before tone mapping; screenshot
formats with limited range will clip values above one.

Run the `deferred-ao-compare` recipe in Sandbox through `scripts/Test.ps1 run`.
It fixes quality High, clear weather, hour 12, visual time 20, camera orbit/traverse
off and manual exposure at 0 EV. It captures lit AO High/Off pairs and the AO view
at the same gate-pair, interior and DHD bookmarks. Camera, animation and exposure
remain fixed between each pair. The recipe leaves exposure/time fixed for further
inspection and restores AO Auto/lit. Set `exposure` to `auto` and `visualTime` to
null to resume live behaviour.

Harness status exposes `performance.gbufferMs`, `aoMs`, `deferredMs`, `weaponMs`,
and `compositionMs`; target memory is in `resources.targetsBytes`, `gbufferBytes`,
`lightingBytes`, `aoBytes` (world AO), and `weaponBytes` (including weapon AO/MSAA).
GPU timings use nonblocking completed-frame queries.

Manual acceptance: grass roots, terrain cavities and contacts, house corners,
DHD base; bright/shaded AO pairs; parallax at grazing angles/water/fog boundaries;
vegetation LOD/wind/cutouts; moving props; gates, DHD, clouds, fog, water, particles,
previews and demolition; weapon edges/shadows/AO/glow; resizing/presets/travel and
frame-time/memory comparisons. Screen-space occlusion remains limited to visible
samples; subpixel and offscreen cavities cannot be reconstructed.

Refresh shader assets through the development overlay before using the matching
client build. No protocol, save-schema or native-physics change is needed.
Builds, tests, profiling and playtesting were left to the developer.

## Lantern lighting

The `lantern-lighting` Sandbox recipe spawns three Wooden Lantern props, fixes
camera/time/exposure, and captures night/noon AO pairs plus night quality presets.
Use a fresh Sandbox session. It uses normal server-validated spawning and waits
for capture readiness. See [LANTERNS.md](LANTERNS.md) for regeneration and manual
acceptance. Status reports `pointLights.count`, `shadowFaceUpdates`, `targetBytes`,
`shadowMs` and `lightingMs`; the `shadows` setting controls global shadow casting.
This recipe has not been run as part of the lantern implementation.
