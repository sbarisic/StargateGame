# Stargate glyph catalogue and numbering

Research date: 2026-11-06. Status: source inventory and proposed game ID mapping.
No game code, asset numbering, or saved reference catalogue was changed.

Primary source: [RDA glyphs and addresses](https://www.rdanderson.com/stargate/glyphs/index.htm). The page supplies 39 numbered Milky Way glyphs,
18 point-of-origin examples, and 36 address entries. It uses three additional
origin images whose filenames end in 40, 41, and 42. Those filenames do not
establish three extra constellation glyphs or expand our 38-reference alphabet.

## Numbering contract

Keep these namespaces distinct:

- `RdaGlyphId`: the website's visible numbering, 1 through 39. Keep it unchanged
  when transcribing addresses or showing a numeric guide to players.
- `ReferenceId`: the geometry catalogue's existing one-based 01 through 38.
  Proposed adapter: `ReferenceId = RdaGlyphId - 1` for RDA 2 through 39.
- `ReferenceArrayIndex`: zero-based implementation index, `RdaGlyphId - 2`.
- `LOCAL_ORIGIN`: a semantic token resolved from the departure gate, not another
  navigational reference. RDA 1 is Earth-origin artwork, not Earth's destination.
- `OriginArtworkId`: a separate presentation identifier. Several origin examples
  reuse the artwork of ordinary destination glyphs.
- `DhdPartName`: an asset identifier such as `SM_Key_27`; see the verified asset
  findings in [DHD_GLYPH_MAPPING.md](DHD_GLYPH_MAPPING.md).

For example, Abydos's `27-7-15-32-12-30` becomes reference IDs
`26-6-14-31-11-29`, then pairs `(26,6)/(14,31)/(11,29)`. Preserve the source
order until pairing; canonicalization only sorts within pairs and between pairs.

This adapter labels the existing arbitrary references; it does not assert that
their XYZ values are measured constellation positions. Adoption must be versioned.

## All 39 numbered glyphs

Names below retain the source's spelling and abbreviations, including `Cra`,
`Mic`, `Scorpio`, and `Capricorn`; numeric IDs are the stable lookup keys.

| RDA ID | Source name | Proposed reference ID | Visual source |
| --- | --- | --- | --- |
| 01 | Earth | LOCAL_ORIGIN artwork; no reference | [glyph01.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph01.png) |
| 02 | Crater | 01 | [glyph02.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph02.png) |
| 03 | Virgo | 02 | [glyph03.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph03.png) |
| 04 | Bootes | 03 | [glyph04.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph04.png) |
| 05 | Centaurus | 04 | [glyph05.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph05.png) |
| 06 | Libra | 05 | [glyph06.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph06.png) |
| 07 | Serpens Caput | 06 | [glyph07.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph07.png) |
| 08 | Norma | 07 | [glyph08.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph08.png) |
| 09 | Scorpio | 08 | [glyph09.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph09.png) |
| 10 | Cra | 09 | [glyph10.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph10.png) |
| 11 | Scutum | 10 | [glyph11.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph11.png) |
| 12 | Sagittarius | 11 | [glyph12.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph12.png) |
| 13 | Aquila | 12 | [glyph13.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph13.png) |
| 14 | Mic | 13 | [glyph14.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph14.png) |
| 15 | Capricorn | 14 | [glyph15.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph15.png) |
| 16 | Pisces Austrinus | 15 | [glyph16.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph16.png) |
| 17 | Equuleus | 16 | [glyph17.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph17.png) |
| 18 | Aquarius | 17 | [glyph18.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph18.png) |
| 19 | Pegasus | 18 | [glyph19.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph19.png) |
| 20 | Sculptor | 19 | [glyph20.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph20.png) |
| 21 | Pisces | 20 | [glyph21.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph21.png) |
| 22 | Andromeda | 21 | [glyph22.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph22.png) |
| 23 | Triangulum | 22 | [glyph23.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph23.png) |
| 24 | Aries | 23 | [glyph24.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph24.png) |
| 25 | Perseus | 24 | [glyph25.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph25.png) |
| 26 | Cetus | 25 | [glyph26.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph26.png) |
| 27 | Taurus | 26 | [glyph27.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph27.png) |
| 28 | Auriga | 27 | [glyph28.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph28.png) |
| 29 | Eridanus | 28 | [glyph29.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph29.png) |
| 30 | Orion | 29 | [glyph30.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph30.png) |
| 31 | Canis Minor | 30 | [glyph31.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph31.png) |
| 32 | Monoceros | 31 | [glyph32.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph32.png) |
| 33 | Gemini | 32 | [glyph33.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph33.png) |
| 34 | Hydra | 33 | [glyph34.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph34.png) |
| 35 | Lynx | 34 | [glyph35.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph35.png) |
| 36 | Cancer | 35 | [glyph36.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph36.png) |
| 37 | Sextans | 36 | [glyph37.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph37.png) |
| 38 | Leo Minor | 37 | [glyph38.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph38.png) |
| 39 | Leo | 38 | [glyph39.png](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph39.png) |

![Website glyph reference sheet](docs/glyph-research/website-contact-sheet.png)

The final three tiles on the sheet are origin image filename numbers, not new
numbered entries in the website's 1-39 glyph catalogue. Graphic orientation may
differ on a rotating ring or radial DHD key; identify shapes independent of rotation.

## All listed point-of-origin examples

Game decision, 2026-11-06: use the same existing ring-origin artwork on every
gate, always interpreted as LOCAL_ORIGIN from departure context. The variants
below are source reference information, not required per-planet assets. See
[STARGATE_RING_MAPPING.md](STARGATE_RING_MAPPING.md) for the verified ring layout.

These are artwork/source-context associations on the page, not additional
destination addresses. Reusing a destination glyph's shape as origin must not
remove that glyph from the destination alphabet.

| Departure/context | Artwork reference | Visual source |
| --- | --- | --- |
| Earth | RDA 01 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph01.png) |
| Abydos | origin-only image file 40 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph40.png) |
| Chulak | RDA 07 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph07.png) |
| P3X-562 | RDA 14 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph14.png) |
| Tollan | RDA 02 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph02.png) |
| Beta Stargate | origin-only image file 41 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph41.png) |
| P3R-233 | RDA 21 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph21.png) |
| Goa'uld Ha'tak | RDA 24 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph24.png) |
| P7J-989 | origin-only image file 42 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph42.png) |
| Tok'ra Planet | RDA 11 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph11.png) |
| P34-353J | RDA 37 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph37.png) |
| Medieval Planet | RDA 15 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph15.png) |
| Rendezvous | RDA 23 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph23.png) |
| P2X-416 | RDA 25 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph25.png) |
| P7S-441 | RDA 29 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph29.png) |
| Pangar | RDA 18 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph18.png) |
| Tagrea | RDA 25 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph25.png) |
| P4M-328 | RDA 33 | [source image](https://www.rdanderson.com/stargate/glyphs/glyphs/glyph33.png) |


The two Tok'ra-labelled entries and the shared Perseus art for P2X-416/Tagrea
remain separate source records. Do not infer that shared artwork means shared
planet identity. A symbol used as a destination glyph earlier in a dial remains
a destination glyph even when the departure origin uses the same art.

The local extraction contains `SM_Key_01_Abydos`, which visually matches the
Abydos-origin family after rotation. It does not contain separately named Beta
or P7J-989 origin key variants in the inspected DHD Buttons directory. Other
artwork may be available elsewhere; this is not a claim about the entire game.

## Scope and evidence

The source page was parsed by glyph/address containers, not OCR. Its 39 glyph
labels, 18 origin records, and 36 address strings were extracted, and glyph
graphics/address strips were inspected visually. Unknown `?` symbols are retained.
Research figures combine source images for comparison; they are not new runtime
assets or a statement of redistribution rights.

Source HTML SHA-256 at retrieval: `0e7e1591773a3bd0854ee324a1a80517509aefee107b3418da3b5942c0404cf3`.

See [the complete address inventory and dialing proposal](STARGATE_KNOWN_ADDRESSES.md)
and [the DHD mapping](DHD_GLYPH_MAPPING.md). These findings cover the supplied
Milky Way reference page, not an exhaustive Pegasus or Universe glyph alphabet.
