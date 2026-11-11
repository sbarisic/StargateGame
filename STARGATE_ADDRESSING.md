# Stargate addressing: three-pair coordinate system

Date: 2026-11-06

Implementation update, 2026-11-07: the runtime uses the adopted
`rda-compatible-fit-20261106-v1` reference set and the pair mathematics below.
See [DHD_DIALING.md](DHD_DIALING.md). The original reference table and census
in this document remain historical research; physical travel is deferred.

Status: accepted design direction, with an exploratory visualization and an
exhaustive address-count experiment. This document does not implement dialing,
planet generation, persistence, or gate travel in the game. The reference-point
catalogue and galaxy dimensions below are experimental, not final universe data.

See [GALAXY_SYSTEM.md](GALAXY_SYSTEM.md) for the galaxy registry, coordinate
frames, 1-light-year acquisition rule, and on-demand world loading design.
The supplied franchise glyphs and addresses are inventoried in
[STARGATE_GLYPHS.md](STARGATE_GLYPHS.md) and
[STARGATE_KNOWN_ADDRESSES.md](STARGATE_KNOWN_ADDRESSES.md). The latter proposes a
compatible reference catalogue; it does not replace this document's baseline.

## Design decision

Use six distinct destination symbols, grouped into three adjacent pairs. Each
symbol identifies a fixed three-dimensional reference point. Each pair defines
the plane of positions equally distant from its two reference points. The
intersection of the three planes gives the address's nominal galactic target.
The receiving gate does not have to occupy that exact point: dialing applies
known drift corrections, searches a bounded acquisition region, and locks onto
the receiver's current position.

For example:

```text
Dialed destination: A B C D E F
Pairs:              (A, B), (C, D), (E, F)
Planes:             bisector(A, B), bisector(C, D), bisector(E, F)
Nominal target:     intersection of those three planes
Actual endpoint:    receiving gate acquired near the corrected target
```

Coordinates come from geometry, not an address hash. A separate procedural seed
can determine terrain and scenery after the destination has been resolved.

The six symbols select and pair reference points. They do not directly represent
six planets, and the reference points need not contain stars or gates.

## Symbol order and equivalent addresses

Order determines the pairing, but reversing either member of a pair does not
change its plane. Reordering entire pairs does not change their intersection.

These sequences therefore identify the same nominal target and use the same
correction and receiver-selection rules:

```text
AB CD EF
BA CD EF
CD EF AB
FE BA DC
```

Changing which symbols are paired usually changes the nominal target. Different
targets can still acquire the same destination within the allowed tolerance:

```text
AB CD EF -> one intersection
AC BE DF -> another intersection
```

Each pairing has `2^3 * 3! = 48` equivalent six-symbol sequences: eight ways to
reverse pairs, multiplied by six ways to order the pairs. This is a count per
pairing. A destination can have additional address aliases when other pairings
also acquire it, even if their nominal intersections are not identical.

A set of six distinct symbols has `6! = 720` permutations. These divide into
`720 / 48 = 15` different pairings. Fully unordered six-symbol dialing is not
the selected model: it leaves up to 15 candidate intersections rather than
identifying one destination.

An optional future research feature could enumerate those candidates from an
unordered inscription. It must not silently reinterpret a failed ordinary dial.

### Canonical address key

Normalize equivalent sequences before address lookup, caching, or saving:

1. Validate that all six destination symbols are distinct and in the catalogue.
2. Split them into the three adjacent pairs.
3. Sort the two symbol IDs within each pair.
4. Sort the three pairs lexicographically.
5. Include the galaxy/reference-frame identity and catalogue version in the key.

Do not sort all six symbols before making the pairs: that would collapse distinct
destinations. Preserve the player's entered sequence separately for animation.

## Geometric calculation

Let `a` and `b` be the reference positions for a pair, and let `p` be the unknown
nominal target. The perpendicular bisector plane satisfies:

```text
|p - a|^2 = |p - b|^2
(b - a) dot p = (|b|^2 - |a|^2) / 2
```

Normalize each equation:

```text
n = (b - a) / |b - a|
d = (|b|^2 - |a|^2) / (2 * |b - a|)
n dot p = d
```

The three pairs produce a matrix with the three unit normals as its rows:

```text
N p = d
```

Solve this three-by-three system for `p = (x, y, z)`. Three independent planes
have one intersection. Dependent planes can have no intersection or infinitely
many; neither yields a unique nominal target.

For a direct calculation, with normals `n1`, `n2`, `n3` and offsets `d1`, `d2`,
`d3`:

```text
D = n1 dot (n2 cross n3)
p = (d1 * (n2 cross n3)
   + d2 * (n3 cross n1)
   + d3 * (n1 cross n2)) / D
```

Use double precision for galactic calculations. Local terrain rendering and
physics should use a separate coordinate frame in metres around the gate.
In this galactic frame, X and Y span the disk and Z is height above the disk;
this is separate from the game's local Y-up convention.

### Worked example

The following reference positions use arbitrary units:

| Pair | First point | Second point | Plane |
| --- | --- | --- | --- |
| AB | A = (0, 0, 0) | B = (4, 0, 0) | x = 2 |
| CD | C = (0, 1, 0) | D = (0, 5, 0) | y = 3 |
| EF | E = (0, 0, 2) | F = (0, 0, 6) | z = 4 |

`AB CD EF` has nominal target `(2, 3, 4)`. With the same reference points,
`AC BE DF` has nominal target `(13/6, 1/2, 4/3)`, approximately
`(2.167, 0.5, 1.333)`. Receiver acquisition happens after this calculation.

## Validity and connection rules

Keep address syntax, geometric validity, and connection availability separate.

1. **Syntax:** six different destination symbols, followed by the appropriate
   origin/routing fields for the supported dialing mode.
2. **Geometry:** three usable planes with a sufficiently stable unique
   intersection.
3. **Galaxy bounds:** the nominal intersection passes the configured galactic
   volume check. This is the rule used by the existing census.
4. **Calibration:** apply known coordinate corrections for the intended location.
5. **Acquisition:** find eligible receivers in a bounded region around the corrected
   target and resolve the destination and receiving-gate priority.
6. **Connection:** power, gate state, and other gameplay requirements permit travel.

Failure at a later stage must not generate a replacement location or choose a
new random result on the next attempt. An unloaded destination is not the same
as a missing receiving gate.

## Spatial tolerance and stellar drift

Accepted refinement, 2026-11-06: the address-to-position-to-gate lookup must allow
positional leeway. Exact coordinate coincidence and floating-point rounding are
not sufficient models for acquiring a receiver.

Keep these quantities separate:

| Quantity | Meaning |
| --- | --- |
| Nominal position | Fixed intersection calculated from the versioned reference catalogue |
| Corrected position | Expected current destination position after known drift corrections |
| Acquisition radius | Maximum remaining positional error allowed during receiver lookup |
| Receiver position | Exact current endpoint used for the connection and local gate transform |

For an initial spherical acquisition region:

```text
nominalPosition  = IntersectThreePairPlanes(canonicalAddress)
correctedTarget = nominalPosition + knownCoordinateCorrection

candidate if distance(receiverPosition, correctedTarget) <= acquisitionRadius
acquisitionRadius = 1 light-year = 0.001 kly
```

The correction represents known movement of the destination location. The radius
handles remaining uncertainty and local displacement. Updates should move the
expected target rather than compensate for unlimited drift by continually
enlarging the search radius. Stale calibration can eventually prevent a lock.

The agreed radius is **1 light-year**, measured from the drift-corrected target,
with an inclusive boundary. It is a spherical radius, not a diameter or minimum
system separation. The correction/update schedule and stale-data limits remain
open. The researched wiki pages do not establish a numerical tolerance; 1 ly is
a game design parameter. The galaxy design records the supporting radius study.

### Destination associations and multiple receivers

- Different canonical pairings can become aliases for the same destination when
  their acquisition regions reach it. Identical plane intersections are not required.
- Nearby systems remain separate physical systems. Overlapping acquisition regions
  must not merge their planets, terrain, or saved state.
- Prefer an established destination association when its receiver remains eligible
  within the acquisition region. Do not treat that association as an unlimited
  location-independent gate identifier.
- If several unrelated destinations qualify and no association resolves the choice,
  report an ambiguous lock. Do not silently pick a different system based on entity
  iteration order or whichever world happened to load first.
- After resolving the destination, apply a separate priority rule if it contains
  multiple receiving gates. The exact game priority rule remains to be specified.
- If an associated destination becomes unavailable, fail or use an explicit
  recovery/re-registration procedure; do not silently redirect to a neighbouring
  system just because it is now the closest candidate.

Address associations and correction records must be shared consistently by all
48 equivalent sequences. Distinct pairings that are aliases may need distinct
corrections because their nominal positions differ.

### Movement and persistent identity

A gate moved around its planet remains associated with the same destination;
receiver acquisition determines its current endpoint. A planetary system that
drifts retains its LocationId, terrain, and saved changes while its expected
coordinates are updated.

Moving gate hardware to another system requires registration at the new location.
Ordinary location-based addresses must not follow a particular device anywhere
in the galaxy merely because its GateId remains unchanged.

Keep correction records separate from the fixed reference catalogue. Updating
one location should not require shifting the shared reference points and thereby
moving unrelated nominal targets. Save the correction's reference epoch or
revision so stale data can be recognized; the detailed record format is undecided.

This refinement supersedes the earlier suggestion to merge only numerical
coincidences when deciding which addresses can reach the same destination.
Numerical tolerance for the plane solver is still needed, but it is separate
from gameplay acquisition tolerance and physical system identity.

### Experimental stability and boundary checks

The visualization and count experiment use unit plane normals and reject:

```text
abs(det(N)) < 0.025
```

This is a dimensionless heuristic for rejecting nearly dependent normals. It
is not a formal bound on coordinate error or a matrix condition-number test.
The threshold remains a tuning choice; production code should also validate
finite coordinates and solver residuals.

The experimental galaxy is an ellipsoid with semi-axes `(50, 50, 8)` kly,
where one kly is one thousand light-years. A stable point is inside when:

```text
(x / 50)^2 + (y / 50)^2 + (z / 8)^2 <= 1
```

These dimensions are illustrative game parameters, not a measured model of the
Milky Way. Rendered plane patches are clipped for display; the equations describe
infinite planes.

## Address capacity and measured reduction

With 38 destination symbols and no repeats:

| Quantity | Count |
| --- | ---: |
| Ordered six-symbol sequences, 38! / 32! | 1,987,690,320 |
| Equivalent sequences per pairing | 48 |
| Distinct pairings before geometric filtering | 41,410,215 |
| Unordered six-symbol sets, 38 choose 6 | 2,760,681 |
| Pairings per six-symbol set | 15 |

An exhaustive float64 census evaluated all 41,410,215 pairings using the exact
38 reference positions in the appendix, the ellipsoid above, and the normalized
determinant threshold of 0.025:

| Outcome | Pairings |
| --- | ---: |
| Stable intersection inside the galaxy | **12,931,523** |
| Stable intersection outside the galaxy | 26,901,911 |
| Rejected by the stability check | 1,576,781 |
| Total | **41,410,215** |

The experiment retains **31.2278577%** and rejects **68.7721423%** of the pairing
pool. Thus this particular layout leaves about **12.9 million geometrically
usable address definitions**.

This is not an inherent limit of the three-pair system. Changing the reference
positions, galaxy shape, or stability threshold changes the result. It also does
not count guaranteed unique planets: drift correction, acquisition regions,
address aliases, coordinate deduplication, minimum planetary separation, and
receiver availability were not applied. The **12,931,523** figure remains the
nominal geometric pairing count, not a new count of distinct systems under the
tolerance-based resolver. No revised system count has been measured.

The three six-symbol sets shown in the visualization had 11, 7, and 8 in-galaxy
pairings respectively. They were selected for demonstration and are not a
representative sample of the whole pool. The exhaustive count is the basis for
the 31.2% retention figure.

## Planet, gate, and dial-string identities

Maintain separate concepts:

| Identity | Purpose |
| --- | --- |
| Canonical address key | Represents an equivalence class of 48 input sequences |
| LocationId | Identifies a persistent place in the universe |
| GateId | Identifies a physical gate, which could be moved or replaced |
| Dial string | Records the actual symbols entered for a connection |

Resolve the canonical address to nominal geometry, apply its correction, and
acquire the location's receiving gate under the rules above. Universe provisioning
must establish locations and any address aliases deterministically; dialing must
not create a second planet simply because an address's nominal point differs from
an existing receiver position. Overlapping search regions alone do not establish
that two physical systems are the same. Do not use exact floating-point coordinate
equality as the sole identity mechanism.

Generate terrain from the resolved LocationId and universe seed, rather than
from the raw entered sequence. All 48 equivalent sequences must load the same
planet and saved changes. Changing the departure origin must not change it.

The intended loading flow is:

```text
Dial string + source context
    -> canonical address and reference frame
    -> nominal geometric intersection
    -> drift-corrected target and bounded acquisition region
    -> persistent destination association and receiving-gate selection
    -> lock onto the receiver's current endpoint
    -> load or generate terrain and collision
    -> restore saved changes
    -> permit travel
```

World generation can begin during dialing. Empty worlds can unload after saving;
their logical locations and gate records persist. Server-side resolution must
ensure that simultaneous dials to the same location share the same world.

Version the reference catalogue, geometric rules, and terrain generator. Editing
reference positions changes many intersections. A saved universe must retain
its versions or use an explicit migration; it must not silently move established
destinations after an update.

## Point of origin and longer addresses

For the initial same-galaxy seven-chevron mode, the six symbols form the three
pairs. The final point-of-origin symbol belongs to the departure context; it is
not a fourth pair and does not multiply the 48 equivalents for a given departure.

Use the same existing origin artwork on every ring. Its semantic value is always
LOCAL_ORIGIN, resolved from the departure gate; artwork does not identify the
planet. [STARGATE_RING_MAPPING.md](STARGATE_RING_MAPPING.md) records the verified
39-slot layout, with the shared origin at slot 00 in the neutral pose.

The inspected DHD has 38 distinct key positions, with key 01 overlapping key 13.
[DHD_GLYPH_MAPPING.md](DHD_GLYPH_MAPPING.md) defines a standard profile with an
explicit origin key and no glyph 13, and an expanded profile with all 38
destination keys and centre-controlled origin. The manual ring exposes every
destination glyph. [STARGATE_MANUAL_DIALING.md](STARGATE_MANUAL_DIALING.md) defines
locking and activation, including controller-specific catalogue restrictions.
These input capabilities do not change destination geometry.

Intergalactic and nine-chevron modes remain future extensions:

- An eight-chevron route should resolve a target reference frame before resolving
  the destination. Different routes to the same location must share its identity.
  The exact translation between glyph catalogues and routing modifiers is undecided.
- A nine-chevron route could use an exact fixed-sequence registry for special
  endpoints such as a moving ship. That would be a separate resolver, without
  automatically replacing its final symbol with the departure origin.

Neither extension should be implemented by blindly appending symbols to the
three-plane equation.

## Reference-point design constraints

- Use genuinely three-dimensional reference positions. A fully planar catalogue
  cannot uniquely resolve the coordinate perpendicular to that plane.
- Vary distances from the galactic centre. If all reference points lie on a sphere
  centred there, every bisector plane passes through that centre, collapsing
  independent intersections to the same location.
- Measure destination density, clustering, rejection rate, and sensitivity before
  freezing the catalogue. A high valid-address count does not guarantee a useful
  distribution of distinct worlds.
- Keep the catalogue fixed within a saved universe. The experimental values below
  are sufficient to reproduce this discussion, not evidence of an optimized layout.

## Implementation checks to retain

When implementing the feature, verify:

- All 48 equivalent orders canonicalize to one key and resolve to the same place.
- A six-symbol set produces exactly 15 canonical pairings.
- The worked example gives both expected intersections.
- Repeated, unknown, or incomplete destination symbols are rejected.
- Singular, unstable, non-finite, and out-of-galaxy results are handled explicitly.
- Origin changes do not change destination identity.
- Equivalent sequences use the same correction and acquisition rules.
- Receivers within tolerance can be acquired without exact coordinate equality.
- Valid drift updates preserve LocationId, terrain, and saved state.
- Stale corrections and out-of-tolerance receivers fail without unlimited searching.
- Overlapping acquisition regions do not merge unrelated systems or silently
  redirect established destinations.
- Destination aliases and local receiver priority behave consistently regardless
  of world-loading or entity iteration order.
- Different players and repeated visits restore the same location and state.
- Catalogue/generator version mismatches cannot silently regenerate saved worlds.

The visualization's numerical check covered 2,160 equivalent-order calculations
across its 45 displayed pairings. Maximum plane residual was about 1.42e-14 kly;
maximum coordinate difference under equivalent reordering was about 5.68e-13 kly.
Those checks validate the demonstration arithmetic, not a production networking,
save, or world-generation implementation.

## Relationship to Stargate lore and CAP

The three-pair construction is a deliberate game interpretation, not a confirmed
formula from the series. It preserves the idea of six spatial references while
allowing 48 equivalent orders. The show more generally establishes that order
matters; the 720-permutation discussion in *Rising* does not establish this
pair-equivalence rule.

The tolerance-and-correction refinement is informed by the wiki's description
of six symbols targeting an area of space, its account of correlative updates,
and the *Avenger 2.0* transcript's explanation that updates supply new coordinates
for existing addresses. The *48 Hours* transcript provides an example of a
DHD-connected gate taking precedence for incoming connections to Earth. These
support the broad model, not our exact acquisition radius, ambiguity policy, or
mathematical plane construction.

CAP's inspected base resolver checks whether the first six symbols occur in a
destination address, rather than comparing their positions. Its group suffixes
then affect routing. This design uses explicit pairs instead of CAP's six-symbol
membership matching. CAP source was inspected at revision
`8dac3ccfa9a12e1713065c31b56b627ba6405d42`; it was not live-tested in GMod here.

Research references:

- [Stargate address](https://stargate.fandom.com/wiki/Stargate_address): normal,
  intergalactic, and special addressing; uncertainty around universal nine-chevron IDs.
- [Stargate](https://stargate.fandom.com/wiki/Stargate): ordinary addresses refer
  to locations, allowing replacement gates to inherit an address.
- [Rising](https://stargate.fandom.com/wiki/Rising): six known symbols and 720
  possible orderings.
- [Dial Home Device](https://stargate.fandom.com/wiki/Dial_Home_Device): network
  updates and stellar-drift correction.
- [Correlative update system](https://stargate.fandom.com/wiki/Correlative_update_system):
  updates to gate positions within the network.
- [Avenger 2.0 transcript](https://stargate.fandom.com/wiki/Avenger_2.0/Transcript):
  updated coordinates associated with addresses.
- [48 Hours transcript](https://stargate.fandom.com/wiki/48_Hours/Transcript):
  the DHD-connected Russian gate receiving incoming wormholes to Earth.
- [CAP dialing documentation](https://github.com/RafaelDeJongh/cap/wiki/Teleportation-Systems#group-system-dialling-formula).
- [CAP base resolver](https://github.com/RafaelDeJongh/cap/blob/8dac3ccfa9a12e1713065c31b56b627ba6405d42/lua/entities/stargate_base/modules/lib.lua#L671-L799).

Fandom research used indexed article text because direct page retrieval was blocked.

## Appendix: reproducible experimental catalogue

IDs 01-38 began as experimental symbol identifiers, not franchise glyph numbers.
The later glyph study proposes mapping RDA glyphs 02-39 to these reference IDs
01-38; it does not renumber the source glyphs or change the coordinates here.
Coordinates are in kly and the decimal values below are the exact inputs used by
the original visualization and census. The separately documented compatible
candidate has different coordinates and its own census.

| Symbol | X | Y | Z |
| --- | ---: | ---: | ---: |
| 01 | 8.223 | -33.142 | -37.710 |
| 02 | -20.869 | -1.518 | -35.922 |
| 03 | 27.969 | -25.706 | -3.624 |
| 04 | -38.623 | -17.222 | -15.500 |
| 05 | -50.434 | 11.536 | 0.857 |
| 06 | -11.994 | 31.919 | 6.510 |
| 07 | 17.995 | -14.175 | 13.371 |
| 08 | 2.833 | -34.909 | 30.359 |
| 09 | -1.090 | -33.331 | -1.321 |
| 10 | 1.698 | 24.443 | 23.371 |
| 11 | -16.856 | 5.096 | -27.494 |
| 12 | -25.224 | 32.067 | 2.377 |
| 13 | -37.504 | 20.970 | -2.741 |
| 14 | -7.117 | 16.501 | 35.071 |
| 15 | -7.342 | 22.078 | -30.910 |
| 16 | -4.735 | 8.138 | 22.848 |
| 17 | 4.832 | -17.261 | 23.088 |
| 18 | 15.999 | -37.837 | -35.053 |
| 19 | 16.993 | -2.367 | 37.653 |
| 20 | 32.723 | -2.220 | -32.959 |
| 21 | 17.731 | 18.629 | 23.714 |
| 22 | -31.607 | -11.173 | 37.192 |
| 23 | 12.106 | 20.291 | -23.469 |
| 24 | -12.427 | -3.119 | -23.924 |
| 25 | -17.883 | -2.630 | -23.845 |
| 26 | -28.674 | -0.414 | 30.659 |
| 27 | 21.110 | -29.121 | 28.578 |
| 28 | -22.866 | -6.455 | -21.739 |
| 29 | 4.537 | 2.259 | -24.076 |
| 30 | -19.315 | -15.131 | -22.853 |
| 31 | 18.614 | 19.289 | -2.168 |
| 32 | 26.618 | 2.255 | -5.246 |
| 33 | 40.353 | 7.508 | 24.848 |
| 34 | -9.582 | -51.344 | -9.087 |
| 35 | -2.681 | -22.759 | 20.161 |
| 36 | -35.632 | 0.648 | 4.101 |
| 37 | -3.240 | 6.385 | 27.803 |
| 38 | -5.453 | -41.070 | -12.153 |

To reproduce the census, enumerate the 703 unordered symbol pairs. Enumerate each
triple of pairs once in increasing pair-index order, keeping only triples with
six distinct symbols. This produces exactly 41,410,215 cases. Apply the normalized
determinant check first, then solve and apply the ellipsoid test. This procedure
counts pairings and does not deduplicate coordinates.

## Decisions still open

- Final reference catalogue, galaxy dimensions, and stability tolerance.
- Correction/update schedule, reference epoch, and stale-data limits. Acquisition
  uses the agreed 1-light-year radius, separate from numerical solver tolerances.
- Deterministic catalogue provisioning and alias assignment under acquisition
  tolerance, plus whether to impose minimum physical system separation.
- A sparse gate-occupancy policy, if introduced later. The initial galaxy design
  gives each provisioned distinct destination one primary gate-bearing planet;
  this is not one planet per raw pairing. No receiver remains a separate result
  from invalid geometry.
- Receiver priority when more than one gate occupies a location.
- Adoption of the documented glyph/reference adapter, origin-control proposal,
  and compatible reference catalogue; integration of the verified ring mapping.
- Intergalactic reference-frame translation and special nine-chevron registrations.
- Save format, generator version retention, and migration policy.
