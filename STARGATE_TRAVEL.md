# Stargate travel



Updated: 2026-11-08



The working implementation now supports authoritative one-way player, crate and

barrel travel, persistent device identity and journaled ownership. The validation

record below distinguishes checked behavior from remaining acceptance work.



## Current implementation



- Protocol 15 authenticates a Windows-account P-256 key before preparing a world.

  The DPAPI-protected key is shared by game copies under

  `%LOCALAPPDATA%\StargateGame\identity.dpapi`. Names and IP addresses are not IDs.

- Successful reservations start the source opening immediately, without waiting

  for destination generation, collision or client visual readiness. The source

  settles after its own four-second animation; incoming locks remain independent.

- Only front-to-back capture at a settled outgoing gate transfers entities.

  Players capture at the leading capsule; props capture after the entire original

  box/barrel shape crosses. Partial props retain source collision and can withdraw.

- **Every entity waits at least two elapsed seconds from authoritative capture.**

  This includes crates and barrels. Destination readiness extends that hold;

  it is not an additional two seconds after loading. Players stay white and props

  stay captured, outside active simulation. There is no tunnel or exit kick.

- Pose, velocity and spin map through upright, equal-sized gate frames. Arrival

  momentum survives the first predicted movement step. Reverse and incoming-side

  entry remain physical pass-through for players only. Props clip on approach and are destroyed after fully crossing in a forbidden direction.

- A captured entity is journaled before source ownership is removed. Destination

  ownership commits atomically. Restart recovers uncommitted captures to the

  source and retains committed arrivals. Safe-position checks use actual collision.

- Player checkpoints run every five seconds, at disconnect and on travel commit.

  Prop registries allow 0–64 bodies per location; starter props are not recreated

  when an emptied world reloads. Schema migrations 7/8 back up the SQLite save;

  terrain and buildings are retained.

- Test clients use `--identity-profile NAME` with a loopback server explicitly

  started with `--allow-test-identities`. The normal device identity is unchanged.

  `--identity-fingerprint` prints the key fingerprint. Server console command

  `rebind-player PLAYER_GUID NEW_PUBLIC_KEY_SHA256` revokes the old key and

  disconnects affected sessions. No recovery code or hardware identifier is used.

- F3 reports transit phase, elapsed time and recovery/occupied-exit reason. Normal

  crosshair UI remains free of these diagnostic messages.



Verification commands and measured results are in [VALIDATION.md](VALIDATION.md).

The following sections retain the design rationale and broader acceptance checklist.



Destination addressing is covered in [STARGATE_ADDRESSING.md](STARGATE_ADDRESSING.md).

Galaxy coordinates, receiver acquisition, and persistent locations are covered

in [GALAXY_SYSTEM.md](GALAXY_SYSTEM.md).



## Agreed behavior



- Transfer objects between connected gates while preserving their orientation,

  direction of motion, speed, and spin relative to the gate frames.

- An inanimate object transfers only after its entire collision shape has entered

  the source event horizon. Crossing with its centre alone is insufficient.

- A partially entered object remains in the source world and can be pushed,

  pulled, or withdrawn. It does not appear at the destination before full capture.

- Player travel fades to white, holds white through a short transit delay

  (typically about 2-3 seconds, or however long readiness takes), then fades

  from white into the destination after teleportation.

- Do not show a wormhole tunnel, flight sequence, or other wormhole travel animation.

  The fade is the player's travel transition; gate opening and event-horizon

  effects are separate presentation concerns.

- The server controls the transfer. Clients must not independently create a

  second authoritative copy of a player or object.



## Destination resolution before transfer



Resolve the address to its nominal galactic position, apply known drift

corrections, and acquire an eligible receiving gate within **1 light-year** of

the corrected target. Resolve destination associations and receiver priority

before reserving the endpoint. The radius does not merge nearby worlds.



Load the resolved LocationId and reserve the selected GateId. Transfer uses its

exact local gate transform in metres, not the nominal galactic coordinates.

Keep the destination and required terrain/collision resident through transfer.

Do not switch receivers by rerunning a nearest-gate search during entry; validate

the reserved endpoint and use the failure/rollback rules if it becomes unavailable.



## Gate frames and motion



Give each gate a local frame with a position and a rotation. Local up follows

the gate's top, and local outward points away from its front face. Gate transforms

must use consistent units and no scale change for the initial implementation.



For stationary gates, map source-world directions to destination-world directions

using:



```text

R = destinationRotation

    * rotation180DegreesAroundGateLocalUp

    * inverse(sourceRotation)



destinationOrientation     = R * sourceOrientation

destinationLinearVelocity  = R * sourceLinearVelocity

destinationAngularVelocity = R * sourceAngularVelocity

```



The half-turn maps motion into the source's front to motion out of the

destination's front. It is a rotation, not a mirror operation. Preserve mass,

shape, inertia properties, and other physical settings when recreating a body.



A crate entering at 5 m/s should leave at 5 m/s, before any subsequent collision,

gravity, or other external force changes that velocity. An angled throw keeps

its corresponding direction relative to the receiving gate. Spin is preserved

with its axis rotated by the same mapping.



This preserves momentum relative to the gate frames; it does not preserve the

original world-space direction across differently oriented gates. Use the state

at capture, after forces during entry, rather than a stale first-contact velocity.



The usual rigid pose mapping is:



```text

destinationPosition = destinationGatePosition

                    + R * (sourcePosition - sourceGatePosition)

```



Preserve the transformed tangential offset through the opening. Placement along

the gate normal depends on the emergence mode below. Use the body's proper

reference frame and centre-of-mass conventions when applying Jolt state.



Moving or rotating gates are a future extension. Their transfer must remove the

source frame's translational and rotational motion, transform the relative

motion, and add the destination frame's motion. Copying the stationary-gate

formula unchanged would not preserve relative motion in that case.



## Whole-object capture



Treat the source event horizon as a plane with origin `o` and outward unit normal

`n`. For a world-space point `x`:



```text

signedDistance(x) = dot(x - o, n)



positive: in front of the source horizon

negative: behind the source horizon

```



An object is fully captured when its furthest remaining point on the source

side has crossed behind the plane:



```text

max(signedDistance(x) for x in the collision shape) < -captureTolerance

```



Evaluate the current transformed collision shape. An oriented box can use its

support extent in the plane-normal direction; a rotating object must not use

stale or unrotated bounds. A conservative bound can delay capture, but must not

permit capture while part of the actual shape remains in front.



Track a valid front-to-back entry through the aperture. Being behind the plane

somewhere else is not an entry. Use swept crossing and collision checks so fast

objects cannot skip the trigger or bypass the ring between simulation ticks.



The portion crossing the plane must fit through the opening, and collision with

the gate ring must remain active. Do not teleport an oversized object merely

because its centre crossed the plane.



### Partial entry



- Keep one authoritative body in source-world physics until capture completes.

- Clip its rendered geometry at the event horizon, hiding the entered portion.

- Keep the exposed portion available for source-side interaction.

- Do not render or simulate a destination copy yet.

- Cancel the pending capture if the object is withdrawn.



The hidden portion needs a controlled transit space behind the horizon. Ordinary

source terrain or a wall behind the gate must not block it. Rendering alone does

not solve this: collision handling must distinguish the entered portion from

the exposed portion without disabling legitimate ring and source-side contacts.

The exact collision-filtering/transit-space implementation remains to be designed.



## Object emergence



The discussed staged approach starts with single rigid props and stationary gates.

After full capture, the initial version can place the complete object just beyond

the receiving horizon, using the transformed pose and velocities. Check destination

clearance rather than inserting the body inside an obstacle.



Buffered emergence is a possible later refinement: only after full source capture,

place the object's leading surface at the receiving horizon and let it emerge,

clipping the part still behind that horizon. Preserve tangential position,

orientation, and transformed motion. This deliberately changes its normal offset

to represent emission progress.



Buffered emergence also needs destination transit-space collision handling. A

nearly stationary object may not clear the horizon under its captured velocity.

A controlled emission mechanism would need an explicit rule for displacement,

velocity, and interaction with obstacles. Do not silently add an exit kick and

claim unchanged momentum. This refinement is not required for the first travel

implementation.



## Player transition: white fade and incoming-gate delay



The intended player experience is:



```text

Enter the active source horizon

    -> quickly fade the source view to solid white

    -> hold white while the incoming gate animates and forms its wormhole

    -> wait for the receiving horizon and destination scene/collision to be ready

    -> complete teleportation and set the destination camera while fully white

    -> quickly fade white away to reveal the destination

```



There is no wormhole-flight or tunnel animation in the traveling player's view.

The receiving gate still performs its incoming animation and forms its visible

wormhole/event horizon in the destination world.



Agreed timing: allow a slight teleport delay of roughly **2-3 seconds**, or

however long it takes for the incoming gate to finish opening and the destination

to become ready. This is a readiness-driven transition, not a hard three-second

timeout. Keep the view fully white throughout the wait; do not reveal an unfinished

gate or world just because a fixed timer elapsed.



The fade edges themselves should remain quick; 100-200 ms each is still a possible

tuning starting point. The 2-3 second estimate describes the overall transit wait,

not a requirement to spend that long on each fade. The earlier proposal for no

intentional white hold is superseded. If the receiving gate is already open,

do not restart its incoming animation for every subsequent traveler; wait for

the remaining transfer/readiness work.



Begin preparing the destination during dialing so normal travel does not expose

world generation or asset loading. The destination gate, arrival collision, and

required client scene resources must be ready before release and reveal. Source

entry may lead into a pending transit while the reserved receiving gate is still

animating; an active source horizon does not mean arrival is already ready.



Proposed sequencing:



1. Detect a valid player entry and begin the local fade to white.

2. Capture the player's transfer state and place it in authoritative pending

   transit. Coordinate the receiving gate's incoming animation, event-horizon

   creation, and destination preparation using the connection and transfer IDs.

3. Hold solid white until the receiving horizon is usable, arrival collision is

   ready, and the client has the scene resources needed to display arrival.

4. Commit the teleport and apply the destination character/camera state under

   full white. Reveal only the matching authoritative destination state.

5. Fade white away into the destination and complete the transition.



The wait must not zero the player's captured velocity or pause either world's

physics. Keep the traveler in a transfer record with no active duplicate body;

do not let an invisible player walk or fall in either world for those 2-3 seconds.

Resume the preserved, transformed motion on destination release and transform

view direction with the gate frame. Transit is a deliberate transport delay,

not several seconds of hidden ordinary character simulation.

Character movement code must retain arrival momentum instead of overwriting it

with ordinary movement input on the next tick.



Only the traveling player's view fades. Other players continue seeing the game

and the relevant entry/exit effects. Prop transfers do not trigger a camera fade.



Player entry can use a character-specific crossing trigger rather than the

whole-rigid-prop rule. The exact trigger location, handling of controller up for

tilted gates, and input behavior during the fade remain implementation decisions.



If travel cannot complete, do not reveal an unloaded world or spawn the player

into an obstruction. Use a defined failure/recovery path, such as restoring a

safe source state and fading back to it. A timeout must not leave the player

indefinitely at a white screen.



## Authoritative transfer and physics ownership



Suggested states for rigid objects:



```text

Approaching -> PartiallyEntered -> FullyCaptured -> PendingTransit -> Emerging -> Released

                    |

                    +-> Withdrawn

```



On complete capture, the server should:



1. Record the entity's current pose, velocities, and persistent physical properties.

2. If arrival is not ready, retain that captured state in pending transit, with

   no active source/destination duplicate. Wait for the receiving horizon,

   destination world, and suitable arrival volume before releasing it.

3. Prepare the destination body without allowing a second active simulation copy.

4. Commit the ownership change between physics updates: remove the source body

   from active simulation and activate the destination body.

5. Preserve the game entity ID and publish the new world, state, and transfer ID.

6. Retire source-only contacts and prediction history associated with the old world.



Jolt body IDs are local physics handles, not the persistent game entity identity.

Queue transfer requests from contact/trigger handling and commit them at a safe

simulation boundary. Do not mutate worlds or transfer bodies directly inside

Jolt contact callbacks.



Client messages and snapshots must identify the world/transfer they belong to.

Late source-world movement or prediction data must not be applied after arrival.

Duplicate delivery of a transfer must not create duplicate entities.



Keep both worlds and the connection alive while a transfer needs them. Gate

closure, loss of power, or disconnection during entry needs a defined finish or

rollback policy. Before full capture, a rollback may require restoring the last

safe source pose rather than revealing a body inside terrain behind the gate.



Prevent immediate retriggering at the exit through entry direction and per-gate

crossing state. A timer alone is not sufficient for slow or large objects.



## Initial scope and later work



Initial travel scope:



- Stationary gates and one rigid body per prop.

- Whole-prop capture followed by physics-driven, clipped emergence (updated 2026-11-10).

- Motion and orientation transformed between gate frames.

- Player fade to white, a roughly 2-3 second readiness-driven transit wait, and

  fade into the destination, without a wormhole-flight sequence.

- Server-owned, single-instance transfers with destination readiness checks.



Later work or unresolved rules:



- Moving or non-upright receiving gates remain deferred; progressive rigid-prop emergence is implemented below.

- Moving gates and their frame velocities.

- Constrained assemblies, vehicles, trailers, ropes, and held objects. Transfer

  connected objects as a supported group or explicitly prevent that crossing;

  do not silently leave broken cross-world constraints.

- Destination obstruction, iris, and closed-gate behavior.

- Large-object transit volumes, partial-body collision handling, and rollback.

- Player trigger placement, tilted-gate orientation, exact fade-edge timing,

  and input policy. The receiving-gate readiness hold is agreed behavior.



## Acceptance checks for eventual implementation



- A crate remains source-owned until its trailing extent clears the horizon.

- A partially entered prop can be withdrawn without duplication or lost state.

- A rotated long object does not transfer when only its centre has crossed.

- A high-speed object is detected and cannot pass through the gate rim unchecked.

- Differently oriented gates preserve relative exit direction, speed, and spin.

- Source terrain behind the horizon does not trap the entered portion.

- Obstructed or unloaded destinations do not receive overlapping or lost entities.

- Only one authoritative copy exists across retries, disconnects, and reconnects.

- All clients agree on the entity's destination world after transfer.

- Player momentum survives the transition and subsequent character update.

- The player's source scene fades to white and the destination fades in, with no

  wormhole tunnel or flight animation.

- A receiving gate that takes longer than three seconds to open keeps the view

  white until its horizon, scene, and collision are ready; no premature arrival.

- Transit preserves captured momentum without hidden movement during the wait,

  and an already-open receiver does not replay its incoming animation per traveler.

- Travel failure recovers visibly rather than leaving a permanent white screen.



These are future checks, not tests completed by this documentation change.



## Technical references



- [Jolt BodyInterface](https://jrouwe.github.io/JoltPhysics/class_body_interface.html):

  body lifecycle and position, rotation, and velocity operations.

- [Jolt ContactListener](https://jrouwe.github.io/JoltPhysics/class_contact_listener.html):

  callback threading and restrictions on changing physics state.

- [Current physics integration](src/StargateGame.Physics/PhysicsWorld.cs): existing

  body-state capture and restoration to extend when travel is implemented.



## Implementation plan: one-way gate transfer (2026-11-08)



Historical implementation specification. This section makes the earlier travel rules

concrete against the current client/server. It does not authorize or claim a

working teleport path. Connections, ripple effects and F4 relocation already

exist; the latter resets position to a spawn and velocity to zero and must not

be reused as the travel operation.



### Selected behavior



- Travel is source-to-receiver only. An incoming gate never sends an entity back

  or requests source-world terrain on behalf of an incoming-side observer.

- Initially support stationary, upright gates with arbitrary yaw and equal

  physical aperture size. Reject unsupported tilted/scaled endpoint transforms

  explicitly. Tilted character gravity, moving gates and constrained assemblies

  are deferred; single crates and barrels remain in scope.

- Only a settled source membrane accepts entry. The receiver must be settled

  before release. A closed gate, rear entry or incoming-side entry causes no

  transfer and retains the current physical pass-through behavior; no new damage

  or destruction rule is introduced.

- Player capture starts when the leading capsule extent sweeps through the front

  plane by 2 cm and its projected capsule footprint fits inside the opening.

  This deliberately captures before the camera/torso can collide with source

  geometry behind the membrane. Spawn overlap, correction jumps and arrival

  overlap never count as entry.

- Fade to white over 150 ms. Hold for a minimum 2 seconds from accepted capture

  and longer if destination readiness requires it. Commit under full white, then

  fade back over 150 ms. No tunnel animation. Do not replay an already-open

  receiver's incoming animation for each traveler.

- Freeze gameplay input during transit; consume mouse deltas without applying

  them, then restore the transformed captured view. Other players and both worlds

  keep running. Menu/disconnect controls remain available.

- Prop capture continues to require the entire original collision shape behind

  the source plane. Partial props stay source-owned and can be withdrawn. Initial

  arrival exposes the leading 2 cm at the receiver after exposed-hull clearance checks;

  the rest emerges under ordinary physics. Do not add an exit kick.



### 1. Shared geometry and identity



Add shared `GateFrame`, `TransitState` and versioned transfer records. Use the

membrane's actual centre, aperture and entity rotation; source frames must not

assume the model origin is the membrane centre. Test the half-turn transform

above for yaw, tangential offsets, player look, prop orientation, linear velocity

and angular velocity using the engine's matrix convention.



Keep stable player identities. Replace per-world prop slots 1-4 with a persistent

universe entity identity plus separate local Jolt handles. Add bounded runtime

prop/archetype registries and create/remove operations shared by server and

prediction. Four starter props become initial records, not an invariant of every

snapshot or save. Update drawing, picking, buoyancy, sound contacts, horizon

footprints and prediction code that currently searches `Definition.Props`.



Migrate existing props to deterministic IDs derived from location and original

slot. Back up the database; preserve terrain, buildings, poses and velocities.

Replace `SaveBodiesAsync`'s exact-four-body restriction with bounded validation.

Do not require terrain regeneration for travel.



### 2. Authoritative crossing and partial collision



Evaluate crossings on consecutive authoritative 60 Hz states, independently of

visibility and client ripple sampling. Sweep translation and rotation; test the

aperture and keep ring collision. Use conservative subdivision/support tests for

fast spinning barrels. Sample capture velocity after entry forces, not at first

contact. Rearm only after genuine separation on the front side.



For a partially entered rigid prop, retain its original shape for capture and

rendering, but use a convex source-side collision proxy clipped to the front

half-space. Boxes and the existing convex barrel profile make this bounded.

The original body's mass, inertia, reference pose and velocities remain the

physical state; account explicitly for the proxy's different centre of mass.

Only one body integrates forces. Update the proxy at simulation boundaries,

retain front-side/ring contacts, and restore the original shape on withdrawal.

Do not disable all collision for an intersecting body or merely hide it visually.



Prototype this native proxy path first with a wall directly behind the gate.

Partial entry/withdrawal, pushing and rotation must pass before prop travel is

considered complete. Jolt mass/shape replacement and rollback serialization need

explicit verification with the pinned native wrapper. If that path fails, do not

ship partial props with behind-gate collision silently disabled.



Clip the entered portion consistently in colour, sunlight/gate shadows and

selection passes. Source observers see no destination copy before capture. A

small per-entity clip description is replicated; collision/capture authority

remains server-side.



### 3. Transfer coordinator and durable ownership



Add `ServerTravel` alongside `ServerGalaxy`. Bind every attempt to a unique

TransferId, fixed ConnectionId, source/destination LocationIds and GateIds, entity

identity, endpoint revisions and physics epochs. Never resolve the receiver again.



States: `Captured -> Preparing -> Ready -> Committing -> Released`, with explicit

`Returning` and terminal `Cancelled`. Store the captured original shape/pose,

velocities, character movement state, safe source pose and timestamps. Pending

transit has no actively simulated source or destination body.



Pin both locations and the connection. Queue captures after the physics step;

mutate native worlds only at a safe server tick boundary under the existing

serialization. Reserve a destination exit volume before committing. Simultaneous

arrivals are serialized by transfer order and non-overlapping exit reservations.



Persist a transit journal and ownership change in one SQLite transaction. On a

crash, recover the committed destination owner or return an uncommitted entity to

its safe source state; never reload one copy in each world. Client acknowledgments

only establish readiness, not authoritative ownership or destination selection.



### 4. Destination readiness and streaming



Reuse the outgoing-only preload and its gate-region meshes, immutable assets and

managed collision inputs. Validate destination terrain hash, building revision,

collision owners and current dynamic baseline again before activation. Client

preload completion still does not delay opening the gate itself.



Transfer readiness includes the arrival footprint plus short swept motion margin,

canonical terrain/water, intersecting buildings, collision and required scene

uploads. Expand beyond 3 x 3 chunks only when the transformed arrival demands it.

Avoid the current F4 path's preparation of every distant saved prop region: load

only arrival-region actors first, retaining the rest as frozen persistent records.



There is one speculative destination and no second running prediction world.

Give readiness work bounded priority while the traveler is white without blocking

other sessions or unloading their source scene. Keep the existing CPU/GPU cache

caps and upload budgets. Revalidate reservations immediately before release.



### 5. Client transition, prediction and momentum



A local predicted crossing may start the white fade immediately, tagged with its

input sequence. The server confirms or rejects capture; rejection fades back to

the authoritative source without switching locations. Do not independently move

the player to a client-selected world.



Once fully white and prepared, acknowledge the matching transfer/revisions. Apply

one authoritative destination baseline, reset old rollback/interpolation buffers,

clock anchors, contact histories and temporal graphics histories, and establish a

fresh prediction epoch. Ignore stale source snapshots, commands and duplicate

commit messages. Reveal only after a complete destination frame has been rendered

with the matching state. UI and input state must not leak across the transition.



The current `PhysicsWorld.Step` replaces horizontal velocity from movement input.

Add a shared predicted arrival-momentum term: initialize it from transformed

capture velocity minus normal locomotion velocity; decay it through bounded

acceleration/friction, not an immediate overwrite. Preserve the complete initial

velocity on release, then allow ordinary gravity/collision/input forces. Include

this state in snapshots, rollback and compatibility hashes. Transform look yaw

with the gate frame; preserve pitch for the supported upright gates.



### 6. Failure and closure policy



- Stop new captures immediately when closure begins. Captured transfers retain

  the connection until release/return; use a 30-second preparation deadline.

- Recheck obstructed exits for up to 5 seconds within that deadline. Keep the

  transformed tangential offset; do not search for a distant arbitrary spawn.

- On failure, return to the reserved safe source pose. Check source clearance,

  then a small front-side recovery region. A designated source spawn is the last

  recovery option, with an explicit diagnostic, never a silent destination warp.

- While partially entered, closure first restores a safe fully front-side pose

  before removing clipping/proxy state. Do not reveal a body inside rear terrain.

- A disconnect cancels an uncommitted traveler. A committed traveler remains

  destination-owned. Reconnect/restart reads the journal; neither a lost ACK nor

  a repeated message duplicates an entity or leaves a permanent white screen.

- Re-entry prevention uses direction and separation state, not only a cooldown.

  Entity traversal never turns an incoming connection into an outgoing route.



### 7. Contracts, delivery and validation



Advance the current working protocol 12 to 13 (or the next free version at

implementation), update movement/physics hashes and use bounded transit messages:

prepare, ready, commit, applied and abort. Scope each by transfer/location/epoch;

retain 64 KiB frame bounds and bulk backpressure. Add F3 transit phase, readiness

bits, wait time, cache reuse, exit occupancy and rollback reason. Keep detailed

errors in diagnostics rather than restoring the removed crosshair reason text.



Deliver in steps: shared geometry/identity and journal -> player capture/fade and

momentum -> preload activation and recovery -> native partial-prop prototype ->

complete prop transfer and clipping -> multiplayer validation and packaging.

Do not label the whole feature complete after only the player path works.



Required checks:



- Forward/backward entry, sprinting, jumping, aperture grazing, corrections,

  rotated endpoints, repeated crossings and entry during opening/closing.

- Exact speed/spin and gate-relative orientation at release; no next-tick

  horizontal momentum loss. Capsule/box/barrel clearance and water arrivals.

- Long/rotated props, withdrawal, a rear wall, pushing, sleeping props, fast

  crossings and multiple entities. No early centre-only capture or duplicates.

- Warm/cold/stale/capped destination caches, edited arrival buildings, blocked

  exits, closure, timeout, lost readiness ACK and cancellation during loading.

- Two clients with 100 ms RTT plus jitter/loss: only the traveler fades; source

  and receiver observers agree; incoming observers receive no source terrain.

- Crash/restart at each journal stage, reconnect in transit, duplicate commits,

  delayed old-world packets, saved ownership and restored dynamic prop counts.

- Live captures of entry, full-white wait, arrival and failed return. Verify

  gate/audio timing, address-book visit updates, physics and UI resource cleanup.

- Release client/server, existing movement/physics/persistence/streaming suites,

  Windows packages and documentation. Measure 1080p frame p95, transition upload

  spikes, input reconciliation, cold/warm travel wait, bytes and memory. Retain

  the 8.33 ms target and report failures rather than claiming preload guarantees

  it. No broad renderer or physics optimization is part of this travel work.


## Physical carrying and item arrival (2026-11-09)

E grabs a visible crate or barrel within 2.5 m, or drops the held item. Left click
throws while carrying and takes priority over weapon fire and DHD input. Doors
and props compete by visible distance outside build mode. Menus and building
retain priority; entering build mode releases the item. The weapon lowers and
contextual hints identify pickup, drop and throw.

Held items remain dynamic and collidable. Fixed-step damped force and torque
control use a 4 Hz position response, 2 Hz orientation response and bounded
acceleration, with shape sweeps toward the target. Excessive tracking error,
disconnect, player transit or full horizon capture releases the hold. Throw adds up to 6 m/s
along the view direction and caps resulting speed at 12 m/s. Sequenced actions
and hold baselines participate in prediction replay. Holds are not saved.

Transfers log persistent item/connection IDs, capture, ownership commit, release,
physics epochs and explicit recovery reasons. A receiving exit first tries the
mapped pose, then nearest-first half-metre offsets within 3 m, entirely in front
of the gate. Successful candidates are reserved during ownership persistence;
other arrivals cannot choose overlapping exits. Occupied exits retry for ten
seconds within the existing thirty-second readiness limit. Nearby source players
see waiting/return reasons. Safe returns remain enabled for genuine failures.

Body autosaves now update poses only against the current durable ownership
registry, matching both persistent and local IDs. An old source snapshot cannot
reintroduce a transferred item; an old destination snapshot cannot remove an
arrival. This needs no save schema migration. Protocol compatibility is 17.

Validation includes stale source/destination saves, blocked exits, delayed
readiness, real player-capsule pushing through a network gate, two-client pickup
with latency, late joins, exclusive grabs, throws, disconnect release and
prediction corrections. Logs and recordings are under
`artifacts/interaction-pickup`; see `validation.json` for measured results and
remaining visual coverage. The cold readiness run took 17.802 seconds while
preserving exactly one captured item; this work does not eliminate cold planet
generation delays.


## Partial entry and opening hazards (2026-11-09)

A held prop stays held while partially inserted; withdrawal and E-drop remain
available. Full capture releases its holder once and follows ordinary transfer
or wrong-way destruction. Clipping and carry sweeps use only the visible hull.
A prop supported at entry retains a private unilateral floor constraint at its
entry elevation until withdrawal or capture. This state is replicated for
prediction and replay. Airborne props receive no floor constraint.

Receiving gates stay fixed. The earlier full-hull placement search and airborne
exit impulse are superseded by progressive emergence below. Reservations and
pre-commit revalidation remain mandatory. Fully blocked exits retain the
existing retry, timeout and explained return behavior.

Each opening endpoint evaluates its own liquid plume using the shared timeline
and shape evaluator. A closed reduced mesh with a refitted BVH tests physical
prop, scenery and building geometry, including swept prop poses. Rear-only
objects, players, gates, DHDs and terrain are unaffected; mist and spray do not
cause destruction. A hit removes the whole identified object. Unsupported
building dependents are removed repeatedly and empty structures are pruned.
Removals are committed in one SQLite world-revision transaction before clients
receive the authoritative revision. Failed writes retain the queued operation
for retry; stale body saves cannot restore removed props. No debris or refunds
are generated. Physics content hashes and protocol 17 require matching builds.

Focused checks, isolated runs and comparison artifacts for these changes are
under `artifacts/gate-physics-fixes`. The validation report distinguishes measured
checks from the remaining full acceptance matrix.


### Arrival fade readiness (2026-11-09)

After authoritative arrival, begin the 150 ms fade back as soon as the receiving
region has rendered with its resident terrain and drawable scenery fallbacks.
Higher-detail tree variants and unrelated queued building models do not extend
the white hold. Invalid chunks beyond world bounds are excluded from the check.
The shared minimum two-second transit delay for players and props is unchanged;
there is no additional timed hold after arrival. Server collision and blocked-exit
readiness checks still apply before release.

## Progressive prop emergence (2026-11-10)

Protocol 19 carries receiving clip mode and connection identity in snapshots and
rollback state. After the existing minimum two-second transit and readiness wait,
crates and barrels begin behind the receiving membrane with their leading 2 cm
exposed. Gate-relative orientation, tangential offset, linear velocity and spin
are retained. There is no position search, exit kick, automatic advance or
receiving floor constraint. Player travel and white fades are unchanged.

The visible half-space drives color, shadows, picking, collision and water sample
coverage. The clipped convex collider retains the original mass, inertia and
center-of-mass reference. Hidden geometry does not collide with rear walls.
Exposed portions can be grabbed. A prop may stop partially emerged; moving its
entire hull behind the receiver destroys it. Once its trailing extent clears by
2 cm, it becomes an ordinary destination prop.

Normal/manual closure waits for unfinished emergence. At 38 minutes from source
Open, unfinished receiving props are durably destroyed and closure begins. A
source insertion is recovered if possible, otherwise durably removed at this
hard limit. Already captured entities may continue safe recovery after closure;
blocked source recovery cannot indefinitely extend the connection. Failed database
writes remain pending for retry rather than allowing duplicate ownership.

The destination owns an emerging prop in a journaled transaction. Completion
commits its ordinary destination pose; destruction removes ownership atomically.
Restart destroys unfinished emergence because gates restart idle, while completed
arrivals remain. Older saves and terrain need no regeneration.
