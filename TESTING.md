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
