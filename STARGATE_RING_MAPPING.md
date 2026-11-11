# Stargate ring symbol mapping

Date: 2026-11-06. Status: inspected asset mapping and agreed shared-origin rule.
This is documentation and lookup data; no game meshes or dialing code were changed.
The complete spin, lock, origin, and activation interaction is defined in
[STARGATE_MANUAL_DIALING.md](STARGATE_MANUAL_DIALING.md).

## Result

The exported ring has **39 equally spaced symbol slots**. In the unrotated
model, the origin occupies the top slot. Viewed from the front, website glyphs
2 through 39 follow clockwise in numeric order.

```text
slot 00 = LOCAL_ORIGIN, existing origin artwork
slot 01 = RDA glyph 02 = reference 01
...
slot 38 = RDA glyph 39 = reference 38

slotPitch = 360 / 39 degrees = approximately 9.230769230769 degrees
```

Slot indices are zero-based. RDA glyph IDs and destination reference IDs are
separate namespaces; the slot index happens to equal the one-based reference ID
for destination slots 01-38. This is an observed asset layout, not a rule to assume
for a different Stargate model.

The glyph names and RDA numbering are described in
[STARGATE_GLYPHS.md](STARGATE_GLYPHS.md), based on the previously inspected
[source catalogue](https://www.rdanderson.com/stargate/glyphs/index.htm).

## One shared point of origin

Per the user's instruction, reuse the **same existing ring-origin glyph on every
gate**. It always means `LOCAL_ORIGIN`: the gate from which the player is dialing.
Its meaning comes from the departure context, not the artwork's resemblance to
Earth's symbol. It does not mean “dial Earth.”

There is no requirement to swap artwork for Abydos, Beta, or any other planet.
The catalogue's historical origin variants remain reference information only.
An Earth-origin dial and an Abydos-origin dial can therefore show the same
physical final glyph while resolving different departure identities.

Slot 00 has no ReferenceId and is never a destination plane. Keep all 38
destination symbols available. After six destination locks in same-galaxy mode,
locking slot 00 supplies the final origin. After seven routing-symbol locks in
the proposed intergalactic mode, it supplies the eighth symbol. Simply rotating
past it does not submit a symbol or activate a connection.

The standard DHD emits the same token through its explicit origin key; an expanded
controller can emit it through the centre control, as described in
[DHD_GLYPH_MAPPING.md](DHD_GLYPH_MAPPING.md). Neither needs an extra
planet-specific origin key. Departure identity remains independent of the
destination canonical key, LocationId, and world-generation seed.

## Inspected meshes and coordinate frame

| Role | Runtime part | Files |
| --- | --- | --- |
| Origin glyph, slot 00 | SM_Stargate_GlyphSwitchable | stargate_18.mesh / stargate_18.png |
| Glyph track and destination glyphs, slots 01-38 | SM_Stargate_GlyphTrack | stargate_19.mesh / stargate_19.png |
| Surrounding gate structure | SM_Stargate_Segment | stargate_20.mesh / stargate_20.png |

The [runtime manifest](assets/models/stargate.json) keeps these parts separate.
The glyph track contains the 38 destination glyphs inside one mesh, not one
runtime mesh per symbol. The origin mesh must rotate with the track, even though
it is a separate part. The outer structure and chevrons remain fixed while dialing.

The unscaled model-local rotation pivot is **C = (0, 3.064, 0) metres**.
The rotation axis is local Z. The front is the +Z side, looking toward -Z;
in that view +X is right and +Y is up. Angle zero is the vertical ray through
the top chevron, not the exported mesh's ground origin.

The pivot comes from the source assembly transform and is confirmed against
runtime vertices. Do not estimate it from the complete mesh bounding box, which
includes asymmetric details. Apply the entity's scale and world transform after
the local ring rotation; embedding the gate into terrain does not change its
local pivot or symbol angles.

![Numbered ring layout](docs/glyph-research/ring-slot-layout.png)

The figure renders extracted glyph triangles in the neutral pose. Labels show
slot indices; it omits the body, track surface, and chevrons for clarity.

## Complete mapping

Neutral angle is the slot-centre angle clockwise from the top at zero ring
rotation. Target angle is an **absolute clockwise ring orientation modulo 360**
that brings the selected slot to the fixed top read position. It is not the
amount to rotate from an arbitrary current orientation. Use the exact pitch
formula; six-decimal table values are for display.

| Ring slot | RDA glyph ID | Name / semantic | Reference ID | Neutral angle CW (deg) | Target ring angle CW (deg) | Corresponding DHD input |
| --- | --- | --- | --- | --- | --- | --- |
| 00 | 1 (art only) | LOCAL_ORIGIN (shared artwork) | — | 0.000000 | 0.000000 | Centre / origin action |
| 01 | 2 | Crater | 01 | 9.230769 | 350.769231 | SM_Key_02 |
| 02 | 3 | Virgo | 02 | 18.461538 | 341.538462 | SM_Key_03 |
| 03 | 4 | Bootes | 03 | 27.692308 | 332.307692 | SM_Key_04 |
| 04 | 5 | Centaurus | 04 | 36.923077 | 323.076923 | SM_Key_05 |
| 05 | 6 | Libra | 05 | 46.153846 | 313.846154 | SM_Key_06 |
| 06 | 7 | Serpens Caput | 06 | 55.384615 | 304.615385 | SM_Key_07 |
| 07 | 8 | Norma | 07 | 64.615385 | 295.384615 | SM_Key_08 |
| 08 | 9 | Scorpio | 08 | 73.846154 | 286.153846 | SM_Key_09 |
| 09 | 10 | Cra | 09 | 83.076923 | 276.923077 | SM_Key_10 |
| 10 | 11 | Scutum | 10 | 92.307692 | 267.692308 | SM_Key_11 |
| 11 | 12 | Sagittarius | 11 | 101.538462 | 258.461538 | SM_Key_12 |
| 12 | 13 | Aquila | 12 | 110.769231 | 249.230769 | SM_Key_13 |
| 13 | 14 | Mic | 13 | 120.000000 | 240.000000 | SM_Key_14 |
| 14 | 15 | Capricorn | 14 | 129.230769 | 230.769231 | SM_Key_15 |
| 15 | 16 | Pisces Austrinus | 15 | 138.461538 | 221.538462 | SM_Key_16 |
| 16 | 17 | Equuleus | 16 | 147.692308 | 212.307692 | SM_Key_17 |
| 17 | 18 | Aquarius | 17 | 156.923077 | 203.076923 | SM_Key_18 |
| 18 | 19 | Pegasus | 18 | 166.153846 | 193.846154 | SM_Key_19 |
| 19 | 20 | Sculptor | 19 | 175.384615 | 184.615385 | SM_Key_20 |
| 20 | 21 | Pisces | 20 | 184.615385 | 175.384615 | SM_Key_21 |
| 21 | 22 | Andromeda | 21 | 193.846154 | 166.153846 | SM_Key_22 |
| 22 | 23 | Triangulum | 22 | 203.076923 | 156.923077 | SM_Key_23 |
| 23 | 24 | Aries | 23 | 212.307692 | 147.692308 | SM_Key_24 |
| 24 | 25 | Perseus | 24 | 221.538462 | 138.461538 | SM_Key_25 |
| 25 | 26 | Cetus | 25 | 230.769231 | 129.230769 | SM_Key_26 |
| 26 | 27 | Taurus | 26 | 240.000000 | 120.000000 | SM_Key_27 |
| 27 | 28 | Auriga | 27 | 249.230769 | 110.769231 | SM_Key_28 |
| 28 | 29 | Eridanus | 28 | 258.461538 | 101.538462 | SM_Key_29 |
| 29 | 30 | Orion | 29 | 267.692308 | 92.307692 | SM_Key_30 |
| 30 | 31 | Canis Minor | 30 | 276.923077 | 83.076923 | SM_Key_31 |
| 31 | 32 | Monoceros | 31 | 286.153846 | 73.846154 | SM_Key_32 |
| 32 | 33 | Gemini | 32 | 295.384615 | 64.615385 | SM_Key_33 |
| 33 | 34 | Hydra | 33 | 304.615385 | 55.384615 | SM_Key_34 |
| 34 | 35 | Lynx | 34 | 313.846154 | 46.153846 | SM_Key_35 |
| 35 | 36 | Cancer | 35 | 323.076923 | 36.923077 | SM_Key_36 |
| 36 | 37 | Sextans | 36 | 332.307692 | 27.692308 | SM_Key_37 |
| 37 | 38 | Leo Minor | 37 | 341.538462 | 18.461538 | SM_Key_38 |
| 38 | 39 | Leo | 38 | 350.769231 | 9.230769 | SM_Key_39 |

![Individual ring glyphs](docs/glyph-research/ring-slot-contact-sheet.png)

Each tile shows the glyph geometry at that slot, rotated into its radial frame
and fitted to the tile. Glyph artwork can itself be rotated relative to the
website drawing. Matches were checked by shape, not merely by angular sequence.

Machine-readable research data: [ring-symbol-map.json](docs/glyph-research/ring-symbol-map.json).
This is not a runtime configuration file currently consumed by the game.

## Rotation and read-position formulas

Use `thetaCW` for the ring's clockwise angle as seen from the front:

```text
neutralSlotAngle(k) = k * 360 / 39
currentSlotAngle(k) = Wrap360(neutralSlotAngle(k) + thetaCW)
targetThetaCW(k)    = Wrap360(-neutralSlotAngle(k))
```

For example, RDA glyph 27 (Taurus, the first Abydos symbol) occupies slot 26,
which starts at 240 degrees clockwise from the top. A ring orientation of
120 degrees clockwise puts it at the top. Origin returns to the top at a
ring orientation of zero modulo 360, regardless of the departure planet.

Convert this user-facing clockwise convention to the game's right-handed local
rotation using **RotZ(-thetaCW)**. With column-vector notation:

```text
pRotated = C + RotZ(-thetaCW) * (p - C)
```

With System.Numerics row-vector transform composition, the equivalent local
matrix is `Translation(-C) * RotationZ(-thetaCW) * Translation(C)`; rotation
functions take radians. Apply this to both track and origin parts before their
ordinary entity transform. Check front and rear views explicitly: apparent
clockwise direction reverses when looking from behind.

For an arbitrary stopped ring orientation, identify the nearest slot and then
test its alignment error:

```text
k = Mod39(round(-thetaCW / slotPitch))
errorDeg = Wrap180(k * slotPitch + thetaCW)
canLock = abs(errorDeg) <= configuredLockToleranceDeg
```

The lock tolerance must be less than half a pitch (4.615384615 degrees).
Its exact gameplay value is not chosen here. A nearest-slot result alone does
not authorize a lock when the ring is between symbols. Boundary ties are
irrelevant when the alignment test rejects the half-pitch region.

When the player explicitly locks a symbol, capture the aligned slot once and
translate it to a destination glyph or LOCAL_ORIGIN. Validate expected symbol
count and dialing mode before appending it. Passing symbols during free rotation
does not append them. Completion of an origin lock at the correct final position
requests activation, as specified in the manual-dialing design.

Selecting clockwise/counterclockwise travel, extra revolutions, speed, and ring
inertia changes the animation path, not the mapping or destination identity.
Keep an unwrapped angle for continuous motion and wrap only for slot lookup.

## Evidence and validation

Source assets inspected under:
`D:/Games/Stargate Network/extraction/models/Stargate/MilkywayV2/Props/`.
Source assembly transform inspected in `extraction/assemble_scene.py`.
The source glTF glyph material is `MI_Stargate_Glyph_Clean`.

Source-to-runtime coordinate conversion used for verification:

```text
runtimeX = -sourceZ
runtimeY = sourceY + 3.064
runtimeZ = sourceX
```

Compared against the actual SGM1 vertices, the maximum nearest-vertex discrepancy
was approximately 2.67e-7 metres for the track glyphs and 3.32e-8 metres for the
origin. All 9,076 destination-glyph triangles fall in slots 01-38; the separate
origin contains 179 triangles in slot 00. Every glyph vertex lies within its
slot's half-pitch angular interval. Shape inspection confirms the RDA 2-39 order.

The shared origin is Earth-like apex/dot artwork in this mesh. Its universal
LOCAL_ORIGIN role is the selected game rule, not a claim that all screen-used
gates have identical artwork. No new origin textures or meshes are required.

The measurements validate the neutral exported asset layout. Runtime animated
dialing, server input validation, chevron motion, and angular locking are not yet
implemented or tested by this documentation task. The existing source artwork
retains its [asset provenance](assets/PROVENANCE.md).

Inspected file SHA-256 values:

| File | SHA-256 |
| --- | --- |
| stargate.json | `90166a29279a825b4497384ab199e61610967663ef68bb8e24ed9b7d6682a967` |
| stargate_18.mesh | `b4cb73bba6be5f934799ac1f9c8ec8489887a99af55eaf7722b57af6609593ad` |
| stargate_19.mesh | `d512f16edcb4e4e48530e803c0ca2aaf2cba6b28f2d1dd45036a566cd4656fca` |
