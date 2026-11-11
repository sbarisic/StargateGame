# Persistent worlds and creative building

Buildings, cleared scenery, doors and crates belong to a persistent location. The
terrain remains generated and immutable. Everyone on a server can build and edit;
there are no resources, claims, decay or damage in this release.

## Controls

The picker keeps working if a thumbnail is missing or invalid: it shows a neutral
placeholder and logs the failed path once per session. The assembly asset tool
generates missing thumbnails from all selectable catalog entries.

Unconnected building-kit wall, window and doorway ends receive automatic flush
stone caps. Caps are derived trim owned by the wall, including its collision and
shadows; they are not selectable or saved separately. Connecting another wall
section removes the cap, and removing that connection restores it. Existing
corner trim is retained. `tools/build_wall_caps.py`, invoked by the assembly
tool, generates the hidden cap models from each wall's endpoint geometry.

The cap catalog changes content compatibility and the movement source changes
physics compatibility through the existing hashes. Use matching client/server
builds; existing saves need no migration.

| Control | Action |
| --- | --- |
| Hold B | Open the prefab picker and unlock the cursor; release to return to placement |
| Click Exit build mode | First entry on page one; closes the picker and clears placement preview |
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


## Placement, foundation edges and stair movement (2026-11-11)

Floor placement checks connected occupied surfaces and containment rather than
reserving one convex footprint around an entire assembly. Floor assets with open
sides use their authored thickness as a closed placement volume. Floors may meet
the authored tops of all adjoining walls in the same structure, including corner
trim, while duplicates and actual crossings remain blocked. Client previews and
server placement share these rules; rejection text names the blocking piece.

The earlier raised wall base course has been replaced by foundation extensions.
Kit foundations retain their top and bottom elevations and use the same stone
material at the original two-metre texture scale. Only supported wall spans extend
outward, flush with the masonry shell. Window inserts and door hardware do not
set the extension width. Door gaps remain open and thresholds retain their height.
Foundation boxes and extensions are joined in structure-local coordinates, with
internal faces removed; end caps remain separate. Foundations own the derived
collision, and edited structures rebuild affected owners when walls are added or
removed. There are no extra saved pieces or migration steps.

Generated houses use wall-run metadata named `baseCourses` for their foundation
extensions. The generated footing bottom is -1.12 m and the walking/floor datum is
0 m. No extension rises above that datum. Near assets were restored from retained
kit/prepared GLB sources, then clipped only where the current foundations cover
them; distant house meshes were regenerated from those restored assets. Reproduce
with `python tools/import_settlements.py --houses-only`, followed by Blender's
background `tools/build_settlement_lods.py -- --houses-only`. The former in-place
repair command intentionally refuses to run because it cannot restore cut faces.

Visible stair treads and prop collision are unchanged. Player collision instead
uses explicitly authored continuous flight profiles for Sandbox, the interior
kit stairs, exterior straight stairs, and Fortification stairs. Player surfaces
preserve landing support, rails, side boundaries and original undersides. Separate
static layers make treads collide with props only and the smoothed surfaces with
characters only. Interaction rays exclude the invisible player surfaces. All
layers are cooked before activation and replaced together at a simulation boundary.
Ground movement, acceleration, friction, slope limits and camera filtering are
unchanged. Jolt's 30 cm stepping remains available for ordinary ledges.

Sandbox includes a comparison incline at (-28, 0, 8), beside the stairs at
(-24, 0.3, 8). It has the same 1.35 m rise / 2.25 m run as a staircase flight and
is not selectable in the build palette. `tools/build_stair_reference.py` generates
it, and the regular kit assembly pipeline also includes it.

Same-world collision updates retain active bodies during preparation, replay
unacknowledged inputs at commit and preserve presentation. Door requests retain
the two-sided camera-ray hit point, authoritative reach/visibility checks and
swept obstruction checks. Removal requests now also require the aimed hit point (protocol 25).

Physics and content compatibility hashes change for this update. Rebuild matching
client/server binaries with updated assets. The new collision layers are configured
in managed code and require no new native exports; the existing bridge-source
identity check remains enabled. If it reports a stale DLL, run
`python tools/build_native.py` before rebuilding client/server. No builds, tests,
or playtests were run for this update; only the requested asset generation ran.


## Door targeting and demolition (protocol 25)

Door obstruction uses authored leaf sections from `tools/build_door_collision.py`.
The kit assembly and settlement import pipelines generate these profiles. The kit
leaf is fitted inside its jamb and its pivot moves to the front hinge edge.
Closed placement and prefab IDs remain unchanged. Each
profile caches 90 one-degree intervals, with a chord-error allowance. Swing
obstruction is dynamic-only: frames, walls, foundations, terrain, fixed scenery
and map edges never prevent opening or closing. Player tests use capsules and
movable prop tests use their convex hulls, including gate clipping. Rejections
log the door, dynamic blocker, section, interval and query time. Reach and
line-of-sight checks remain separate: walls still prevent interacting through them.
This policy changes the physics compatibility hash; rebuild client and server
together. No asset regeneration, native rebuild or save migration is needed.

Building targeting shares prefab-local triangle BVHs and revision-dependent
world spatial buckets. Door state changes invalidate affected instances. Native
queries handle live non-building obstacles; indexed building queries retain
individual ownership even when physics bodies are chunk-batched. Prompts and
interaction use the same frame result. Aggregate query cost/candidate/triangle
counts are logged every 15 seconds of active querying, not every frame.

Interior stairs, Entrance platform, the two fixed gates, Bollard and decorative
Gate control are non-selectable. Their IDs/assets remain valid in existing saves.

Demolition resolves assembly children to their removal owner. The highlight uses
actual rendered model parts and derived trim, a visible-surface mask, tint and
inner outline. It retains its resources until the target/revision changes and
accounts for the mask and generated geometry in GPU-memory reporting. Hold left
mouse for one second; the target name and progress bar remain visible. Release,
target loss/change, invalid targets, opening the palette, pausing or exiting
build mode cancels progress. Completion sends one request and requires release
before another attempt. Pending removal says “Demolishing…”. Door feedback does
not appear as demolition feedback. Removal validates the aimed surface, reach
and visibility while preserving dependency and generated-structure protection.

Regenerated catalog/collision data and changed simulation sources update content
and physics identities. Use matching rebuilt client/server binaries and assets;
no save migration or new native ABI is required. Existing native-source identity
checks remain enabled. Implementation included asset generation only: builds,
tests, profiling and playtesting remain with the developer.


## Exterior corner contact

Building-kit stone corners use a closed vertical convex footprint for physical
collision. This fills decorative gaps and removes per-stone ledges/undercuts that
can snag a capsule approaching at an angle. The original height and horizontal
extents are retained. Rendered stones, shadows, picking and demolition highlighting
keep the detailed mesh. Both derived corners and explicit corner pieces use this
collision path; end caps, foundations, openings and stair profiles stay unchanged.
The same cooked geometry serves authoritative simulation and client prediction,
including after loading existing buildings and atomic geometry replacement.
Physics debug geometry displays the smooth corner shell.

The source-based physics identity changes. Rebuild client/server together and
restart Sandbox to load the new collision. No native changes, asset regeneration
or save migration are required. Builds and playtesting were left to the developer.
Manual checks: approach each exterior corner diagonally from either side, slide
along it while walking/running/crouching, reverse away and jump near the footing.


## Door seating and threshold support

The kit door leaf now extends behind the irregular masonry jamb and lintel edges
so the closed door has no deliberate daylight clearance. The door asset generator
updates both render vertices and collision sections, including the hinge pivot.
Fixed geometry remains excluded from swing obstruction.

Foundation extensions continue across supported doorways at the foundation's
existing top and bottom elevations. This fills the recess under the threshold
without adding a raised step. Generated houses derive the same extension between
the jamb footings beneath an authored header. Refresh their existing metadata
with `python tools/building_base_geometry.py --thresholds-only`; normal settlement
imports also produce it. The kit leaf is regenerated with
`python tools/build_door_collision.py`. Both were run for this change.

Rebuild matching client/server binaries and reload Sandbox for the changed assets
and collision. Existing saves need no migration. No builds or playtests were run.


## Connected end caps and scanned masonry

End caps use the wall's stone masonry material, not the wall manifest's first
(timber) part. Touching wall endpoints are compared in world coordinates with a
2.5 cm placement tolerance, including separately started structures. Solid-wall
T junctions also suppress covered end caps; windows and door openings do not act
as solid bounding boxes. Caps remain derived from building state, so removal and
reload restore the appropriate exposed ends. Corner trim remains independent.

`tools/import_masonry_materials.py` imports the user-selected local CC0 Poly Haven
sets: `assets_itch/textures/rustic_stone_wall` for stone walls, corner/frame stones
and caps; `assets_itch/textures/stone_brick` for stone floors and foundations;
`assets_itch/textures/stone_wall` for plaster surfaces. Wood uses the supplied
`assets_itch/textures/wood060` set; tile roofing uses
`assets_itch/textures/roofing_tiles_007`. Both are ambientCG CC0 sets. Their authored
UVs preserve grain and tile orientation, including curved roof geometry. Changes
apply to kit parts, assembled pieces, and near/far generated mega-house models.
The supplied 1K maps retain their source resolution. OpenGL normals are converted
to the runtime DirectX convention; AO/roughness/metalness/height share one RGBA map.
Credits and source hashes are in `assets/building/masonry-materials.json`.
The import also updates the kit and generated-house material tiles and UVs.

Generated mega houses now include the same stone doorway frame as the build-menu
assembly. Source re-export reapplies foundation clipping to its bottom surfaces,
then regenerates house LODs so the old unframed doorway cannot return at distance.
Use `python tools/import_settlements.py --houses-only`, then Blender background
`tools/build_settlement_lods.py -- --houses-only`. Regenerate palette icons with
Blender background `tools/assemble_building_prefabs.py -- --refresh-icons`.
Icons use the runtime albedo, normal and roughness maps. The refresh flag replaces
existing images rather than only filling missing thumbnails.

The renderer reads the displacement map from material alpha and applies bounded
parallax occlusion with 16/32/48 maximum steps on Low/Medium/High, adaptive to
view angle, followed by five binary intersection refinements. Distance ranges
are 18/28/40 metres, with grazing-angle fade matching terrain. Material
`heightScale` is recessed depth in world metres, independent of UV scale.
Stone wall relief is 17.5 cm, rustic masonry 8 cm, floor brick 2 cm, roofing
3.5 cm, and wood 30 mm. Original UV derivatives stabilize texture mip selection.
Building instances, foundation extensions, props and static model/rock draws all
bind authored height-enabled materials; missing height maps leave POM disabled.
Albedo, normal, roughness and AO use the displaced surface coordinates together.
This is surface relief, not silhouette tessellation or physical displacement;
collision remains smooth. Existing saves need no migration. Rebuild matching
client/server and reload assets because cap collision and content identities
change. Asset generation ran; builds, tests, profiling and playtesting did not.

## Floor finishes beside walls

Shared prefab placement accepts thin floor finishes at the foot of perimeter
walls, windows, doorways and corner pieces in the same structure. The floor must
share the wall datum within 2.5 cm and stay within 3.5 cm above/below it. Wall
alignment must follow a floor edge; crossing walls, duplicate floors and unrelated
structures keep their overlap rejection. The rule applies in both placement
orders and is shared by preview and server acceptance. Existing wall-top support
rules are unchanged. No builds or playtests were run for this change.

## Height-map self-shadowing

Height-enabled models and terrain now trace a second ray from the POM surface
intersection toward the sun, based on LiveTrower's CC0 implementation:
https://godotshaders.com/shader/parallax-occlusion-mapping-with-self-shadowing/
The port keeps this renderer's lighting rather than replacing it with Godot's
custom BRDF. Bounded adaptive loops, explicit texture gradients, a depth bias and
a softened occlusion test limit acne and aliasing. Terrain visibility follows
material/projection blends, including roads and beaches. Height-field visibility
multiplies both direct sun diffuse and specular, together with cloud/world shadows;
ambient and emissive light remain intact. Local gate/DHD lights still use their
existing lighting and do not perform additional height-field shadow rays.
No silhouette, shadow-map geometry or collision displacement is introduced.
No builds, shader compilation, profiling or playtesting were run.

The stone_wall depth range is set to 17.5 cm (half the previous 35 cm) to expose the mortar recesses.
Its source height map uses only part of that range for typical stone-to-mortar
contrast; this value is the full POM slab depth, not a uniform 17.5 cm indentation.
Other materials keep their current depth. No builds or playtests were run.
