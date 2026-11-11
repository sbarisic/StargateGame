# Existing Stargate addresses: inventory and compatibility design

Research date: 2026-11-06. Status: complete inventory of the supplied page and a
tested design proposal. No runtime dialing or catalogue migration was implemented.

Primary source: [RDA glyphs and addresses](https://www.rdanderson.com/stargate/glyphs/index.htm). Numbering and source artwork are documented in
[STARGATE_GLYPHS.md](STARGATE_GLYPHS.md); physical input is covered in
[DHD_GLYPH_MAPPING.md](DHD_GLYPH_MAPPING.md).

## Findings and recommendation

The page contains **36 address entries**: 32 complete six-symbol destinations,
three incomplete six-symbol fragments, and Othala's seven-symbol intergalactic
route. The address strings omit the departure point of origin. Do not strip
their final supplied symbol as though it were origin.

Use the website's numbers on input and convert glyphs 2-39 to reference IDs 1-38.
Under that adapter, the original experimental catalogue accepts only 11 of the
32 complete ordinary entries. Twenty fall outside the ellipsoid and one fails
the normalized-determinant threshold. Earth is outside; Apophis's Base is unstable.
A 1-light-year acquisition tolerance cannot repair these catalogue-scale failures.

**Recommended design:** retain the three-pair equations and fit a new, versioned
reference catalogue with the supplied complete addresses as constraints. Then
bind their resolved locations to authored world definitions. Do not hash known
addresses into arbitrary positions or hide large relocations as drift corrections.

A concrete candidate has been computed and independently evaluated after rounding
its 38 reference positions to six decimals in kly. It supports all 35 proposed
ordinary routes, including the three explicitly fictional completions below.
It also makes Othala's first six symbols geometrically valid in a separately
selected Ida frame. Exact values, all resolved positions, and full census results
are in [the candidate catalogue](docs/glyph-research/CANDIDATE_REFERENCE_CATALOGUE.md).

The candidate is not adopted by this document. The old catalogue, visualization,
and 12,931,523 count remain the baseline for their existing experiment. Adoption
changes positions across the entire galaxy and requires a new catalogue version.

## All supplied address records

The baseline column uses the original catalogue, the proposed ID adapter,
`abs(det(N)) >= 0.025`, and ellipsoid semi-axes `(50,50,8)` kly. It is not a
statement about whether a fictional destination exists or its gate is operational.

| Record | Source label | Supplied sequence | Original geometry | Handling |
| --- | --- | --- | --- | --- |
| RDA-01 | Abydos | `27-7-15-32-12-30` | inside | Ordinary six-symbol destination |
| RDA-02 | Apophis's Base | `20-18-11-38-10-32` | unstable | Ordinary six-symbol destination |
| RDA-03 | Castiana & Sahal | `29-3-6-9-12-16` | outside | Shared source label; preserve A/B records |
| RDA-04 | Castiana & Sahal | `29-18-19-20-21-22` | inside | Shared source label; preserve A/B records |
| RDA-05 | Chulak | `9-2-23-15-37-20` | inside | Ordinary six-symbol destination |
| RDA-06 | Clava Thessara Infinitas | `14-21-16-11-30-7` | inside | Dream-derived address; distinct destination |
| RDA-07 | Clava Thessara Infinitas | `26-20-35-32-23-4` | outside | Dream-derived address; distinct destination |
| RDA-08 | Destroyers | `3-32-16-8-10-12` | outside | Ship-associated destination, not fixed GateId tracking |
| RDA-09 | Earth | `28-26-5-36-11-29` | outside | Ordinary six-symbol destination |
| RDA-10 | Edora | `28-38-35-9-15-3` | outside | Ordinary six-symbol destination |
| RDA-11 | Euronda | `30-27-9-7-18-16` | outside | Ordinary six-symbol destination |
| RDA-12 | Juna | `29-8-18-22-4-25` | outside | Ordinary six-symbol destination |
| RDA-13 | Kallana | `6-16-8-3-26-25` | inside | Ordinary six-symbol destination |
| RDA-14 | Kheb | `26-35-6-8-23-14` | outside | Ordinary six-symbol destination |
| RDA-15 | K'tau | `18-2-30-12-26-33` | outside | Ordinary six-symbol destination |
| RDA-16 | Land of Light | `26-2-?-38-10-32` | incomplete | Proposed game completion: 26-2-12-38-10-32 |
| RDA-17 | Martin Lloyd's Homeworld | `24-12-32-7-11-34` | outside | Ordinary six-symbol destination |
| RDA-18 | NID Off-world Base | `38-28-15-35-3-19` | outside | Ordinary six-symbol destination |
| RDA-19 | Othala | `11-27-23-16-33-3-9` | intergalactic | Seven routing glyphs, then departure origin |
| RDA-20 | P2X-555 | `28-8-16-33-13-31` | outside | Ordinary six-symbol destination |
| RDA-21 | P34-353J | `38-9-28-15-35-3` | outside | Ordinary six-symbol destination |
| RDA-22 | P3W-451 | `19-8-4-37-26-16` | outside | Ordinary six-symbol destination |
| RDA-23 | P3X-116 | `21-13-26-17-?-?` | incomplete | Proposed game completion: 21-13-26-17-30-35 |
| RDA-24 | P3X-118 | `9-26-34-37-17-21` | inside | Ordinary six-symbol destination |
| RDA-25 | P3X-562 | `3-28-9-35-24-15` | inside | Ordinary six-symbol destination |
| RDA-26 | P3X-984 | `29-5-36-6-23-?` | incomplete | Proposed game completion: 29-5-36-6-23-35 |
| RDA-27 | P9C-372 | `25-8-18-29-4-22` | inside | Ordinary six-symbol destination |
| RDA-28 | PB5-926 | `12-36-23-18-7-27` | outside | Ordinary six-symbol destination |
| RDA-29 | PX1-767 | `20-2-35-8-26-15` | outside | Ordinary six-symbol destination |
| RDA-30 | Praclarush Taonas | `35-3-31-29-5-17` | inside | Ordinary six-symbol destination |
| RDA-31 | Sangreal Planet | `30-19-34-9-33-18` | outside | Ordinary six-symbol destination |
| RDA-32 | Tartarus | `33-28-23-26-16-31` | outside | Ordinary six-symbol destination |
| RDA-33 | Tollan | `6-33-27-37-11-18` | outside | Ordinary six-symbol destination |
| RDA-34 | Tollana | `4-29-8-22-18-25` | inside | Ordinary six-symbol destination |
| RDA-35 | Vagon Brei | `3-8-2-12-19-30` | inside | Ordinary six-symbol destination |
| RDA-36 | Unnamed Planet | `9-18-27-15-21-36` | outside | Ordinary six-symbol destination |


![All supplied address strips and numbers](docs/glyph-research/address-reference-sheet.png)

All 32 complete six-symbol entries use six distinct IDs in 2-39. Their canonical
three-pair keys are distinct; adding the three proposed completions also produces
distinct keys. Each ordinary route can therefore retain our 48 equivalent input
orders without two listed destinations colliding at the canonical-key level.

## Incomplete addresses: preserve the evidence, choose game completions explicitly

`?` is unknown source information, not a dialable wildcard. Supporting every
entry requires either acquiring better source evidence or choosing and labelling
a game-specific completion. The following are concrete proposals, not recovered
canonical glyphs:

| Destination | Source fragment | Proposed complete game sequence | Valid ordered completions in baseline search |
| --- | --- | --- | --- |
| Land of Light | 26-2-?-38-10-32 | 26-2-12-38-10-32 | 10 |
| P3X-116 | 21-13-26-17-?-? | 21-13-26-17-30-35 | 712 |
| P3X-984 | 29-5-36-6-23-? | 29-5-36-6-23-35 | 10 |


The completion study enumerated unused IDs 2-39 in the missing slots, preserved
every known symbol and its position, rejected duplicate symbols and already
reserved canonical keys, and kept geometrically valid baseline results. It
selected the greatest absolute normalized determinant, with a lexicographic
tie-break. These choices were then included as constraints in the candidate fit.
Reversing P3X-116's final pair is an equivalent route under our game rules.

Store both `SourceSequence` with unknowns and `GameSequence` with completed values,
plus `CompletionKind = GameAuthored`. The address book can reveal the full game
sequence through exploration. Do not present these completions as website facts,
randomize them per dial, or let arbitrary matching fragments reach the same world.

## Known-world registration without changing the coordinate resolver

At catalogue-build time, reserve each selected canonical route, solve its nominal
position using the adopted reference catalogue, and create a persistent LocationId
there. Attach the authored name/world preset and receiver descriptor to that ID.
Register these entries before procedural provisioning, so an earlier random dial
cannot create a competing planet at a reserved known-world location.

Other aliases must resolve through the same deterministic provisioning policy.
Do not merge nearby physical systems just because their acquisition spheres
overlap. Existing association preference and ambiguity failure remain in force.

At runtime:

```text
DHD/ring/computer input + departure context + explicit route mode
  -> validate destination symbols and origin action
  -> select target galaxy/reference catalogue
  -> convert RDA IDs to reference IDs
  -> form and canonicalize the three pairs
  -> intersect planes and validate nominal geometry
  -> apply known correction for the associated location
  -> acquire eligible receiver within 1 light-year
  -> load the persistent authored/procedural world and connect
```

The named-world table supplies identity and content, not a bypass for invalid
ordinary geometry. Once configured, all 48 equivalent ordinary inputs reach the
same named destination, terrain seed, and saved state. The departure origin never
becomes part of that content seed.

The source does not give real XYZ positions. An authored Earth location would
occupy the candidate's solved coordinate, not an independently imposed astronomy
coordinate. Additional lore-based relative positions would need new constraints
and revalidation before adoption.

## Othala: a deliberate intergalactic route

The source supplies `11-27-23-16-33-3-9`. The [Othala entry](https://www.rdanderson.com/stargate/lexicon/entries/othala.htm)
places it outside the Milky Way and describes eight-glyph dialing. Its last `9`
must not be thrown away: the departure origin is added after all seven numbers.

Proposed first supported eight-chevron route:

```text
Mode: EightChevron / intergalactic
Source frame: MilkyWay
Recognized routing sequence: 11-27-23-16-33-3-9
Target frame: Ida
Destination pairs in target frame: (11,27), (23,16), (33,3)
Final origin token: departure gate's LOCAL_ORIGIN
```

Use a route registry keyed by source frame, mode, and the full seven-symbol
sequence. It returns the target frame and destination reference translation.
For this prototype proposal, Ida can explicitly reuse the candidate's reference
coordinates as its own versioned local frame. The candidate includes this first-six
constraint and yields an in-bounds Ida target. GalaxyId distinguishes it from an
identical numeric position in the Milky Way.

This is a game routing convention, not a claim that glyph 9 universally means Ida.
Unknown eight-chevron routes fail as unsupported; do not discard their seventh
symbol or infer a target galaxy from it alone. Initially whitelist the supplied
ordered Othala route; ordinary six-symbol equivalence does not automatically
define intergalactic route equivalence.

The source origin identifies departure; the eighth chevron and required power
must be supported before activation. A minimal second-galaxy descriptor and
Othala location suffice for this route; a fully generated second galaxy can follow.
Return routing must be registered separately; it cannot be inferred by reversing
the outgoing sequence. None of this creates a nine-chevron device-ID route.

## Duplicate labels and story context

- **Castiana and Sahal:** the source gives both sequences under a combined label.
  Its [Castiana](https://www.rdanderson.com/stargate/lexicon/entries/castiana.htm)
  and [Sahal](https://www.rdanderson.com/stargate/lexicon/entries/sahal.htm) entries
  also present the pair together. Keep separate `castiana_sahal_a` and
  `castiana_sahal_b` records until individual assignment is established. Assigning
  first-to-Castiana and second-to-Sahal is a plausible authoring choice, not a
  mapping verified from this page. Both routes can work without inventing certainty.
- **Clava Thessara Infinitas:** do not merge these two entries just because the
  label repeats. The [Clava entry](https://www.rdanderson.com/stargate/lexicon/entries/clava.htm)
  describes two dream-derived addresses and different visited/ambush locations;
  neither establishes the actual treasure location. Use distinct destination IDs.
- **Destroyers:** register a receiver at the route's destination location for the
  chosen scenario. A six-symbol address must not become an unlimited ship tracker.
  A moving ship leaving acquisition range needs corrections/registration as in
  the galaxy design; this entry is not evidence for nine-chevron device addressing.
- **Destroyed or hazardous destinations:** an address being valid does not imply
  a surviving, safe receiver. Choose the campaign era and gate availability
  separately. A sandbox compatibility preset can provision functioning receivers
  for all records; a story preset can deliberately leave some unreachable.

Thus “support all supplied addresses” means preserving all supplied symbols and
providing a defined route for every entry in the compatibility preset, with explicit
game completions for unknowns. It does not mean inventing missing canon or forcing
every destroyed gate to work in every story state.

## Controller profiles and worked dials

The standard DHD keeps origin key 01 and hides overlapping glyph 13; it accepts
six available destination symbols, explicit origin, then centre activation.
P2X-555 and P3X-116 need glyph 13 and are manual-only on such a DHD. Othala is
also beyond its seven-chevron capability. An expanded controller retains all
38 destination keys 02-39, hides key 01, and uses its centre to finalize origin
and request activation. Intergalactic input requires an additional capability.
Manual ring dialing retains the final physical origin lock. Every gate uses the
same origin artwork for that lock, interpreted from its departure identity;
see [STARGATE_RING_MAPPING.md](STARGATE_RING_MAPPING.md) for all ring positions.
The complete input sequence and coverage table are specified in
[STARGATE_MANUAL_DIALING.md](STARGATE_MANUAL_DIALING.md). Manual entry covers all
catalogue records under the selected completion, route, and power prerequisites.

- Earth to Abydos on a standard DHD: press `27,7,15,32,12,30`, origin, then centre. The logical final
  symbol is Earth's origin. Resolve the pairs using converted reference IDs.
- Abydos to Earth on a standard DHD: press `28,26,5,36,11,29`, origin, then centre. The final origin is
  Abydos's local origin, not Earth glyph 1 and not a destination symbol 40.
- Milky Way to Othala on an intergalactic expanded controller: select supported eight-chevron mode, enter
  `11,27,23,16,33,3,9`, then centre. Resolve Ida before target geometry.
- P3X-116 on an expanded controller in the proposed compatibility preset: enter
  `21,13,26,17,30,35`, then centre; the last two values are game-authored.

Numeric guides must display website IDs, not internal reference IDs or exported
mesh-file indices. A centre press is never itself a destination glyph.

## Validation and adoption gates

The research checked source inventory counts, graphic/number correspondence,
glyph-to-reference conversion, uniqueness of ordinary canonical keys, all selected
routes against both catalogues, and the complete candidate pairing pool. These
are offline calculations, not end-to-end in-game dialing tests.

Before implementation/adoption: select the catalogue version and completion
policy; implement DHD interaction and origin handling; integrate the documented ring mapping;
define the Ida route and return route; and implement authoritative provisioning,
receiver state, loading, and travel. Validate all 48 equivalents of every ordinary
known route, origin independence, unsupported mode failures, unknown fragments,
concurrent dials, and stale/missing receivers.

The earlier acquisition-neighbour study applies only to the original catalogue.
Re-evaluate neighbour/alias statistics for the candidate before treating that
study as evidence for its spatial ambiguity rate. Keep the agreed radius at 1 ly.
