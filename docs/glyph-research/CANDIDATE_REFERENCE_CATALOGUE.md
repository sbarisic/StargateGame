# Candidate reference catalogue for existing addresses

Date: 2026-11-06. Candidate ID: `rda-compatible-fit-20261106-v1`.
Status, 2026-11-07: adopted for the connection prototype. Historical fitting
methods and limitations below remain unchanged. Runtime data and full enumeration
are in `StargateGame.Galaxy`; see [DHD_DIALING.md](../../DHD_DIALING.md).

See [the compatibility proposal](../../STARGATE_KNOWN_ADDRESSES.md) for the source
records, ID conversion, fictional completions, and intergalactic route semantics.
The original catalogue remains in [STARGATE_ADDRESSING.md](../../STARGATE_ADDRESSING.md).

## Construction and limits

Start from the original 38 reference points in kly. Fit their 114 coordinates
against 36 first-six-symbol constraints: 32 complete ordinary source routes,
three proposed completed routes, and Othala's first six symbols for a separately
selected Ida frame. Othala's seventh symbol remains routing metadata.

The least-squares residual uses, per route, the unit-normal determinant `D` and
the cross-product numerator `w` of the intersection formula:

```text
20 * max(0, length(w / (50,50,8)) - 0.8 * abs(D))
20 * max(0, 0.12 - abs(D))
0.015 * (referenceCoordinates - originalCoordinates) / 40
```

The last term applies per reference coordinate and discourages gratuitous
movement. Optimization variables are coordinates divided by 40. SciPy
`least_squares` used 350 maximum function evaluations and ftol/xtol/gtol of
1e-9. It reached the evaluation budget; this is a feasible solution, not a
claimed global optimum or a fully converged optimum. The published six-decimal
coordinates were re-evaluated directly after rounding.

All 36 tested target intersections satisfy the actual `abs(D) >= 0.025` and
`q <= 1` requirements, where `q = sum((position/(50,50,8))^2)`.
Minimum absolute determinant: **0.119999934**.
Maximum q: **0.640004915**.
RMS reference displacement from baseline: **5.522633 kly**.

References are neither coplanar nor all on an equal-radius sphere. Radius from
the galactic origin ranges from 25.120404 to
51.716567 kly. The closest pair among the 35 proposed
Milky Way named targets is approximately **1134.895 ly** apart.
This excludes Othala's separate frame and does not test proximity to every
procedural target, realistic star density, or astrophysical lore positions.

## Exact candidate references

All coordinates below are **kly**. Multiply by 1,000 for the runtime galactic
light-year convention. RDA glyph 1 is origin artwork and has no reference row.

| Reference ID | RDA glyph ID | X (kly) | Y (kly) | Z (kly) |
| --- | --- | --- | --- | --- |
| 01 | 02 | 6.295226 | -31.854334 | -35.076593 |
| 02 | 03 | -16.089708 | 2.747918 | -35.537070 |
| 03 | 04 | 27.640413 | -24.969500 | -4.059987 |
| 04 | 05 | -35.188804 | -15.054097 | -11.224247 |
| 05 | 06 | -50.175428 | 12.481369 | 1.115846 |
| 06 | 07 | -10.798188 | 27.316668 | 7.653097 |
| 07 | 08 | 26.981446 | -16.939155 | 18.023552 |
| 08 | 09 | -0.306757 | -35.700898 | 27.519261 |
| 09 | 10 | 2.110806 | -37.466159 | -1.344351 |
| 10 | 11 | 2.644448 | 21.302474 | 21.260791 |
| 11 | 12 | -17.428509 | 5.807429 | -25.874201 |
| 12 | 13 | -27.035132 | 28.733652 | 4.034634 |
| 13 | 14 | -37.086700 | 19.791599 | -2.842566 |
| 14 | 15 | -6.380554 | 12.860171 | 33.725667 |
| 15 | 16 | -2.531620 | 18.514649 | -29.017571 |
| 16 | 17 | -5.041212 | 8.967359 | 23.799406 |
| 17 | 18 | 7.913170 | -19.853320 | 32.354816 |
| 18 | 19 | 14.922739 | -27.012314 | -33.427389 |
| 19 | 20 | 18.650157 | -2.092916 | 35.965610 |
| 20 | 21 | 31.910365 | -4.319491 | -30.007218 |
| 21 | 22 | 16.888270 | 17.634244 | 23.219815 |
| 22 | 23 | -31.881800 | -9.564102 | 35.871320 |
| 23 | 24 | 13.159861 | 17.942559 | -22.968794 |
| 24 | 25 | -13.136263 | -1.305372 | -29.849624 |
| 25 | 26 | -24.012627 | -2.846674 | -29.483868 |
| 26 | 27 | -24.655793 | -3.281421 | 28.968166 |
| 27 | 28 | 11.962995 | -22.191877 | 25.981165 |
| 28 | 29 | -25.138675 | -7.041687 | -24.540521 |
| 29 | 30 | 5.422528 | 2.692610 | -25.904701 |
| 30 | 31 | -20.903076 | -15.464792 | -29.263114 |
| 31 | 32 | 19.802697 | 26.666397 | -2.302262 |
| 32 | 33 | 22.840444 | 5.416778 | -8.944683 |
| 33 | 34 | 35.060701 | 3.252580 | 20.499716 |
| 34 | 35 | -8.675960 | -50.126120 | -7.125271 |
| 35 | 36 | -1.661524 | -24.981244 | 24.856684 |
| 36 | 37 | -38.616678 | 0.893660 | 2.907148 |
| 37 | 38 | -3.291866 | 6.987024 | 27.694627 |
| 38 | 39 | -5.453000 | -41.070000 | -12.153000 |


## Resolved route targets

Coordinates are displayed to six decimals; calculations used the exact reference
values above. Unknown source glyphs use the explicitly proposed game completions.
Ida reuses this catalogue numerically in its own frame for the prototype route.

| Record | Name | Frame | X (kly) | Y (kly) | Z (kly) | abs(D) | q |
| --- | --- | --- | --- | --- | --- | --- | --- |
| RDA-01 | Abydos | MilkyWay | -7.841311 | -9.430575 | -6.051571 | 0.426934 | 0.632380 |
| RDA-02 | Apophis's Base | MilkyWay | 22.698692 | -8.687754 | -5.083094 | 0.120016 | 0.639999 |
| RDA-03 | Castiana & Sahal | MilkyWay | 1.129596 | 4.353129 | -6.359422 | 0.438159 | 0.640001 |
| RDA-04 | Castiana & Sahal | MilkyWay | 13.979998 | 4.079102 | -5.270892 | 0.120002 | 0.518930 |
| RDA-05 | Chulak | MilkyWay | 1.886475 | -25.909728 | -3.412014 | 0.607546 | 0.451853 |
| RDA-06 | Clava Thessara Infinitas | MilkyWay | 3.908085 | 12.152710 | -3.844868 | 0.339184 | 0.296169 |
| RDA-07 | Clava Thessara Infinitas | MilkyWay | -6.490056 | -7.919295 | 5.786384 | 0.967031 | 0.565094 |
| RDA-08 | Destroyers | MilkyWay | 2.526586 | -6.144313 | -4.636922 | 0.141326 | 0.353608 |
| RDA-09 | Earth | MilkyWay | -11.609486 | -3.353408 | 5.067855 | 0.120004 | 0.459710 |
| RDA-10 | Edora | MilkyWay | -21.017159 | -19.542745 | 4.458119 | 0.190787 | 0.640000 |
| RDA-11 | Euronda | MilkyWay | -14.618569 | -12.029724 | -2.487686 | 0.357801 | 0.240063 |
| RDA-12 | Juna | MilkyWay | 3.289548 | -6.912344 | -4.977512 | 0.200313 | 0.410560 |
| RDA-13 | Kallana | MilkyWay | -13.349655 | -32.623208 | -3.025283 | 0.189289 | 0.640000 |
| RDA-14 | Kheb | MilkyWay | -15.102573 | -19.774602 | -4.963567 | 0.356348 | 0.632602 |
| RDA-15 | K'tau | MilkyWay | -6.413489 | 1.042020 | -5.823298 | 0.205444 | 0.546744 |
| RDA-16 | Land of Light | MilkyWay | 8.462637 | -4.741544 | -3.811793 | 0.877895 | 0.264667 |
| RDA-17 | Martin Lloyd's Homeworld | MilkyWay | 4.680316 | -12.539128 | 5.804836 | 0.270114 | 0.598156 |
| RDA-18 | NID Off-world Base | MilkyWay | 3.086723 | -6.309545 | -6.297217 | 0.120000 | 0.639344 |
| RDA-19 | Othala | Ida | -16.013669 | 8.485783 | 5.701933 | 0.356355 | 0.639379 |
| RDA-20 | P2X-555 | MilkyWay | -3.857260 | 4.532636 | -6.120618 | 0.961619 | 0.599513 |
| RDA-21 | P34-353J | MilkyWay | -33.308804 | -16.437185 | -2.375033 | 0.197713 | 0.640000 |
| RDA-22 | P3W-451 | MilkyWay | -2.650929 | -3.358296 | -5.814966 | 0.869089 | 0.535663 |
| RDA-23 | P3X-116 | MilkyWay | -6.983521 | -16.813836 | -1.121517 | 0.965525 | 0.152243 |
| RDA-24 | P3X-118 | MilkyWay | 2.960691 | -16.471795 | -5.655461 | 0.594388 | 0.611788 |
| RDA-25 | P3X-562 | MilkyWay | -10.147223 | -11.139949 | -1.666510 | 0.427699 | 0.134220 |
| RDA-26 | P3X-984 | MilkyWay | -17.288092 | -6.893328 | -5.665003 | 0.797669 | 0.640000 |
| RDA-27 | P9C-372 | MilkyWay | 2.707655 | -2.358830 | -0.172209 | 0.120000 | 0.005622 |
| RDA-28 | PB5-926 | MilkyWay | -10.685179 | 0.032159 | 5.683430 | 0.491662 | 0.550379 |
| RDA-29 | PX1-767 | MilkyWay | -1.212070 | -22.004311 | 4.931906 | 0.120000 | 0.574321 |
| RDA-30 | Praclarush Taonas | MilkyWay | -0.424213 | -12.714780 | -4.028554 | 0.726502 | 0.318320 |
| RDA-31 | Sangreal Planet | MilkyWay | 16.543710 | -18.896570 | 4.981182 | 0.651649 | 0.640000 |
| RDA-32 | Tartarus | MilkyWay | -5.266025 | -2.219417 | 6.334374 | 0.120000 | 0.640005 |
| RDA-33 | Tollan | MilkyWay | -13.966294 | -7.492975 | 5.462730 | 0.849008 | 0.566753 |
| RDA-34 | Tollana | MilkyWay | -1.227423 | -3.739361 | 2.823749 | 0.639541 | 0.130783 |
| RDA-35 | Vagon Brei | MilkyWay | -19.308992 | -29.810764 | 2.800719 | 0.191310 | 0.627170 |
| RDA-36 | Unnamed Planet | MilkyWay | 21.343729 | -26.728052 | -3.318054 | 0.307285 | 0.640001 |


## Exhaustive pairing census

Enumerate all 703 unordered pairs, then each disjoint triple once. Six distinct
symbols yield 41,410,215 canonical pairings. Normalize plane normals, reject
`abs(det(N)) < 0.025`, solve in float64, and apply the `(50,50,8)` kly ellipsoid.

| Result | Canonical pairings |
| --- | ---: |
| Stable and inside | 19,020,803 |
| Stable and outside | 20,804,218 |
| Rejected as unstable | 1,585,194 |
| Total | 41,410,215 |

Retained: **45.9326352%**. The original catalogue retained
12,931,523 (31.2278577%). Neither count deduplicates physical systems or applies
receiver availability, aliases, drift, minimum separation, or the acquisition
resolver. The candidate is not a claim of 19,020,803 unique planets.

The same combinatorial pool remains 41,410,215; fitting references changes the
geometrically usable subset. Three missing source fragments and Othala's route
do not create extra ordinary symbols or alter the 48-order equivalence rule.

## Adoption implications

Adopt only under a new catalogue version, retain old universes on their saved
catalogue, and migrate explicitly if desired. Rebuild spatial indices and
known-world registrations. Do not silently replace coordinates under an existing
version or relabel the old census as a result of this candidate.

The 1-light-year acquisition rule, correction revisions, location identities,
and local metre-scale gate transforms remain unchanged. Large catalogue changes
are not stellar drift updates. A separate final catalogue review should examine
overall density and numerical conditioning before production use.
