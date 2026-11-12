# Third-party notices

The project uses window/render/input integration patterns from RaylibGame and a
pinned FishGfx dependency. Their MIT notices follow. The FishGfx checkout retains
its own dependency and font notices. The Stargate Network artwork is separate;
see `assets/PROVENANCE.md`.

MIT License

Copyright (c) 2020 Saša Barišić (RaylibGame)
Copyright (c) 2018 Saša Barišić (FishGfx)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Jolt physics

Jolt Physics is Copyright 2021 Jorrit Rouwe, licensed under MIT.
JoltPhysicsSharp and joltc are Copyright (c) Amer Koleci and Contributors,
also licensed under MIT. Full license texts are retained in `native/licenses/`
and copied into application distributions.

The C# binding is pinned to NuGet JoltPhysicsSharp 2.23.0, source commit
`e468f1f30ef74726a799ceef1f8427fa39867e93`. The game distributes a custom joltc
binary, built from pinned sources with the local `native/rollback.inl` extension.
See `native/README.md` for source revisions, build options, and checksums.

## Runtime terrain generation

LayerProcGen is Copyright (c) 2024 Rune Skovbo Johansen, licensed under MPL-2.0.
Pinned source: https://github.com/runevision/LayerProcGen at
`7fb6100ddcffa79ff993d4abbfb61602501cfcc8`.
The non-Unity source is compiled by `vendor/LayerProcGen/LayerProcGen.csproj`.
`vendor/LayerProcGen/LayerManager.cs` retains its upstream notice and has two
lifecycle changes: make the worker a background thread, and unregister its
UnhandledException handler during Unload. The corresponding source is included
in this repository under MPL-2.0. All other upstream files remain unchanged.
Upstream's third-party notices and MPL text are in `vendor/licenses/` and are
included in published applications. Preserve this source availability when distributing binaries.

FastNoiseLite is Copyright (c) 2023 Jordan Peck and Contributors, licensed under MIT.
The C# implementation is pinned to v1.1.1, commit
`7ccfbc16eb1c932568f177d63a9ba51d89bbe516`, from
https://github.com/Auburn/FastNoiseLite. Its full MIT text is in
`vendor/licenses/FastNoiseLite-MIT.txt` and is included in published applications.

## Shadow shader adaptation

assets/shaders/shadows.glsl adapts the directional shadow sampling functions
from FishGfx/data/shaders/voxel.frag under the FishGfx MIT notice above.
Game-specific changes include three sampler slots, receiver-triangle depth
correction, adjusted bias, and distance fading. The FishGfx dependency is unchanged.

## Persistent storage

Microsoft.Data.Sqlite 10.0.12 and its SQLitePCLRaw dependencies are used by the
server persistence project (MIT licenses). SQLite itself is public domain.
Sources/notices: https://github.com/dotnet/efcore,
https://github.com/ericsink/SQLitePCL.raw, https://www.sqlite.org/copyright.html.
The procedural building kit is game-owned geometry. Its surface reuses the
existing extracted cliff texture documented in assets/PROVENANCE.md.

## Atmospheric scattering � Eric Bruneton

The atmosphere uses Eric Bruneton's precomputed atmospheric scattering model:
https://github.com/ebruneton/precomputed_atmospheric_scattering
Commit: `34f14e745cff948f4ca3157d1b62a445ffa7286f`.
Copyright (c) 2017 Eric Bruneton. BSD-3-Clause.

Unmodified `definitions.glsl`, `functions.glsl`, and the complete license are in
`assets/shaders/bruneton/`. File hashes and pinned source URLs are recorded in
`provenance.json`. The loader adapts 3D sampler declarations and texture calls to
2D atlas sampling in memory. Game-owned fragment passes supply reduced table
sizes, RGB parameters, multiple-scattering iteration and runtime integration.
The license ships with the client assets. The Godot shader linked during design
was an appearance reference; its nested ray integrator was not copied.

## Audio code and recordings

The audio layer is adapted from RaylibGame revision `a183d45726f60d50e1a7d2a4803acf4ad82474a6`; its code license is retained in `vendor/licenses/RaylibGame-Audio-LICENSE.txt`. Miniaudio revision `9634bedb5b5a2ca38c1ee7108a9358a4e233f14d` retains its license in `vendor/miniaudio/LICENSE`.

Stargate Network and RaylibGame recordings retain their original rights and attribution. These code licenses do not license the recordings. `assets/audio/provenance.json` records the source files and conversions; no new redistribution permission is inferred.

## Imported physical props

This work is based on "Wooden Box"
(https://sketchfab.com/3d-models/wooden-box-afb1324784774f9d8de52f1c9756a052)
by Alexey Stepanov (https://sketchfab.com/Alexey.Stepanov), licensed under
CC-BY-4.0 (https://creativecommons.org/licenses/by/4.0/).

This work is based on "Dirty oil barrel - 5MB"
(https://sketchfab.com/3d-models/dirty-oil-barrel-5mb-b973a95ff3bb478a941ccb06a70b14bb)
by adventurer (https://sketchfab.com/ahmagh2e), licensed under CC-BY-4.0
(https://creativecommons.org/licenses/by/4.0/).

Changes: scene-transform baking, metre scaling, centred pivots, separate barrel
variants, texture conversion/downsampling and generated collision hulls.
Original attribution files and source hashes accompany the imported model assets.
The new footsteps are user-supplied recordings without a license file in their
source folder; code licenses do not grant recording rights.

## Imported building components

- Medieval Village MegaKit (Standard), Quaternius: CC0; source license retained in
  `assets/building/megakit-license.txt`. Sixteen native components are used, with a
  game-authored stone foundation using the kit material.
- Kit Fortification, TuszPro: CC BY 4.0; source credit and model link retained in
  `assets/building/fortification-license.txt`. Eleven separated components are used.
- Conversion: source transforms and gallery offsets normalized, Y-up metre meshes,
  original UVs, resized textures, material batching, collision metadata and rendered
  thumbnails. No source assets were modified. Reproduction scripts and source
  SHA-256 values are in `tools/*building_kit.py` and `assets/building/provenance.json`.


## Realistic trees

This work is based on "Realistic Trees Pack of 2 Free"
(https://sketchfab.com/3d-models/realistic-trees-pack-of-2-free-08b4a9eac77a40419fd59402cc7b2deb)
and "More Realistic Trees Free!"
(https://sketchfab.com/3d-models/more-realistic-trees-free-b5b506fc4f5d4af9b546283bdf0c6a15)
by Nicholas-3D (https://sketchfab.com/Nicholas01), licensed under CC BY 4.0
(https://creativecommons.org/licenses/by/4.0/).

Changes: reduced geometry and leaf cards, four detail levels, planting-pivot
alignment, runtime mesh conversion, texture resizing and normal-map conversion,
and wood-only collision. Original attribution files and conversion provenance
are retained alongside the runtime model assets.


## Ground materials and foliage

Ground material scans by FreePBR.com. Original usage notice:
`assets/terrain/pbr/FreePBR-notice.txt`. The manifest records source/output hashes.
Changes: packing, OpenGL-to-DirectX normals, colour-correct albedo mipmaps and
renormalized normal mipmaps. Original source packs remain untouched.

This work is based on "Some Foliage"
(https://sketchfab.com/3d-models/some-foliage-5e806681504a4642a16a59c057d34e8d)
by soidev (https://sketchfab.com/soidev), licensed under CC BY 4.0
(http://creativecommons.org/licenses/by/4.0/).
Changes: baked transforms, planting pivots, unit-height Y-up meshes, reduced
LODs, texture packing, normal conversion and alpha-preserving mipmaps.
Full source attribution and hashes accompany assets/foliage.

## Settlement source models

Converted settlement assets retain their supplied author/license files under
`assets/settlements/licenses/`. `assets/settlements/provenance.json` records source
hashes and conversions; `lods.json` records derived near/far triangle counts.
The importer does not modify originals or infer asset permission from code licenses.
See [SETTLEMENTS.md](SETTLEMENTS.md) for the imported families and reproduction steps.

## Interior furnishings

The generated furniture is based on the following CC BY 4.0 works:

- "Furniture set" by Asylum Nox: https://sketchfab.com/3d-models/furniture-set-09b8b458fbad4986b6fd44698f5bc6dc
- "Rustic Wood Furniture Assets" by Pinos: https://sketchfab.com/3d-models/rustic-wood-furniture-assets-a791cdb4de744866aed3725f49027108
- "Day5-the final tavern" by Xeka Volnin: https://sketchfab.com/3d-models/day5-the-final-tavern-35306e061fc547a0b17d9c44d2dc35c5

License: https://creativecommons.org/licenses/by/4.0/
Changes: selected individual assets, baked transforms, metre-scale pivots, runtime
mesh conversion, texture resizing and normal-map conversion, simplified collision,
and tableware compositions. Original attribution files are included under
assets/furnishings/licenses; source selections and hashes accompany that folder.

## Gate spray textures

`assets/gate-effects/` contains unchanged UE Viewer PNG exports of Stargate
Network 4.0.0-a1 assets `T_SmokeSubUV_8X8`, `T_Smoke_Tiled_D` and `T_Snow_fx`.
Original channel values, including smoke alpha, are retained. Source paths and
export SHA-256 hashes are recorded in `assets/gate-effects/provenance.json`.
These are extracted game artwork, not covered by the code's MIT notices. No new
license or redistribution permission is granted. The atlas is sampled as a static
density cell; its name is not evidence of a verified 64-frame animation.


Grass blade scans: **Foliage 008**, ambientCG / Lennart Demes, CC0 1.0 Universal.
Source: https://ambientcg.com/a/Foliage008 ; license: https://docs.ambientcg.com/license/ .
The seven photometric blade scans are normalized by `tools/import_grass_blades.py` onto the procedural curved grass geometry. Source hashes and conversion details are retained in `assets/vegetation/grass-blades.provenance.json`. Original downloaded maps stay in the external AssetSources library.

### Meadow textures (2026-11-09)

Ground meadow uses ambientCG Grass005 (https://ambientcg.com/a/Grass005),
CC0 1.0 (https://docs.ambientcg.com/license/). It is a procedural material with
bitmap elements. `tools/import_meadow_ground.py` packs the original colour,
DirectX normal, roughness, AO and height maps, with a small linear colour match.
Source hashes and conversion settings are retained in the terrain manifest.
Original downloads remain unchanged.

### Realistic Grass Pack
This work is based on "Realistic Grass Pack For Games Free!" (https://sketchfab.com/3d-models/realistic-grass-pack-for-games-free-9b958d613e9a44dbba580748e7a1789c) by Nicholas-3D (https://sketchfab.com/Nicholas01), licensed under CC-BY-4.0 (https://creativecommons.org/licenses/by/4.0/). Changes: normalized planting pivots and scale, olive colour grade, alpha-preserving mipmaps, whole-card LODs. Original notice: assets/foliage/realistic-grass-license.txt.



## First-person gloved arms

This work is based on ["FPS pistol animations"](https://sketchfab.com/3d-models/fps-pistol-animations-0d7a343dcb6f401197a73c91aee93f6d)
by [Cransh](https://sketchfab.com/ccransh), licensed under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).
Only the arms mesh and its textures are used. Changes: fitted to the existing
rifle skeleton, remapped skin weights, 2K texture conversion, and gloss-to-roughness
conversion. The pistol mesh and animations are not included.
Original attribution: `assets/characters/arms2-license.txt`.

## SLTerrainGeneration hydrology

RegionalHydrology.cs adapts D8 slope analysis and stream-power sediment-capacity
ideas from Michael Tuck's SLHydrology (2024), https://github.com/acellular/slterraingeneration.
Changes: C# implementation; explicit metre and diagonal distances; deterministic
priority-flood routing; iterative accumulation; bounded conservative sediment transport
instead of the original aspect-derivative USPED implementation; cancellation checks.
The upstream MIT license is retained in thirdparty/slterraingeneration/LICENSE.
No native dependency or upstream lake-water-budget implementation is included.

## Regional terrain additions

Marble Rock 03 and Dry Riverbed Rock by Amal Kumar, Poly Haven:
https://polyhaven.com/a/marble_rock_03 and https://polyhaven.com/a/dry_riverbed_rock.
Ground 055 S and Snow 002 by ambientCG:
https://ambientcg.com/a/Ground055S and https://ambientcg.com/a/Snow002.
All four are CC0 1.0. Original credits and licenses remain in their
assets_itch/textures source folders. Changes: 1K JPEG sources packed into the
runtime 2K container, linear-light colour mips, normalized normal mips,
OpenGL-to-DirectX conversion and AO/roughness/height packing. Upsampling provides
format compatibility, not additional source detail. Source hashes and credits
are recorded in assets/terrain/pbr/manifest.json.


## Retained bark candidates (not assigned)

Bark Brown 01 and Bark Brown 02 by Rob Tuytel / Poly Haven, CC0 1.0.
https://polyhaven.com/a/bark_brown_01 and https://polyhaven.com/a/bark_brown_02.
Original maps and license: assets_itch/textures/tree_bark. Runtime import packs
AO/roughness/nonmetallic maps and retains native diffuse colors and DX normals.
These candidates were rejected and are not assigned to runtime trees.
Active broadleaf bark uses the developer-supplied BarkPoplar001 set under
assets_itch/textures/bark. Its original license was not supplied in that folder.
Runtime conversion and source hashes: assets/models/tree-bark-materials.json.

## Wooden Lantern 01

Wooden Lantern 01 by James Ray Cock / Poly Haven, CC0 1.0.
https://polyhaven.com/a/wooden_lantern_01
The supplied Patch repack restores glass alpha, includes LODs and moves the pivot
to the bottom centre. Our importer bakes node transforms at authored scale,
converts normal maps to the runtime convention, and adds a steady flame insert.
Source credits: assets_itch/props/wooden_lantern/CREDITS.md. Generated model
credits, licence and source hash accompany assets/models/wooden-lantern.json.
