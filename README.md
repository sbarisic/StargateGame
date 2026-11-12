# StargateGame

A work-in-progress first-person multiplayer exploration game built with C#/.NET,
FishGfx, and Jolt Physics. Explore generated planets, dial physical DHDs, travel
through Stargates, move physics props, and build in persistent worlds.

Breaking changes are expected. Use matching client/server builds and content;
changes to world generation or save formats may require a fresh universe.

## Source and assets

This repository contains game code, shaders, scripts, tests, documentation,
small asset metadata, and required native runtime libraries. Models, textures,
audio, fonts, generated collision/prefab data, cooked packs, and captures stay
local and are excluded from Git. A clone alone is not enough to play.

FishGfx and FishUI are vendored source snapshots. FastNoiseLite and LayerProcGen
are source submodules; Jolt and joltc submodules support native rebuilds. See
[REPOSITORY.md](REPOSITORY.md) for the asset layout and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for licensing.

## Set up a development checkout

- Windows x64 and an OpenGL 4.0-capable GPU for the rendered client and local harness.
- The .NET SDK selected by [global.json](global.json).
- PowerShell and Python for the build, asset, and testing scripts.
- A local copy of the required assets. No automatic asset download is configured.

The dedicated server supports Windows x64 and Linux x64. Native physics binaries
are included; rebuilding native code or importing assets has additional tool
requirements documented by the relevant tools.

```powershell
git clone https://github.com/sbarisic/StargateGame.git
cd StargateGame
git submodule update --init thirdparty/FastNoiseLite thirdparty/LayerProcGen
./scripts/Restore-LocalAssets.ps1 -SourceRoot E:/Path/To/ExistingAssetCheckout
```

Replace the example asset path with a populated checkout. Restore copies missing
ignored assets without overwriting existing files. A full restore needs several
GiB; reuse an existing populated checkout when possible. For compilation and
headless work, see the `-MetadataOnly` workflow in [REPOSITORY.md](REPOSITORY.md).

## Start the game

In Visual Studio, open `StargateGame.sln` and select the **Testing menu** launch
configuration, or start `StargateGame.Client` directly. Building the client also
builds its Server and Harness dependencies and records their matching output
paths. Keep those projects enabled in the solution configuration.

From PowerShell:

```powershell
./scripts/Run-Client.ps1
```

The main menu offers **Local Testing** and **Connect to Server**. Choose a local
scenario to start with defaults; advanced settings are optional.

| Scenario | Use it for |
| --- | --- |
| **Sandbox** | Two functional gates/DHDs, props, movement obstacles, equipment storage, a furnished building, and vegetation/water inspection. Transport rings and the ZPM hub are visual-only assets. |
| **Planet Explorer** | Generated terrain with biome, landscape, settlement, seed, time, and weather controls. Presets include mountain-meadow, forest-interior, desert-mesa, coastline, and icy-highlands. |
| **Travel** | An isolated universe using production addressing and cross-world travel, with known icy/desert routes and on-demand destination loading. |

Press **F6** in a menu-owned local session for scenario selection, camera
bookmarks, player relocation, environment settings, captures, a second client,
and player/station/scenario resets. Camera bookmarks do not move the player.
Local test saves and settings are separate from normal play.

### Connect to a dedicated server

Use **Connect to Server** to enter a host, port, and player name, or run these
commands in separate terminals:

```powershell
./scripts/Run-Server.ps1 -Bind 0.0.0.0 -Port 7777 -Universe my-universe
./scripts/Run-Client.ps1 -Server 127.0.0.1 -Port 7777 -Name Explorer
```

For another machine, replace `127.0.0.1` with its address. TCP port 7777 must be
reachable; the game does not configure firewalls or routers. Stop the server
with Ctrl+C. Buildings, scenery edits, doors, props, and location clocks persist
in the selected universe. Local testing controls cannot modify a remote server.

## Basic controls

| Input | Action |
| --- | --- |
| WASD / mouse | Move / look |
| Shift / Space | Sprint / jump |
| Escape | Release the cursor and open the menu |
| Left click on a DHD key | Enter a glyph or activate its orb |
| E | Interact with props, doors, or storage |
| Hold B | Open the building picker |
| Hold Q in Sandbox | Open the spawn menu, including wooden lanterns |
| I / 1–4 / 0 | Open weapon inventory / select a slot / empty hands |
| Tab | Open the address book |
| F3 | Show physics, terrain, and rendering diagnostics |
| F6 | Open local testing controls |
| F12 | Capture a screenshot |

Sandbox accepts six distinct available destination glyphs followed by the orb;
the local origin key is optional there. Production routes require the six
destination glyphs, local origin, then the orb. Travel is one-way through an
outgoing connection; close it and dial back for the return trip. See
[DHD_DIALING.md](DHD_DIALING.md) for dialing rules and
[BUILDING.md](BUILDING.md) for placement and interaction controls.

## Automated testing and AI development

Use `scripts/Test.ps1` as the shared entry point. Menu and CLI scenarios come from
the same catalog. Defaults are Release, seed `7319`, one client, performance
graphics, and isolated test state.

```powershell
./scripts/Test.ps1 list -Json
./scripts/Test.ps1 check                          # small quick group
./scripts/Test.ps1 check -Group building,ui,audio
./scripts/Test.ps1 run -Scenario sandbox -Recipe gate-roundtrip
./scripts/Test.ps1 run -Scenario sandbox -Recipe lantern-lighting
./scripts/Test.ps1 run -Scenario planet-explorer -Preset icy-highlands -Recipe visual-check
./scripts/Test.ps1 run -Scenario travel -Recipe travel-roundtrip -Cold
```

`run` builds incrementally, waits for readiness, executes a recipe, records
results, and closes owned processes. `start` creates a persistent session for
interactive or AI work:

```powershell
$s = ./scripts/Test.ps1 start -Scenario sandbox -Json | ConvertFrom-Json
try {
    ./scripts/Test.ps1 status -Session $s.session -Json
    ./scripts/Test.ps1 wait -Session $s.session -Condition capture-ready -Timeout 120
    ./scripts/Test.ps1 capture -Session $s.session -Name sandbox.png
} finally {
    ./scripts/Test.ps1 stop -Session $s.session
}
```

Typed `action` requests can inspect state, apply bounded input, interact through
gameplay commands, change settings, and reset the scenario. Captures wait for the
requested rendered state and include a JSON state snapshot. Session logs and
results remain under `%LOCALAPPDATA%/StargateGame/Testing/sessions/<id>/`.

Reuse builds and caches with `-BuildRoot`, `-ContentCache`, and `-NoBuild`; stop
sessions before rebuilding their binaries. Prefer focused checks and extend
Sandbox before adding scenarios. Full suites and stress matrices are opt-in.
See [TESTING.md](TESTING.md) for action schemas, recipes, readiness, isolation,
failure reporting, and current validation limitations.

## Build and package

```powershell
./scripts/Build.ps1
./scripts/Publish.ps1 -Client
./scripts/Publish.ps1 -ServerRuntime linux-x64
```

Outputs default to `artifacts/publish/`. The Windows client package includes the
matching server and coordinator needed by Local Testing outside the checkout.
Copy the complete package. Framework-dependent packages require .NET 10.
Asset cooking uses the local asset library; distribution must respect its licenses.

Keep generated output out of Git. Reuse incremental caches and use
`scripts/Clean-Artifacts.ps1` to review cleanup options when disk usage grows.
Before committing, stage source changes and run
`python tools/check_source_repository.py` to check the index for excluded payloads.

## Further reading

- [Development guidance](AGENTS.md) and [testing workflow](TESTING.md).
- [Graphics](GRAPHICS.md), [audio](AUDIO.md), and [lanterns](LANTERNS.md).
- [Terrain](TERRAIN.md), [streaming](STREAMING.md), and [settlements](SETTLEMENTS.md).
- [Building and persistence](BUILDING.md) and [equipment](HYBRID_GRASS_WEAPONS.md).
- [Galaxy design](GALAXY_SYSTEM.md), [addressing](STARGATE_ADDRESSING.md),
  [dialing](DHD_DIALING.md), and [travel](STARGATE_TRAVEL.md).
- [Asset loading](ASSET_LOADING.md), [asset provenance](assets/PROVENANCE.md),
  and [native rebuilds](native/README.md).

Feature documents include implementation notes, historical measurements, and
deferred designs. They are not a claim that every current change has been
playtested; check their validation notes when planning development work.
