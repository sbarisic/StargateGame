# Manual Stargate dialing

Date: 2026-11-06. Status: gameplay design, not implemented.

Update 2026-11-07: physical DHD dialing and connections are implemented in
[DHD_DIALING.md](DHD_DIALING.md). Manual ring controls and travel remain deferred.

This document defines spinning the ring, locking symbols, and activating after
the origin lock. It uses the inspected [ring mapping](STARGATE_RING_MAPPING.md),
the [address rules](STARGATE_ADDRESSING.md), and the
[known-address catalogue](STARGATE_KNOWN_ADDRESSES.md).

## Design decision

Manual dialing exposes all 38 destination symbols and the shared LOCAL_ORIGIN
on the ring. Every catalogue record has a manual input path in the proposed
compatibility preset. A standard DHD need not expose every such path.

This is an input capability, not a promise that any gate can connect anywhere:
the compatible reference catalogue and known-world registrations must be adopted,
the three incomplete source addresses need their documented game completions,
and Othala needs its intergalactic route, eight-chevron capability, and power.
A missing, destroyed, busy, or out-of-range receiver can still prevent connection.

For the standard DHD, retain the physical origin key in the slot shared by meshes
01 and 13, and hide key 13. It therefore has 37 destination keys plus origin.
The centre dome activates after the origin key. This deliberately leaves Aquila
(RDA 13) unavailable on that controller. An optional expanded controller can
instead expose all 38 destination glyphs and finalize origin through its centre.

This revises the earlier recommendation to make every destination glyph available
on the default DHD. It uses the user's acceptance that some routes are manual-only.
It does not remove glyph 13 from the gate, alphabet, or galaxy coordinate system.

## Player sequence

1. Interact with the gate's manual-dial control area and take control of the ring.
   Select ordinary seven-chevron mode, or supported eight-chevron mode before
   entering a route. Show the expected number of symbols and existing progress.
2. Push/drag the ring clockwise or counterclockwise. Releasing input lets it slow;
   braking/fine adjustment lets the player align a symbol accurately.
3. Bring the wanted glyph to the fixed top read position. Stop the ring, then use
   the explicit **Lock symbol** action. Passing a glyph never submits it.
4. On acceptance, hold the ring briefly at the aligned position, animate the
   locking mechanism, play confirmation, and record that glyph once. Release
   the mechanism so the player can spin toward the next symbol.
5. Repeat for all six destination symbols, or all seven routing symbols in
   eight-chevron mode. Preserve the entered order for the dial animation.
6. Rotate the same origin glyph to the top and explicitly lock it.
7. After that origin lock completes, the gate requests activation automatically.
   It checks the route and connection requirements, prepares the destination,
   and opens only when ready. No DHD or additional centre-button press is needed.

Locking should be available through the manual control interaction; players
should not have to climb to the top chevron for every symbol. Exact bindings and
the physical control-area presentation remain implementation choices. Controller
and keyboard inputs should provide the same spin, brake, lock, and reset actions.

## Ring movement and alignment

Track an unwrapped clockwise angle and angular velocity. Input adds angular
acceleration, release applies drag, and braking removes speed. Fine adjustment
reduces the input rate. Ring mass/inertia, speed, and friction are gameplay tuning
parameters; the visual ring need not be simulated as a freely colliding rigid body.

Use the verified local pivot `(0, 3.064, 0)` metres and rotate both the glyph
track and separate origin mesh together. The surrounding gate and chevrons do
not rotate with them. In the front view, slot 00 starts at the top and slots
increase clockwise in steps of `360 / 39` degrees.

```text
pitch = 360 / 39
slot = Mod39(round(-thetaCW / pitch))
alignmentError = Wrap180(slot * pitch + thetaCW)

canLock = abs(alignmentError) <= lockToleranceDeg
       && abs(angularVelocity) <= maximumLockSpeed
       && dialingState accepts a symbol
```

The server must check both alignment and speed. Do not catch a rapidly passing
symbol just because a lock packet arrives at the right instant. Lock tolerance
must be less than half a slot (4.615384615 degrees). A starting playtest proposal
is 0.5 degrees and 2 degrees/second maximum lock speed; neither value is final.

An accepted lock may settle the ring through the remaining small alignment error
to the exact slot centre. It must not snap from an unrelated angle or silently
choose a different glyph. The lock animation freezes the captured slot and
ignores additional lock presses until it finishes.

The existing rotation convention remains `RotZ(-thetaCW)` about the local pivot,
then the gate's normal scale and world transform. Extra turns and direction
reversals do not change the selected symbol. Do not require alternating rotation
directions or a fixed number of revolutions in the initial design.

## Locking destination symbols

For destination slots 01-38:

```text
RdaGlyphId = slot + 1
ReferenceId = slot
```

Keep the raw entered RDA sequence until the route's destination portion has been
collected. For ordinary dialing, reject repeated destination symbols and accept
exactly six. Convert to reference IDs and form adjacent pairs before canonicalizing.
Never globally sort the six glyphs or substitute a nearest valid address.

Eight-chevron input collects seven routing symbols. Its route-specific validator
owns repetition/order rules; do not turn it into three pairs plus an ignored extra
glyph. Othala initially requires the exact supported seven-symbol sequence.

Each accepted lock updates progress immediately. Report failures clearly, such
as “Ring still moving,” “Align a symbol,” “Symbol already entered,” or “Lock origin
to finish.” A rejected lock leaves the entered sequence unchanged.

## Origin lock and activation

Ring slot 00 always emits LOCAL_ORIGIN. Reuse the same artwork on every planet
and resolve its meaning from the departure gate's identity. It is neither Earth's
destination nor an additional reference point.

Origin is accepted only after the selected mode's complete destination/routing
sequence. An early origin lock is rejected without activating or clearing the dial.
After the required count, another destination-symbol lock is also rejected; the
player must lock origin or reset the address.

Origin locking and activation are distinct ordered events performed by the same
manual action:

```text
Origin alignment and speed accepted
  -> origin captured and final lock animation completes
  -> complete dial is frozen
  -> ActivationRequested
  -> route resolved and source power/capability checked
  -> receiver acquired and reserved
  -> source horizon opens when the connection can safely accept pending transit
  -> incoming gate animates and forms its receiving horizon
  -> destination terrain, collision, and saved state ready
  -> pending travelers may emerge at the destination
```

Geometry/metadata resolution and destination preloading may start earlier once
enough symbols are known, but must not make the gate traversable before the final
origin lock and successful activation. Use the same correction records and
1-light-year acquisition rule as DHD/computer dialing. Manual input does not
bypass stellar drift, power requirements, or receiver priority.

On failure, show the reason and release any provisional reservations. Keep the
attempted sequence visible for diagnosis, but require an explicit reset/retry;
do not open later by itself when power or receiver status changes. A cancelled
or expired activation attempt cannot open on a late loading callback.

Source entry and destination emergence have separate readiness checks. Player
entry fades to white and waits roughly 2-3 seconds, or however long the incoming
gate animation, wormhole formation, and destination preparation need. Teleport
and fade back only when arrival is ready. The [capture and fade rules](STARGATE_TRAVEL.md)
preserve captured motion during this pending transit, with no wormhole-flight
sequence in the player's view.

## Example: dial Abydos manually

The catalogue supplies `27-7-15-32-12-30`, followed by local origin. The table
gives absolute clockwise ring orientations from the neutral pose, not relative
movement amounts from the previous step:

| Lock | Input | Ring slot | Target ring orientation CW |
| --- | --- | --- | --- |
| 1 | 27 — Taurus | 26 | 120.000000 degrees |
| 2 | 7 — Serpens Caput | 06 | 304.615385 degrees |
| 3 | 15 — Capricorn | 14 | 230.769231 degrees |
| 4 | 32 — Monoceros | 31 | 73.846154 degrees |
| 5 | 12 — Sagittarius | 11 | 258.461538 degrees |
| 6 | 30 — Orion | 29 | 92.307692 degrees |
| 7 | LOCAL_ORIGIN | 00 | 0.000000 degrees |

Spin, stop, and lock at each step. The final origin lock requests activation.
The same glyph artwork is used when dialing away from Abydos; its departure
identity changes, while the symbol-to-slot mapping stays the same.

## Catalogue coverage by input device

| Catalogue group | Manual ring | Standard DHD | Expanded controller |
| --- | --- | --- | --- |
| 33 ordinary entries without glyph 13 | Supported | Supported | Supported |
| P2X-555: 28-8-16-33-13-31 | Supported | Missing glyph 13 | Supported |
| P3X-116: 21-13-26-17-30-35 (proposed completion) | Supported | Missing glyph 13 | Supported |
| Othala: 11-27-23-16-33-3-9 + origin | Supported with eight-chevron capability and power | Unsupported route length | Requires intergalactic upgrade and power |

These are the 36 source records, not necessarily 36 verified canon worlds with
currently functioning gates. The 33-entry group includes the proposed completions
for Land of Light and P3X-984. P3X-116's last two symbols are also game-authored.
An unknown `?` cannot be locked on a ring or entered on a DHD.

Standard DHD controls: enter six available destination glyphs, press the explicit
origin key, then press the centre dome to activate. The origin key's appearance
may be a generic mark; its semantic action is the same LOCAL_ORIGIN as the ring.
Do not render or enable overlapping keys 01 and 13 at once.

Expanded controller profile: expose keys 02-39, hide the overlapping key 01,
and let the centre action append LOCAL_ORIGIN before activation. An additional
intergalactic capability permits seven routing symbols. The earlier expanded
layout proposal remains useful as an upgrade, not the standard DHD default.

Neither pair reordering nor reversing pairs can avoid a missing DHD glyph: all
48 equivalent ordinary sequences still contain the same six symbols. No arbitrary
destination blacklist is needed. Capability differences follow the device's
available symbols and supported route lengths.

## Session ownership, reset, and interruptions

One authoritative dial session owns a gate at a time. Manual controls, DHD,
and dialing computer cannot append symbols concurrently. Switching devices
requires cancelling/resetting the current attempt in the initial design.

The server owns ring motion, accepted slots, input progress, origin context,
activation revision, and connection state. Clients send spin/brake/lock/reset
intent. Do not trust a client-supplied symbol when its ring angle disagrees.
Use input sequence numbers to prevent duplicated locks and tie each lock to
the authoritative angle/speed at its accepted simulation step.

Reset clears the complete entered sequence and lock-progress indicators and
brakes the ring; it need not rotate the ring back to zero. First-release manual
controls reset the whole address rather than undoing individual locks. Ordinary
release of spin input only slows the ring; it does not cancel the dial.

Explicitly abandoning the session, owner disconnect, or an idle-session timeout
cancels it and releases control. The timeout is still a tuning decision. An
opening/open/closing gate rejects new dialing input. Incoming connections need
an explicit arbitration policy; never mix incoming state with a partly entered
outgoing address.

## Implementation checks

Verify all 39 slots at neutral, positive, negative, and multi-turn angles; the
alignment/speed limits; exactly one recorded glyph per lock; no entry while
spinning past symbols; early-origin and excessive-symbol rejection; final-lock
ordering; and power/load failures without delayed unintended activation.

Exercise every supported catalogue sequence with its chosen completion policy,
all 48 equivalents of ordinary routes, the standard DHD's missing-glyph cases,
and Othala's exact eight-chevron route. Verify shared-origin behavior from at
least two departure locations and unchanged destination identity across devices.
The physical chevron animation order, exact key bindings, session timeout, and
ring-motion tuning remain implementation details to finalize.
