# StargateGame DHD glyph mapping and asset findings

Research date: 2026-11-06. Status: inspection and design recommendations only.
No meshes, textures, collision, or game interactions were changed.

## Located assets

- Runtime manifest: [assets/models/dhd.json](assets/models/dhd.json).
- Runtime components: `SM_DHD_Base`, `SM_DHD_Dome`, and `SM_Key_01` through
  `SM_Key_39`, for 41 mesh parts in total.
- `SM_DHD_Base` uses `dhd_00.mesh`; the centre dome uses `dhd_01.mesh`.
- Key `NN` uses `dhd_(NN+1).mesh` and the corresponding baked PNG. These suffixes
  are export indices, not glyph IDs. Match by manifest part name, not file order.
- Source assembly: `D:/Games/Stargate Network/extraction/assembled/Stargate_and_DHD.blend`.
- Source key meshes: `D:/Games/Stargate Network/extraction/models/Technology/Ancient/DHD/Milkyway/Buttons/`.
- Source glyph atlas: the parent directory's `T_DHD_Glyph_E.png` and
  `T_DHD_Glyph_N.png`. The geometry uses `MI_Glyph_NoGlow`,
  `MI_Touches_NoGlow`, and `MI_Touches_Base` material slots.

The source assembly script imports the default numbered keys, not the separate
`SM_Key_01_Abydos.gltf` variant. Runtime geometry is Y-up and in metres, as
documented in [asset provenance](assets/PROVENANCE.md).

## Critical finding: 39 meshes, 38 key positions

`SM_Key_01` and `SM_Key_13` have identical runtime axis-aligned bounds and centre:
`(-0.373787, 1.072320, -0.216696)` approximately in model-local metres.
The source key-body vertex positions for material slots 1 and 2 are identical
between these two meshes. Their glyph geometry differs.

They occupy one physical slot; they are not two adjacent usable buttons.
The assembly currently includes both. This can produce overlapping artwork and
ambiguous interaction hits. The inspected layout has 19 inner and 19 outer key
positions. The presence of 39 named meshes must not be interpreted as 39 sockets.

The website's Earth artwork has a pyramid/sun outline. Source `SM_Key_01` has
a three-armed mark with a square end and does not visually match that artwork.
Its use as a generic origin key is a proposed semantic assignment, not a verified
Earth-art match. Keys 02-39 correspond to the same-numbered website shapes,
allowing for rotation and the source's thicker, filled styling.

![Numbered runtime DHD positions](docs/glyph-research/dhd-key-layout.png)

The highlighted `01/13` label marks the shared position. This is a projection of
actual runtime triangles onto the key plane, not a guessed screen-space layout.

## Full mapping for later implementation

Centres below are runtime mesh bounding-box midpoints in model-local metres,
rounded to four decimals. They identify parts; they are not collision shapes or
precise animation pivots. Do not use them as world-space coordinates without
applying the DHD entity transform.

| RDA ID | Source glyph name | Runtime part | Mesh / baked texture | Local centre XYZ (m) | Evidence / caveat |
| --- | --- | --- | --- | --- | --- |
| 01 | Earth | SM_Key_01 | `dhd_02.mesh` / `dhd_02.png` | (-0.3738, 1.0723, -0.2167) | Origin role proposed; art mismatch; overlaps 13 |
| 02 | Crater | SM_Key_02 | `dhd_03.mesh` / `dhd_03.png` | (0.0567, 0.9838, 0.1045) | Shape correspondence |
| 03 | Virgo | SM_Key_03 | `dhd_04.mesh` / `dhd_04.png` | (0.0855, 0.8380, 0.1921) | Shape correspondence |
| 04 | Bootes | SM_Key_04 | `dhd_05.mesh` / `dhd_05.png` | (-0.0614, 0.8369, 0.1939) | Shape correspondence |
| 05 | Centaurus | SM_Key_05 | `dhd_06.mesh` / `dhd_06.png` | (-0.3502, 0.9450, 0.0140) | Shape correspondence |
| 06 | Libra | SM_Key_06 | `dhd_07.mesh` / `dhd_07.png` | (0.1308, 1.2235, -0.4501) | Shape correspondence |
| 07 | Serpens Caput | SM_Key_07 | `dhd_08.mesh` / `dhd_08.png` | (-0.1399, 1.2211, -0.4460) | Shape correspondence |
| 08 | Norma | SM_Key_08 | `dhd_09.mesh` / `dhd_09.png` | (-0.1721, 1.0156, 0.0517) | Shape correspondence |
| 09 | Scorpio | SM_Key_09 | `dhd_10.mesh` / `dhd_10.png` | (-0.2818, 0.8923, 0.1017) | Shape correspondence |
| 10 | Cra | SM_Key_10 | `dhd_11.mesh` / `dhd_11.png` | (0.0851, 1.2170, -0.2839) | Shape correspondence |
| 11 | Scutum | SM_Key_11 | `dhd_12.mesh` / `dhd_12.png` | (0.2987, 0.8983, 0.0917) | Shape correspondence |
| 12 | Sagittarius | SM_Key_12 | `dhd_13.mesh` / `dhd_13.png` | (0.3885, 1.0156, -0.1219) | Shape correspondence |
| 13 | Aquila | SM_Key_13 | `dhd_14.mesh` / `dhd_14.png` | (-0.3738, 1.0723, -0.2167) | Shape correspondence; overlaps 01 |
| 14 | Mic | SM_Key_14 | `dhd_15.mesh` / `dhd_15.png` | (0.1837, 1.0186, 0.0467) | Shape correspondence |
| 15 | Capricorn | SM_Key_15 | `dhd_16.mesh` / `dhd_16.png` | (-0.2246, 1.1233, -0.1469) | Shape correspondence |
| 16 | Pisces Austrinus | SM_Key_16 | `dhd_17.mesh` / `dhd_17.png` | (-0.0868, 1.2158, -0.2820) | Shape correspondence |
| 17 | Equuleus | SM_Key_17 | `dhd_18.mesh` / `dhd_18.png` | (0.1269, 0.9954, 0.0852) | Shape correspondence |
| 18 | Aquarius | SM_Key_18 | `dhd_19.mesh` / `dhd_19.png` | (0.2209, 1.0508, -0.0069) | Shape correspondence |
| 19 | Pegasus | SM_Key_19 | `dhd_20.mesh` / `dhd_20.png` | (-0.3289, 1.1361, -0.3157) | Shape correspondence |
| 20 | Sculptor | SM_Key_20 | `dhd_21.mesh` / `dhd_21.png` | (-0.1825, 0.8549, 0.1639) | Shape correspondence |
| 21 | Pisces | SM_Key_21 | `dhd_22.mesh` / `dhd_22.png` | (0.2029, 0.8587, 0.1577) | Shape correspondence |
| 22 | Andromeda | SM_Key_22 | `dhd_23.mesh` / `dhd_23.png` | (-0.2480, 1.1905, -0.3950) | Shape correspondence |
| 23 | Triangulum | SM_Key_23 | `dhd_24.mesh` / `dhd_24.png` | (0.2353, 1.0885, -0.0882) | Shape correspondence |
| 24 | Aries | SM_Key_24 | `dhd_25.mesh` / `dhd_25.png` | (0.3266, 1.1446, -0.3273) | Shape correspondence |
| 25 | Perseus | SM_Key_25 | `dhd_26.mesh` / `dhd_26.png` | (-0.0403, 0.9834, 0.1052) | Shape correspondence |
| 26 | Cetus | SM_Key_26 | `dhd_27.mesh` / `dhd_27.png` | (0.2293, 1.1277, -0.1539) | Shape correspondence |
| 27 | Taurus | SM_Key_27 | `dhd_28.mesh` / `dhd_28.png` | (-0.2118, 1.0469, -0.0004) | Shape correspondence |
| 28 | Auriga | SM_Key_28 | `dhd_29.mesh` / `dhd_29.png` | (-0.0030, 1.2217, -0.2919) | Shape correspondence |
| 29 | Eridanus | SM_Key_29 | `dhd_30.mesh` / `dhd_30.png` | (0.2416, 1.1955, -0.4033) | Shape correspondence |
| 30 | Orion | SM_Key_30 | `dhd_31.mesh` / `dhd_31.png` | (-0.1512, 1.1982, -0.2526) | Shape correspondence |
| 31 | Canis Minor | SM_Key_31 | `dhd_32.mesh` / `dhd_32.png` | (-0.2283, 1.0841, -0.0779) | Shape correspondence |
| 32 | Monoceros | SM_Key_32 | `dhd_33.mesh` / `dhd_33.png` | (-0.3805, 1.0072, -0.1050) | Shape correspondence |
| 33 | Gemini | SM_Key_33 | `dhd_34.mesh` / `dhd_34.png` | (0.1510, 1.2007, -0.2567) | Shape correspondence |
| 34 | Hydra | SM_Key_34 | `dhd_35.mesh` / `dhd_35.png` | (0.3765, 1.0806, -0.2302) | Shape correspondence |
| 35 | Lynx | SM_Key_35 | `dhd_36.mesh` / `dhd_36.png` | (-0.1988, 1.1636, -0.2059) | Shape correspondence |
| 36 | Cancer | SM_Key_36 | `dhd_37.mesh` / `dhd_37.png` | (-0.1134, 0.9936, 0.0882) | Shape correspondence |
| 37 | Sextans | SM_Key_37 | `dhd_38.mesh` / `dhd_38.png` | (0.3625, 0.9526, 0.0013) | Shape correspondence |
| 38 | Leo Minor | SM_Key_38 | `dhd_39.mesh` / `dhd_39.png` | (-0.0068, 1.2323, -0.4648) | Shape correspondence |
| 39 | Leo | SM_Key_39 | `dhd_40.mesh` / `dhd_40.png` | (0.2009, 1.1688, -0.2118) | Shape correspondence |


![Extracted glyph geometry](docs/glyph-research/meshes-contact-sheet.png)

The sheet shows only triangles assigned to the source glyph material, projected
onto each key's fitted plane. It includes the unused Abydos variant. It omits
lighting, normal-map relief, and the key body; rotations differ from the website
sheet in [STARGATE_GLYPHS.md](STARGATE_GLYPHS.md).

## Standard and expanded controller profiles

Updated design, 2026-11-06: the user accepts that some catalogue routes can be
manual-only. The standard DHD therefore keeps key 01 as LOCAL_ORIGIN and hides
the overlapping Aquila/key 13. It has 37 destination keys plus origin. Enter six
available destination symbols, press origin, then the centre dome to activate.
It supports seven-chevron dialing only. P2X-555 and P3X-116 require glyph 13 and
must use the manual ring or an expanded controller; Othala needs eight-chevron
capability and power. See [manual dialing](STARGATE_MANUAL_DIALING.md) for coverage.

The earlier all-symbol layout is retained as an optional expanded controller:
keep keys **02-39** and suppress key 01. Use the centre dome to finish a dial.
With six destination symbols in same-galaxy
mode, pressing it performs two ordered logical events: lock the departure
`LOCAL_ORIGIN`, then request activation. With seven routing symbols in the
intergalactic mode, do the same after the seventh. Use the same shared ring-origin
artwork in the dial display on every planet; resolve LOCAL_ORIGIN from the departure
gate. One physical press can perform the two logical actions.

The expanded profile combines origin and activation into one physical action;
the standard profile keeps them as separate origin-key and centre presses.
Manual ring dialing still locks a physical local-origin glyph as its final symbol,
and completion of the final lock requests activation. Merely spinning past it cannot activate.

An explicit intergalactic upgrade must distinguish 7-chevron and 8-chevron input so that
Othala's seventh routing symbol is not mistaken for the local origin. Reject an
early centre press without discarding the entered symbols. Cancel/backspace
should be separate input actions, not accidental extra symbols.

## Picking, feedback, and authority

The current [collision exporter](tools/export_collision.py) includes only the
DHD Base and Dome. It deliberately excludes keys. Therefore a structural
physics hit alone cannot identify a glyph key.

For later implementation, add interaction-only key triangles or fitted local
hit volumes keyed by `DhdPartName`. Transform the player's ray into DHD local
space, perform occlusion-aware nearest-hit picking, then map the part name to
`RdaGlyphId`. Use the dome's active surface rather than its whole broad AABB.
Do not add all key detail to dynamic world collision solely to get click IDs.

Clients can show hover, press animation, and the current sequence. The server
validates interaction range, selected DHD/gate, dialing mode, symbol count,
duplicate rules, and the final resolver result. A client supplies symbol IDs and
input intent, not arbitrary destination coordinates.

The exported SGM1 mesh format merges triangle data per part and has no per-triangle
material index. The new offline DHD material exporter maps source material
assignments and source glyph UV masks into dedicated runtime emission atlases.
The client can illuminate each visible key's glyph and rim independently;
`DhdGlyphState` uses the source key number. Standard rendering hides key 13's
overlapping mesh and keeps key 01. This implements visual feedback only;
picking, input, dialing and authority described above remain future work.

## Ring mapping and shared origin artwork

The runtime Stargate manifest contains `SM_Stargate_GlyphTrack` and
`SM_Stargate_GlyphSwitchable`; glyphs are not exported as 39 separate ring parts.
DHD mesh suffixes alone do not establish ring angular positions. The subsequent
[ring inspection](STARGATE_RING_MAPPING.md) verifies all 39 slots, the local pivot,
front-view direction, and the rotation needed to align each symbol at the top.

The agreed rule now reuses the existing ring-origin artwork on every gate as
LOCAL_ORIGIN. Planet-specific origin artwork is not required. The unused Abydos
key variant remains a historical asset finding, not a new DHD socket. The DHD
key-01/key-13 overlap remains a separate issue; it does not exist on the ring.

## Evidence and limitations

The mapping combines [RDA glyphs and addresses](https://www.rdanderson.com/stargate/glyphs/index.htm), visual inspection of all glyph shapes,
the runtime manifest/SGM1 vertices, source glTF material primitives, the assembly
script, and collision-export selection. Key 01 is explicitly unresolved as an
Earth-art match. This work does not recover the original executable's input logic
or establish that our exported DHD already supports dialing.

Inspected runtime manifest SHA-256: `b89df41e9a3a07afe89b9098307a7700f93302cb1cea7fc3615ecdaefe8b13cf`.
All source artwork retains the provenance described in [assets/PROVENANCE.md](assets/PROVENANCE.md).
