# NPC framework

The first definition uses the existing Military Tactical Soldier asset. Sandbox's
held-Q menu has **Military Soldier** (spawn) and **NPC Orders** (equip). Equipping
orders selects the most recently spawned soldier if no soldier is selected.
Select another visible soldier with left click, then hold Q to choose an order.
The Q menu shows the selected target, click instructions, and live order status.
Move To and
Wander take one surface click; Patrol takes 2–16, confirmed with Enter. Right click
cancels point entry; Escape leaves the tool. Hold and Follow Me act immediately.
Orders and removal require the spawning player's identity or local developer
authority. There are at most 16 NPCs per location.

## Simulation and persistence

`CharacterDefinitions` owns the model, animation names, capsule and locomotion
profile. This release has one navigation/capsule profile, the soldier's 32 cm
radius and 1.8 m height. The shared `ICharacterMovement` adapter preserves player
acceleration, friction and support velocity. NPCs use Jolt and the player stair
surfaces, with the existing 30 cm step and 45 degree slope limits.

NPC IDs are persistent GUIDs, separate from player IDs. Sixteen reusable native
controller slots keep rollback body topology stable. `PhysicsSnapshot.Npcs`
records identity, slot, pose, velocity and the intended movement command with its
tick. Checkpoints save these states with Jolt's state recorder. Reconciliation
restores the baseline's NPC membership before replaying player inputs. An NPC
command can extrapolate for six ticks, then becomes neutral. Render interpolation
uses a separate history and never supplies collision positions.

Lifecycle and standing orders use the `npcs` stream, with revision/effective tick;
movement uses normal physics baselines. NPC edits do not increment the physics
epoch or transfer terrain. Rendering shares skeletal assets and character LODs,
with separate animation clocks and consistent poses for deferred/shadow passes.

`NpcStorage` stores location-owned records in the `npcs` table. Active tasks,
paths, perception and memory are reconstructed after loading. Follow becomes
Hold after a disconnect, departure or server restart. Idle locations suspend AI;
distant agents stop requesting movement and remain at their last safe position
when collision coverage unloads. NPCs cannot travel through gates or be grabbed
or used as cable anchors.

## Navigation and reactions

DotRecast Recast and Detour are pinned to **2026.3.1**; the distributed license is
in `licenses/DotRecast.txt`. Crowd and Detour.Dynamic are not used.

Tiles target 32 m, with 10 cm horizontal / 5 cm vertical cells. The 15 cm setting
uses 31.95 m tiles to keep boundaries on the voxel grid. Two worker jobs per
location build immutable terrain, scenery, building and player-stair geometry.
Navigation uses committed collision inputs, not pending replacements. Tile
fingerprints include neighboring geometry needed by derived caps and foundations.
Stale job results cannot replace current tiles. Unaffected corridors remain
usable; affected corridors stop until their tiles match committed collision.
Moving actors and props use live avoidance and Jolt collision, not tile rebuilds.

The default changed from 20 to 10 cm after Sandbox movement checks on 2026-11-13:
20 and 15 cm rasterization disconnected the first house entrance after clearance
erosion; 10 cm retained a usable route. All three resolutions remain available
for harness comparisons. The soldier capsule and player movement are unchanged.

The Detour polygon corridor is converted to straight-path steering points.
Corner waypoints are advanced only when a navigation ray can reach the next
segment on the same floor. Follow routes target the player's walkable position
and stop at visible separation, avoiding offsets inside walls. Bounded capsule
probes select a consistent passing side around nearby obstacles; probes follow
navigation height and leave normal stepping to Jolt. Progress measures remaining
route distance, so oscillating against a corner does not count as advancement.
Queries allow 5 mm of endpoint rounding at navigation polygon edges and sample
height on the visited polygon, preventing exact corner waypoints from stalling.
Missing tiles report PendingNavigation; a complete failed query reports Unreachable.
Failure to advance reports Blocked, with three delayed retries and live obstacle
checks before retrying again. Door traversal links are derived from the closed
leaf and do not depend on door pose. Agents approach outside the full swing
envelope, use the server's door service and wait for the collision commit. NPC
capsules participate in door obstruction checks. Opened doors stay open.

Standing orders survive interruptions. Decisions and sight run at staggered
10 Hz; movement runs at 60 Hz. Ordinary impacts attract investigation. Accepted
gunfire or a visible hostile player triggers flight. Sight is 40 m / 110 degrees;
hearing is 50 m for gunfire and 15 m for impacts, quartered through occluders.
Sound positions include uncertainty and do not track their source afterward.
Danger lasts five seconds after its latest observation; hostile memories last
ten seconds and ordinary memories five. After flight, a short search resumes the
standing order. Self-generated contact stimuli are ignored.

## Harness and coverage

These commands are provided for developer execution; they were **not run** during
implementation:

```powershell
./scripts/Test.ps1 check -Group npcs
./scripts/Test.ps1 run -Scenario sandbox -Recipe npc-lifecycle
./scripts/Test.ps1 run -Scenario sandbox -Recipe npc-follow-buildings
./scripts/Test.ps1 run -Scenario sandbox -Recipe npc-navigation
```

The focused group covers order validation, native slot limits, movement,
historical spawn/removal rollback without prediction resets, pending navigation,
capsule obstacle probes, follow-target visibility, and location-isolated
persistence. The lifecycle recipe checks late-join spawn, orders and removal.
The navigation recipe uses identical flat, two-flight stair,
doorway and house-detour probes for 10/15/20 cm cells, writes
`npc-navigation-comparison.json` under the session directory, captures overlays,
and restores the 10 cm default afterward. Route results are reported, not assumed successful.
Tile timings include collision-input extraction and Recast construction. `Bytes`
is the mesh/detail array payload estimate, not total process memory or peak
worker allocation; compare process measurements separately when profiling.

`npc-follow-buildings` tests two generated houses, including a rotated house.
It spawns a soldier, issues Follow, sets up the player inside a closed doorway,
and waits for the soldier to open the door and walk inside. It also checks exit,
following a walking player, and routing around the outside wall. NPCs are never
teleported; player teleports are labelled setup. The recipe writes sampled
movement, task and probe diagnostics to `npc-movement.json`, with bounded waits
that fail the run if movement does not complete. Fixture NPC and cable storage
honours the harness save directory instead of sharing the normal Sandbox store.

Validation on 2026-11-13: Release build, focused `npcs` checks, and all eight
movement phases passed. Successful movement session:
`dd76b6dd098a463ca3574a683aea9154` (43.9 seconds including startup).
The checks caught a doubled vertical offset in the steering capsule that falsely
hit doorway lintels, and the polygon-edge query issue on the rotated entrance.
This is targeted movement coverage; crowded entrances, multiplayer latency and
all other navigation configurations are not covered by this run.

The typed harness `npc` action accepts a `command` and optional `debug` flag.
Commands include gameplay `spawn`, `order`, `remove` and local-developer-only
`configure`, `probe`, `debug`, `noise`, `gunshot`. `configure` accepts `cellSize`
and a `hostilePlayer` ID for neutral-versus-hostile perception checks. `probe`
takes `origin` and `point` and returns navigation outcome and path. Stimulus
commands take `point` and enter authoritative perception without playing audio.
No game command-line diagnostics were added.

Example action file for a live Sandbox session:

```json
{
  "action": "npc",
  "data": {
    "debug": true,
    "command": { "action": "configure", "cellSize": 0.1, "hostilePlayer": 0 }
  }
}
```

Request `debug` again to refresh server geometry/perception overlays. Green/orange
tile edges show current/pending work; cyan shows paths; door links show traversal
state; perception rays and memories distinguish visibility and danger. Magenta
capsules show simulation proxies and cyan capsules show interpolated actors.
Harness status includes NPC tasks/outcomes, tile revisions/timings, collision
states and render states. Physics baseline histories retain the corresponding
simulation ticks for latency inspection.

Generate the menu thumbnail with Blender's background Python runner and
`tools/render_npc_thumbnail.py`. It poses the existing runtime soldier skeleton
and renders its supplied materials; the generated file is
`assets/gui/building/military-soldier.png`. The existing soldier asset credits
remain unchanged.

Protocol is **33**, repository schema is **11**, and NPC table records use schema
**1**. Existing locations gain an empty NPC table. Matching client/server builds
and refreshed cooked assets are required. Physics/content identities include the
new source. There is no native bridge API change. Builds, test execution,
profiling and playtesting remain with the developer.
