# Persistent worlds and creative building

Buildings, cleared scenery, doors and crates belong to a persistent location. The
terrain remains generated and immutable. Everyone on a server can build and edit;
there are no resources, claims, decay or damage in this release.

## Controls

| Control | Action |
| --- | --- |
| Hold B | Open the prefab picker and unlock the cursor; release to return to placement |
| Left click in picker | Select a thumbnail (selection does not place the piece) |
| Mouse wheel | Change picker page while B is held; change selected piece while placing |
| R | Rotate a freestanding piece by 30 degrees; cycle compatible attachment sockets |
| Page Up / Page Down | Adjust a new foundation by 0.25 m |
| Left mouse | Place the preview |
| Hold left mouse for one second | Demolish/clear with the selected tool |
| E | Open/close the targeted door |
| Escape | Leave building first; a subsequent press opens the pause menu |
| F3 | Physics, building revision, collision batches and save diagnostics |

Aim at an existing piece to snap to its nearest compatible edge. Start with a
foundation on dry terrain, including the Stargate clearing and approach. All edits require an unobstructed target within 8 m. The server
checks players, crates, scenery, support, bounds and door-swing clearance. A green
preview is a local geometry check; the server can still reject occupied space.

The picker contains 28 textured prefabs: 16 Village pieces plus a generated stone
foundation, and 11 Fortification components. Village floor/foundation tiles use a
2 m grid and walls are 3 m high. The complete tiled roof spans 6 x 6 m; attach its
two gables to the front/rear roof sockets. Fortification pieces retain their native
metre dimensions. Roots, stairs, floors, wall edges, openings and upper landings
have explicit attachment sockets. Press R to choose another compatible socket.

Start a Village base with stone foundations, add wooden or brick floors, then
walls, windows, doorway, door and roof. Foundation tops must sit 0.15-0.85 m above
canonical terrain, including attached foundations. Non-foundation ground pieces
allow slight burial. Attached parts follow a retained parent chain; remove their
dependents first. Maximum height is 24 m, with 1,024 pieces per structure and
4,096 per location. Separate structures do not share support.

The wooden door opens with E and uses matching rotated collision. The Fortification
swing/sliding gates are currently **fixed barriers**, and their control box is
 decorative. The picker labels these explicitly. Individual imported pieces have
textured colour/shadow rendering and authored triangle collision. Wooden floors,
stairs and doors supply the wood footstep surface.

Existing procedural buildings remain editable with their original 3 m geometry;
new kit parts start their own structure rather than mixing incompatible grids.
No saved building is removed or converted by this update.

## Saves and identity

The dedicated server owns saves. Default Windows location:

```text
%LOCALAPPDATA%\StargateGame\Server\saves\default\universe.sqlite
```

Linux uses the corresponding local application-data directory. Override with
`--save-dir PATH --universe NAME`, or `Run-Server.ps1 -SaveDir PATH -Universe NAME`.
Names accept letters, digits, hyphens and underscores. Visual Studio's normal
dual startup uses the same save directory across builds. Only one server can own a
universe at a time; use different names for independent sessions.

Universe/location GUIDs are separate from procedural seed, settings hash and
future gate addresses. The developer preset/seed selector retains a location
record. The repository also supports independent same-seed locations and multiple
aliases resolving to one location; this does not implement gate dialing.

`StargateGame.Persistence` owns a single background SQLite connection, WAL and
FULL synchronization. Schema 3 stores locations, structures, pieces, scenery
tombstones, crates, aliases, migrations and successful request receipts. Building
edits and their receipts commit in one transaction before success is reported.
Crates checkpoint every five simulation seconds and on switch/shutdown; abrupt
termination can lose up to five seconds of crate motion. Inactive worlds do not
simulate. Player positions reset to gate spawns and are not saved.

Generator/settings, scenery catalogue and building-kit versions are retained.
Unknown/newer versions fail explicitly. Migrations use the SQLite backup API and
retain three backups. To restore, stop the server and restore a selected backup to
a separate universe directory as `universe.sqlite`; it retains its universe ID.
Never replace a live database or discard its WAL. Terrain cache deletion does not
delete saved changes. No native Jolt checkpoint enters a save or network message.

## Simulation and rendering

`StargateGame.Building` is graphics independent. Legacy tile vertices use integer
coefficients of `1` and `sqrt(3)` in half-edge units, with discrete orientations.
Transforms derive from the structure origin, so square/triangle loops close
without successive snapping error. Server and client share geometry and support.

Protocol 9 sends bounded world-state fragments before terrain preparation. Edits
are serialized with streaming/planet switches. Clients prepare shapes, acknowledge
readiness, and stop prediction at an announced tick at least twelve ticks ahead.
At that tick the server rechecks occupancy, holds simulation while the database
transaction completes, applies collision changes, increments the physics epoch,
and sends a baseline. Failed preparation/saving leaves the old world active.
Confirmed request receipts survive reconnects and restarts.

Static building geometry is batched by canonical owner chunk; required collision
coverage includes owners of pieces crossing into another resident chunk. Only
changed building batches rebuild. Scenery tombstones rebuild affected terrain/
scenery colliders after ordinary placement resolution, without changing canonical
chunks or rerolling neighbouring candidates. Static shadows invalidate on changes.

The new kit uses source UVs, base colour, normal and metallic/roughness maps, with
material batching and bounds derived from the imported meshes. Client and server
share the embedded prefab catalogue; its checksum contributes to physics content
compatibility. Protocol 9 carries prefab IDs, offsets and attachment parents. Both
client and server must be updated together.

Reproduce assets with `python tools/import_building_kit.py --export`. Blender reads
the supplied component preview and original MegaKit glTF files without modifying
them, then renders 256 x 192 thumbnails. The importer converts to Y-up metres,
normalizes component pivots, batches materials and writes collision metadata.
See `assets/building/provenance.json` and the accompanying license files:
Quaternius MegaKit is CC0; TuszPro Kit Fortification is CC BY 4.0.

## Validation tools

- Tests executable `--prefab-only`: native kit sockets, complete base, collision, door, surface and save/reload checks.
- Tests executable `--prefab-demo PATH`: a separate snapped 6 x 6 m inspection base.
- Client `--building-picker-check --benchmark-seconds 20`: captures both picker pages, resize and return to placement.
- `scripts/Test.ps1 -Building`: geometry, physics, durability and two-client tests.
- `scripts/Test.ps1 -Building -Delayed`: two clients through 100 ms RTT / 20 ms jitter.
- Tests executable `--building-demo PATH` creates a separate 40-piece inspection save;
  add `--large` for a synthetic 1,000-piece rendering fixture. Fixtures bypass live
  placement rules and must not be used as examples of legal support/terrain sites.
- Client `--view buildings --benchmark-seconds 5 --vsync off` captures a repeatable
  inspection view. These flags select a camera only, not a save directory.

See `VALIDATION.md` for measured results and remaining manual checks.


## Automatic village corners (2026-11-07)

Right-angle village wall joints now receive the imported corner trim automatically, including existing saved bases. Rendering, sunlight/gate shadows, picking and native collision use the same derived geometry. Removing a joined wall removes its trim. Explicit nearby trim prevents duplicate decoration; no save migration or source asset change is required.

## Generated settlements

Generated assemblies use the existing prefab, collision-owner and door paths.
`StructureRecord.Generated` is authoritative. Demolition, attachments and edits
are rejected; door use still validates reach, visibility and swing occupancy.
A whole building is one static root, with separate moving door children. These
assets do not enter the player's build menu. IDs derive from location and plot
identity; initial materialization and subsequent door state are saved atomically.
See [SETTLEMENTS.md](SETTLEMENTS.md). Current wire version is 12.

## Complete player building pieces (2026-11-08)

The hold-B palette now offers Wall with window, Wall with door, and Finished roof
6 x 6 m alongside plain walls, floors, foundations and stairs. Window inserts,
door frames/leaves, roof tiles/gables and automatic corner trim are no longer
separate menu choices. Their original IDs remain available to existing saves
and generated settlement templates.

A door wall is one placement transaction: its frame and wall are static, and its
owned door leaf still opens with E. Removing either part removes the complete
assembly, subject to normal dependent-piece checks. Complete thumbnails and
placement previews include the leaf. The roof includes both gables.

`tools/assemble_building_prefabs.py` composes the retained imported assets and
collision into three runtime models; Blender renders their thumbnails. The main
kit importer runs this step automatically. Recipes and input hashes are recorded
in `assets/building/assemblies.json`; original asset licenses remain applicable.
Client and server must both be updated because the prefab content hash changes.
