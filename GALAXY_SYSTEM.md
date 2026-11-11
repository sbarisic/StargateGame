# Galaxy system design

Date: 2026-11-06

Status, 2026-11-07: [DHD_DIALING.md](DHD_DIALING.md) describes the implemented
connection prototype. It adopts `rda-compatible-fit-20261106-v1` and provisions
19,020,803 distinct locations with an inclusive **1-light-year** acquisition radius.
The design below retains future travel/discovery scope; older unresolved catalogue
choices are superseded by the adopted version. Physical travel remains deferred.

This document defines the galaxy, persistent destinations, and loading flow.
[STARGATE_ADDRESSING.md](STARGATE_ADDRESSING.md) owns the address mathematics and
reference catalogue. [STARGATE_TRAVEL.md](STARGATE_TRAVEL.md) owns physical
transfer and the player transition. [TERRAIN.md](TERRAIN.md) describes the current
terrain implementation used when galaxy locations are materialized.

The requirement to support supplied franchise addresses is developed in
[STARGATE_KNOWN_ADDRESSES.md](STARGATE_KNOWN_ADDRESSES.md), with a tested candidate
reference catalogue, explicit handling of missing glyphs, and an Othala routing
proposal. The implementation adopts the candidate listed above; historical counts
and coordinates below describe the original design baseline.

## Core model

A six-symbol destination describes a nominal position through three intersecting
planes. The network applies known coordinate corrections, searches within
1 light-year of that corrected position, resolves a persistent destination, and
locks onto its receiving gate. Terrain is generated or loaded when needed.

```text
Entered symbols + departure context
  -> canonical three-pair address
  -> nominal galactic coordinates
  -> drift-corrected target
  -> destination and eligible receiver within 1 light-year
  -> reserved gate endpoint
  -> destination terrain, collision, and saved state ready
  -> connection ready for travel
```

The galaxy exists as lightweight records before its planets are visited. Dialing
materializes a location's playable world; it does not invent a new location each
time. A destination retains its identity and saved changes when unloaded.

## Galaxy and coordinate frames

The initial hierarchy is:

```text
Universe
  Galaxy
    Star system
      Stars, planets, and moons
        Gate-bearing world / persistent location
          Physical gates and local gameplay entities
```

Start with one galaxy and one primary gate-bearing planet per provisioned
destination. Additional bodies can initially be descriptive data. Several
addresses may refer to that same destination. Multiple gate worlds in a system
can be added later without treating a star system, planet, and gate as one ID.

| Frame | Units and convention | Use |
| --- | --- | --- |
| Galactic | Double-precision light-years; X/Y disk, Z vertical | Navigation, drift, spatial queries, map |
| Star system | Relative astronomical coordinates, such as AU | Stars and orbital body descriptions |
| Playable world | Metres, local Y-up | Terrain, rendering, characters, Jolt physics |
| Gate | Local position and orientation within its playable world | Exact entry and exit transforms |

Do not pass galaxy-scale positions into local physics. Store a galactic base
position and local offsets separately. A planet's orbit and a gate's placement
contribute to its current galactic receiver position, but local transfer uses
the precise metre-scale gate transform.

The experimental galactic volume is an ellipsoid with semi-axes
`(50,000, 50,000, 8,000)` light-years:

```text
(x / 50000)^2 + (y / 50000)^2 + (z / 8000)^2 <= 1
```

The addressing appendix uses kilolight-years: convert its coordinates by
multiplying by 1,000. The acquisition radius is `1 ly = 0.001 kly`.
These dimensions are game parameters, not a measured Milky Way model.

## Mapping addresses to positions

Use 38 fixed, versioned reference points. These are navigational anchors, not
necessarily stars, planets, or gate locations. Six distinct destination symbols
form adjacent pairs: `AB / CD / EF`.

For each pair with reference points `a` and `b`, construct its perpendicular
bisector plane:

```text
n = (b - a) / length(b - a)
d = (dot(b, b) - dot(a, a)) / (2 * length(b - a))
dot(n, nominalPosition) = d
```

Solve the three equations in double precision. Reject invalid symbols, repeats,
non-finite results, degenerate or insufficiently stable intersections, and
nominal positions outside the configured galaxy. The experimental stability
rule is `abs(det(N)) >= 0.025` for unit-normal rows. It is a heuristic; production
solver residual and stability checks still need specification.

Reverse either member of a pair or reorder whole pairs without changing the
address. Canonicalize by sorting within each pair, then sorting the three pairs.
Include the galaxy and catalogue version in the key. Do not globally sort the
six symbols before forming pairs.

Each canonical pairing has 48 equivalent entered sequences. A fully unordered
set of six symbols has 15 different pairings, potentially selecting different
targets. Ordinary dialing must preserve the chosen pairing.

Coordinates come from these equations, not a hash. Hashing is suitable for
stable content seeds after destination identity has been resolved.

## Acquisition and stellar drift

Keep four separate quantities:

| Quantity | Definition |
| --- | --- |
| NominalPosition | Immutable intersection for a versioned canonical address |
| CorrectedTarget | NominalPosition plus the known coordinate correction |
| AcquisitionRadius | **1 light-year**, spherical and inclusive |
| ReceiverPosition | Receiving gate's current galactic position |

```text
correctedTarget = nominalPosition + correctionAtKnownRevision
eligible = distance(receiverPosition, correctedTarget) <= 1 light-year
```

The radius is measured from the corrected target, not the departure gate or the
uncorrected intersection. It is not a diameter, sector size, minimum star-system
separation, or floating-point epsilon.

Corrections move the expected target as a destination drifts. Keep their epoch
or revision separate from the immutable reference catalogue. Do not move all
38 anchors to update one system, or keep expanding the radius to compensate
for stale information. Stale corrections can cause acquisition to fail.

For example, an uncorrected displacement of 1.2 ly is outside the radius. If a
known correction accounts for 0.8 ly along that displacement, the remaining
0.4 ly is inside it. Acquiring that receiver still requires the destination and
gate-state rules below to pass.

A system keeps its LocationId and content as it drifts. Moving gate hardware
around its planet changes the endpoint without creating a new planet. Moving
hardware to another system requires registration there; an ordinary address
does not follow a GateId anywhere in the galaxy.

The original nominal-position boundary check remains in force. A radius that
overlaps the galaxy does not rescue a nominal target rejected by that check.

### Destination selection and aliases

1. Prefer an established address-to-location association if that location has
   an eligible receiver within the acquisition region.
2. An established association that cannot acquire its destination fails. It must
   not silently redirect to a neighbouring destination.
3. For an unassociated address, one eligible destination can be resolved;
   several unrelated eligible destinations produce an ambiguous lock.
4. After resolving a destination, choose among its eligible gates using a
   separate deterministic receiver-priority policy.
5. No eligible receiver means failure, even if the geometry is valid. Distinguish
   this from an unloaded world, whose receiver record still exists.

Do not choose destinations by entity iteration order, discovery order, or an
arbitrary nearest-neighbour tie-break. Persist any new association through the
authoritative registry, with concurrent attempts resolving consistently.

Different canonical pairings may be aliases for one LocationId. Their nominal
positions and correction records may differ, while all 48 equivalent sequences
of one pairing share the same metadata. Thus a destination may have more than
48 usable dial strings.

Overlapping acquisition spheres do not merge physical systems or save files.
In particular, do not merge chains of overlapping spheres into one destination.
Numerical coincidence handling and intentional alias assignment are separate
catalogue policies; their production algorithm remains to be chosen.

### Basis for the radius

The selected 1 ly is a game design value, not a numerical limit established by
Stargate canon. A static study sampled 100,000 nominal pairing positions without
replacement, using random seed 20261106, and queried their nearest other
position against all 12,931,523 geometrically valid entries.

| Radius | Sample with another nominal position inside that radius |
| --- | ---: |
| 0.1 ly | 0.261% |
| 0.5 ly | 1.279% |
| **1 ly** | **2.524%** |
| 5 ly | 11.860% |

At 1 ly, 4.941% of sampled positions had another centre within 2 ly, so their
equal-radius spheres overlapped. Sphere overlap is different from a receiver
being inside both spheres. These are static nominal-position statistics, before
deduplication, aliases, corrections, and receiver placement; they are not
measured ambiguity rates or counts of unique planets.

## Persistent identities and records

The following are conceptual records, not a finalized serialization format:

| Record | Required responsibilities |
| --- | --- |
| Universe | UniverseId, UniverseSeed, retained generation and catalogue versions |
| Galaxy | GalaxyId, coordinate frame, reference catalogue, bounds, geometric rules |
| Address | Canonical key, nominal position, optional destination association, correction revision |
| Star system | Stable SystemId, base position and motion/epoch data, body descriptors |
| Location | Stable LocationId, SystemId/body association, world seed/settings, saved-state reference |
| Gate | Stable GateId, LocationId, exact local transform, receiver position, availability and priority data |
| Connection | Source/destination IDs, reserved receiver, resolved revision, lifecycle state |

Neither exact floating-point coordinates nor the raw dial string are persistent
world identity. Replacing a gate does not replace its planet. Distinct locations
must not share saved state merely because their coordinates are close.

For a given galaxy catalogue and provisioning version, retain the same spatial
layout across universe seeds. Use UniverseSeed and stable LocationId to derive
world content; include a body identifier when generating additional bodies.
Retain the generator version and settings needed to reproduce each world.

Apply saved changes after generating the retained baseline. Never regenerate
a missing or destroyed receiver as a side effect of dialing, or reroll terrain
when a connection fails. Catalogue and generator changes need explicit version
retention or migration rather than silently relocating existing destinations.

## Catalogue construction and spatial lookup

Prepare the galaxy's metadata independently of terrain generation:

1. Enumerate canonical pairings and solve/filter their nominal coordinates.
2. Apply a deterministic, versioned provisioning policy to establish distinct
   physical locations and explicit address associations/aliases.
3. Give each provisioned destination its initial primary gate-bearing planet
   and receiver record. Start without an additional random gate-occupancy filter.
4. Build a compact address index and a spatial index partitioned into sectors.
5. Store procedural world descriptors; do not generate millions of terrain worlds.

Provisioning must be independent of the order in which players dial addresses.
Do not create a planet at every nominal point and later merge its saved state
because a nearby address was discovered. The precise alias and minimum-spacing
policy is an open implementation decision, not an implicit radius-based merge.

The initial census has 41,410,215 canonical pairings, of which 12,931,523 pass
the experimental geometry and volume filters (31.2278577%). This is the count
of usable nominal address definitions, not a promised number of unique systems
or available gates. Acquisition tolerance alone does not establish a revised
planet count.

Sector size is a storage/performance choice independent of the 1 ly radius.
Queries must visit every sector intersecting the search sphere, including across
sector boundaries. Index receiver positions for acquisition; the nominal-address
index alone cannot find a receiver that moved. Update or overlay the receiver
index when positions change. Descriptors remain queryable when physics worlds
are unloaded.

Solving an entered address needs only three equations. Reverse lookup of nearby
addressable locations uses the precomputed catalogue/index; arbitrary XYZ
coordinates do not automatically have an exact six-symbol representation.

## On-demand world loading and travel

The authoritative server resolves the address, reserves the selected receiving
gate, and loads the resolved LocationId. Concurrent requests share one loading
operation and one authoritative world instance. Loading may begin during dialing
once the destination is known.

Generate or load the gate region, terrain and collision, restore saved changes,
and ensure the receiving gate and safe arrival area are ready before destination
release. A validated, reserved connection may admit source entry into pending
transit while the receiving gate finishes its incoming animation. Hold captured
state until arrival readiness; do not simulate the traveler invisibly in either
world during the wait. A generation failure or timeout fails the connection; it does not select
another planet. Reserve/pin both endpoints and their required regions while
the connection or a transfer needs them.

Resolution selects the endpoint. Transport then uses that gate's exact local
transform in metres, not the nominal or corrected galactic point. Do not rerun
an unconstrained nearest-receiver search midway through a transfer. Validate
the reserved endpoint and use the travel failure/rollback rules if it becomes
unavailable.

Empty worlds may save and unload when no player, connection, or transfer needs
them. Their identities, gate availability, corrections, and saved-state records
remain in the registry.

Player travel quickly fades to white, holds white for roughly 2-3 seconds or
however long the receiving gate's incoming animation, wormhole formation, and
destination readiness require, then fades from white after teleportation. There
is no wormhole-flight animation in the traveling player's view. The receiving
gate still visibly opens in its own world. Inanimate objects transfer after full entry,
preserving motion relative to the gate frames. The detailed capture and transfer
rules remain in [STARGATE_TRAVEL.md](STARGATE_TRAVEL.md).

The current developer planet-switching interface is a terrain test facility;
its session-wide switches and spawn resets are not the gate-travel contract.

## Galaxy map, authored locations, and extensions

The galaxy map uses the same coordinates as navigation. Show discovered system
records, known addresses, names, notes, distances, and known gate status. Keeping
the full catalogue on the server does not reveal every destination to players.

Spiral arms and other background art must not silently warp the solved points.
A physically constrained spiral distribution would require further catalogue
design or explicit filtering, with a new census and version.

For an authored destination with a supplied franchise address, preserve that
address and use a compatible versioned reference catalogue. Place the authored
location consistently with its solved target and acquisition region. The known
address study supplies a concrete catalogue proposal for Earth, Abydos, and the
other listed routes. For a new fictional destination without a supplied address,
choose a valid address first. Do not promise an exact address for an arbitrary
chosen coordinate. Authored worlds use the same identity, correction, and
receiver-selection rules.

The point of origin identifies the departure context; it does not contribute
another destination plane or change the destination's content seed. Every ring
uses the same shared origin artwork, resolved as LOCAL_ORIGIN.
The [ring mapping](STARGATE_RING_MAPPING.md) records its slot and rotation frame.
Manual ring dialing locks the final origin symbol before activation; passing it while spinning
does not activate the gate. DHD/computer activation is a separate logical action.
The [manual-dialing design](STARGATE_MANUAL_DIALING.md) makes completion of the
final origin lock request activation. A standard DHD uses an explicit origin key
followed by its centre button, and cannot enter glyph 13 or eight-chevron routes.
An expanded controller exposes all destination glyphs and can combine origin
and activation in one centre press. Input-device limits do not change the galaxy
resolver or remove the need for source power and an available receiving gate.

Keep GalaxyId and catalogue versions in records from the start. Eight-chevron
intergalactic routing and special nine-chevron endpoints remain separate future
resolvers, not extra terms appended to the three-plane equation.

## Remaining decisions and implementation checks

Still to specify before production implementation:

- Final reference catalogue, galaxy dimensions, and numerical stability criteria.
- Deterministic provisioning, numerical coincidence handling, alias assignment,
  and whether distinct systems need minimum physical separation.
- Correction distribution, revision/epoch format, simulation of stellar motion,
  and stale-data limits. The acquisition radius itself is settled at 1 ly.
- Receiver priority for multiple eligible gates at one destination.
- Sector layout, compact index format, save schema, migration, and loading budgets.

Implementation must verify that equivalent sequences resolve identically;
aliases share saved state; distinct nearby systems remain distinct; drifting
locations retain identity; receivers inside/on/outside the 1 ly boundary behave
consistently; sector boundaries do not hide candidates; stale/ambiguous/missing
receivers fail deterministically; and concurrent dials cannot duplicate worlds.

## Lore basis

The [Stargate address](https://stargate.fandom.com/wiki/Stargate_address) and
[correlative update system](https://stargate.fandom.com/wiki/Correlative_update_system)
articles support location addressing with updates for stellar drift. The
[Avenger 2.0 transcript](https://stargate.fandom.com/wiki/Avenger_2.0/Transcript)
describes coordinate updates associated with addresses. These support the broad
navigation model; the bisector mathematics, 1 ly radius, identity records, and
ambiguity policy are this game's design choices. CAP's lookup behavior is
discussed separately in the addressing document.

## Home time policy (2026-11-08)

The home association receives a persisted optional environment `FixedHour=11`.
The server applies it before constructing the initial physics/world definition,
and galaxy adoption applies it to retained homes. The shared environment phase
query honors it on all clients. Other locations keep advancing from server UTC;
no terrain regeneration or schema migration is required.
