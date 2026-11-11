# Asset provenance

The Stargate and DHD meshes and source texture content come from the user's local
Stargate Network installation. They are extracted original artwork, not newly
created or MIT-licensed assets. No permission to redistribute that artwork is
asserted by this repository. This project has no configured publishing remote.

Conversion source:
`D:\Games\Stargate Network\extraction\assembled\Stargate_and_DHD.blend`

Source SHA-256:
`a5e8440f50683037bf435481f31fc47ef706ef030cb6bfdafba7afbd8d1917f0`

The assembled scene contains 21 gate mesh objects and 41 DHD mesh objects. Chevron
placements were reconstructed from the original blueprint; the DHD uses the meshes'
shared original coordinates. Preview lights, cameras, and floor are not exported.

`scripts/Export-Assets.ps1` runs the Blender 5.2 offline exporter. It opens the source
without saving it, makes temporary mesh/material copies, preserves the source UVs
for sampling, creates a new runtime UV set, and bakes reconstructed base colour
(including the source colour/AO graph) without emission or illumination. Meshes keep
their authored corner normals. World-space vertices are transformed from Z-up to
Y-up; the preview layout is removed so each assembly has its own ground origin.

For UV packing, only the temporary bake mesh is welded across glTF split vertices.
Original corner IDs transfer the resulting UVs back onto the untouched source
triangles and corner normals. This avoids thousands of isolated triangle islands.
The full gate ring and glyph track use 4096 × 4096 atlases; other parts use 2048
or 512 according to size/complexity. Runtime model textures use linear magnification,
trilinear mipmapping, and up to 8× anisotropic filtering when supported.

Each `.mesh` file starts with ASCII `SGM1`, followed by a little-endian uint32 vertex
count, then triangle-list vertices of eight float32 values: XYZ position, XYZ normal,
and UV. JSON manifests retain component names and source material names and map
parts to mesh files and PNG atlases. The manifests currently use version 1.

Base colour remains baked without illumination. Normal-map relief, layered
roughness/specularity, and Unreal's original lighting are not reproduced. Separate
chevron emission masks now enable the seven active crystal/strip materials. The
two ChevronLightOff components and all DHD buttons remain inactive.

`models/export-report.json` records source hash, assembly bounds, and part counts.
The converted files are included so build/run require no access to the original
installation. Re-exporting requires Blender and the source blend.

Terrain geometry, runtime shaders, UI layout, and placeholder player geometry are generated
by this project. Terrain albedo textures are extracted artwork as described below. The UI font and native GLFW runtime are supplied by the pinned
FishGfx dependency under its existing notices.

## Physics collision export

`tools/export_collision.py` reads the checked-in SGM1 meshes without Blender and
writes welded, indexed SGC1 triangle meshes under `assets/collision/`. It selects
the Stargate Segment and GlyphTrack plus DHD Base and Dome, omitting keys,
crystals, chevrons, and switchable glyph decorations. Degenerate and duplicate
triangles are removed. Source mesh files and the source installation are not changed.

The exported gate has 25,036 vertices / 42,079 triangles; the DHD has 4,437 vertices /
8,438 triangles. `assets/collision/provenance.json` records selected parts and source/output
SHA-256 hashes. Run `scripts/Export-Collision.ps1` to reproduce these files.
Collision uses the structural triangles; F3 displays sampled wireframes for readability.

## Terrain surface textures

`assets/terrain/` includes byte-for-byte copies of four local Stargate Network
extraction textures. They are not generated artwork or newly licensed assets;
the source artwork provenance above also applies to these textures.

| Runtime file | Extracted texture | Resolution |
|---|---|---|
| grass.png | LandscapeAutoMaterial / Grass01 / Grass0072_D.png | 1024 × 1024 |
| cliff.png | LandscapeAutoMaterial / Cliff01 / Cliff01_D_H.png | 2048 × 2048 |
| sand.png | LandscapeAutoMaterial / Sand01 / T_Sand_D.png | 2048 × 2048 |
| snow.png | PhotoR_Landscape_4 / Textures / snow_c_d.png | 2048 × 2048 |

`terrain/provenance.json` records full source-relative paths and SHA-256 hashes.
Run `python tools/import_terrain_textures.py` to reproduce the import from the
local `extraction/terrain_library/source_exports` directory, or pass `--source`.
The import does not modify the extraction. Builds and launches use the included
copies and do not require the original installation.

Terrain uses RGB albedo only; height stored in alpha is not opacity. The runtime
loader makes alpha opaque before image conversion, preserving RGB instead of
compositing packed masks into black spots. The included PNG copies remain unchanged. The shader
blends surfaces by preset, slope, and moisture, with world-space triplanar sampling,
repeat addressing, trilinear mipmaps, and up to 8× anisotropic filtering. Desert
and icy cliffs receive preset colour tints. These do not reproduce the source
Unreal materials, normal maps, or full lighting model.

The gate entity is uniformly scaled by 6.7056 / 6.127384 (about 1.0944), matching
the outer diameter in the user's supplied reference. The authored opening becomes
approximately 5.0 m; its proportions are preserved rather than reshaped to the
reference's quoted 4.8768 m opening. Embedding scales from 1.25 m to approximately
1.37 m to preserve the buried lower arc. Rendering, structural collision, and debug
wireframes share this transform; source meshes and the DHD remain unchanged.

## Daylight terrain normal maps

The same importer now includes Grass0072_N, Cliff01_Normal, T_Sand_N2, and
snow_c_n from the matching extracted terrain sets. Each runtime PNG is a
byte-for-byte copy, with its source-relative path and SHA-256 recorded in
assets/terrain/provenance.json. The shader reads linear DirectX XY normals,
flips green, and reconstructs Z; alpha is ignored. Albedo is decoded from
sRGB before lighting. Normal maps add shading detail without changing meshes
or collision. Source artwork and the existing asset restrictions are unchanged.


## Recovered rocks - 2026-11-06

Thirteen models come from the local extraction/terrain_library catalogue:
Stone_01/02/03, SM_rock_sandstone/SM_rock_sandstone2, SM_Rock_Sandstone_01-04,
and rock_ground_1-4. They retain the existing artwork restrictions above.

`tools/import_rocks.py` reads recovered glTF buffers, material inheritance, and
texture assignments. It preserves metre-scale Y-up geometry and authored normals,
centres each pivot at the bottom, and converts top-left glTF UVs to FishGfx UVs.
Runtime SGM1 meshes use recovered albedo with inherited linear colour tints baked
into sRGB PNGs. Normal maps remain linear DirectX data. Texture dimensions are
capped at 2048 pixels; alpha masks are not used.

The importer uses recovered LOD2 for Stone collision and LOD1 for mossy outcrops;
other collision meshes preserve the original triangles with degenerates removed.
No original file is modified. Every consumed buffer, glTF, material properties file,
and texture has a SHA-256 in `models/rocks-provenance.json`. Local dimensions are
recorded in `models/rocks-bounds.json` and embedded in the generation assembly.
Biome size multipliers are explicit in RockScenery; they do not alter source units.


## Recovered trees - 2026-11-06

Six recovered glTF models from extraction/terrain_library: the three
BlueSpruce_Desktop_Winter variants, European_White_Birch_Hero_Winter,
Leave-Tree-01a, and Broadleaf_Desktop (the bare sapling group). The supplied
previews are reproduced with recovered material mappings, not fallback glTF
materials. Branch_Inst2 uses Branch_D/Branch_N; Trunk_Inst2 uses Tree_Bark_D.

`tools/import_trees.py` preserves metre/Y-up geometry, authored normals, and
bottom-centred placement. It flips glTF V coordinates for FishGfx texture upload,
preserves foliage alpha, keeps normal maps linear, and caps textures at 2048px.
Frond/leaf atlases use a 0.333 cutoff; Branch_Inst2 uses 0.5, matching the preview.
Wood textures are opaque. Runtime manifests opt into repeating UV sampling.

Recovered LOD1/LOD2 are included for birch and the leafy tree. Wood-only collision
uses LOD2 where available and source wood triangles otherwise; transparent foliage
is excluded. `models/trees-bounds.json` includes every rendered LOD's bounds.
`models/trees-provenance.json` records source paths and SHA-256 hashes. Originals
are read only; the existing artwork ownership and redistribution restrictions apply.


## Procedural building kit

Building meshes are generated by StargateGame.Building. Their concrete surface
reuses assets/terrain/cliff.png, already attributed above. Metal-coloured seams
are shader-generated; no new external building models or textures were imported.

## Building plaster (2026-11-06)

Construction plaster albedo and normal maps are byte-for-byte copies from the
local Stargate Network extraction: terrain_library/source_exports/Master/temp/
Megascans/Textures/T_Construction_Plaster_D.png and T_Construction_Plaster_N.png.
Run tools/import_building_textures.py to reproduce assets/building. SHA-256
checksums and exact relative source paths are in assets/building/provenance.json.
Original files remain unchanged. Runtime uses opaque albedo, linear normal data,
DirectX green-channel conversion, mipmaps and anisotropic repeating sampling.
These extracted assets retain their original third-party rights.


## Chevron emission masks (2026-11-06)

`tools/export_gate_emission.py` reads the same assembled Blender file and existing
SGM1 runtime meshes. It verifies source triangle positions/order against the
runtime geometry, then rasterizes active source material assignments into the
existing UVs at 512x512 with two pixels of padding. It does not threshold albedo,
modify the blend, change model geometry, or include lighting in the albedo.
`export_assets.py` invokes this step after its geometry/base-colour export.

The active material names are MI_Stargate_ChevronLightTop and
MI_Stargate_ChevronLight_Master. Masks cover seven crystals and seven strip/frame
parts, excluding metal polygons and MI_Stargate_ChevronLightOff. Optional manifest
fields are emissionTexture, emissionColor, emissionIntensity and lightPosition;
missing emission defaults to zero. The seven light positions use crystal bounds
with an outward 0.08 m offset in model space. Mask/source checksums and lit-pixel
counts are recorded in models/gate-emission-provenance.json. Horizon emission
remains procedural and uses no additional extracted texture.


## DHD glyph materials

`tools/export_dhd_materials.py` reads the original assembled
`extraction/assembled/Stargate_and_DHD.blend` without saving it. Source triangle
material assignments and `T_DHD_Touhes_GyphBorder.png` source UVs map to the
existing runtime atlases. Glyph albedo becomes subdued warm metal. Glyphs and
raised upper key rims form emission masks; the housing remains non-emissive.
No colour-threshold selection is used.

Outputs: `models/dhd_*-weathered.png`, `dhd_*-emission.png`, per-key metadata in
`dhd.json`, and SHA-256 records in `dhd-material-provenance.json`. Original baked
PNGs remain untouched as explicit `sourceTexture` inputs. Repeated rebuilding
does not compound recolouring. All 39 source keys are retained; standard rendering
hides overlapping key 13. Existing asset ownership/restrictions above apply.

The main asset exporter invokes this step automatically. For a material-only
rebuild, open the same source blend in background Blender and run
`--python tools/export_dhd_materials.py -- --output assets/models`.
Blender is only required offline; normal game builds package the generated PNGs.

The central orb additionally uses `T_DHD_BigButton_E.png`, sampled through its
original source UVs and rasterized into `dhd_01-emission.png`. Only triangles
assigned `MI_DHD_Dome` emit; the same part's `MI_DHD_Base` metal stays dark.
The recovered map supplies grain and core falloff. Deterministic model-space
noise adds larger dark patches. The linear RGB map keeps dim regions red and
bright patches pale pink, avoiding a uniform yellow glow.
The source map and output mask hashes are in `dhd-material-provenance.json`.
Original dome albedo and geometry remain unchanged for its inactive state.

## Atmospheric scattering — Eric Bruneton

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

## Tree Pack 3D - 2026-11-07

Retired from active tree selection and render/collision loading at the user's
request. Source files and generated assets remain available for reference.

Five additional OBJ/MTL models are supplied locally in `assets_itch/Tree Pack 3D`.
`tools/import_tree_pack.py` reads those files without modifying them. Rebuild with
Blender 5.2 in background mode: `--factory-startup --python tools/import_tree_pack.py
-- --source "assets_itch/Tree Pack 3D"`.

Runtime names are `TreePack3D_1` through `TreePack3D_5`. Authored units are treated
as metres, with Y up and pivots moved to the trunk root. Heights are approximately
7.86, 9.17, 16.29, 19.84 and 5.11 m. The source has polygonal leaves and solid
material colours, with no texture maps. The importer authors muted bark, green
foliage and autumn foliage colour textures; it preserves the autumn assignment
on tree 2. Leaves use two-sided foliage lighting. Near meshes retain the source
geometry; two progressively simplified LODs cover middle and far distances.
Only wood enters collision. Bounds include all LODs.

`tree-pack-provenance.json` records source hashes, conversion settings, Blender
version, dimensions and triangle counts. `tree-pack-bounds.json` is a separate
archived catalogue so reimporting the recovered trees cannot overwrite it.
It remains embedded for the existing compatibility hash but is not loaded into
the active tree registry.
No license or author notice was supplied inside this pack; no redistribution
license is inferred from its presence in the workspace.

## DHD interaction surfaces (2026-11-07)

`python tools/export_dhd_interaction.py` reads the existing runtime DHD meshes
and runtime UVs. It exports `assets/interaction/dhd.sgdi`, embedded by Shared
for identical client/server picking. The 13,096 triangles retain source part names
in `assets/interaction/dhd-provenance.json`, with input/output SHA256 hashes.
Key 13 is omitted. Non-pressable base triangles remain occluders; the orb uses only
its upper emissive dome. These meshes are separate from physical collision.
The mask lookup uses `1 - v` to match FishGfx's default vertical image flip;
the mask hash and `textureFlipY` setting are recorded in provenance. This fixes
the red dome being unclickable while a small patch of its base was interactive.
The exporter does not modify source assets, albedo, emission masks, or FishGfx.

## Audio bank (2026-11-07)

`tools/import_audio.py` creates 46 recordings and 27 cues from local Stargate Network SoundWave exports and RaylibGame gameplay recordings. `audio/provenance.json` retains source/output/decoded hashes, channels, duration, loop frames and conversion settings. WAV effects are mono; FLAC ambient beds are stereo and positional loops mono, all at 48 kHz. Short edges fade; loops use a recorded offline overlap crossfade. Source packages remain untouched. SoundCue extraction exposes node names but not all properties, so gains are explicit importer tuning. See [AUDIO.md](../AUDIO.md) for the source pins, rights distinction and validation limits.

## GUI Stargate glyphs (2026-11-07)

`python tools/export_gui_glyphs.py` reads `MI_Glyph_NoGlow` triangles from the
39 source `SM_Key_NN.gltf` files and their buffers. It projects each shape onto
its fitted key plane, removes radial orientation with inward-up, preserves
handedness and normalizes its silhouette into a padded 128-pixel cell. Fourfold
supersampling produces the alpha coverage in `gui/stargate-glyphs.png`.
`gui/glyph-provenance.json` records each source part/hash and the final hash.
The runtime uses four mip levels and linear filtering for small HUD sizes.
No source geometry or images are modified. All 39 IDs are retained, including
hidden DHD key 13 for address display; ID 1 remains the existing generic origin
artwork, not an invented Earth-origin replacement. Asset rights are unchanged.

## Realistic trees (2026-11-07)

Four trees from Nicholas-3D's "Realistic Trees Pack of 2 Free" and "More Realistic
Trees Free!" are used in temperate worlds. Source packs remain under
`assets_itch/trees/`. Both are CC BY 4.0; original attribution files accompany the
runtime meshes as `models/realistic-trees-license.txt` and
`models/more-realistic-trees-license.txt`.

Rebuild runtime files with `python tools/import_realistic_trees.py --source
"D:/Games/Stargate Network/extraction/tree_optimization"`. This reads the approved
optimized GLBs without modifying them. The optimization folder retains the
original comparison, editable Blender scenes, and optimization scripts.

| Runtime model | Near | Beyond 40 m | Beyond 90 m | Beyond 180 m | Wood collision |
| --- | ---: | ---: | ---: | ---: | ---: |
| RealisticTree_01 | 50,000 | 16,699 | 8,000 | 2,999 | 1,199 |
| RealisticTree_02 | 50,000 | 16,699 | 8,000 | 3,000 | 1,200 |
| MoreRealisticTree_01 | 50,000 | 16,700 | 7,999 | 3,000 | 1,200 |
| MoreRealisticTree_02 | 50,000 | 16,699 | 8,000 | 2,999 | 1,199 |

Counts are triangles. Source Quality/LOD0/LOD1/LOD2 map to runtime base/Lod1/Lod2/Lod3.
All levels share a planting origin at the principal trunk base; long exposed roots
extend below this origin. Geometry remains Y-up in metres. UVs are preserved with
the runtime V flip; normal-map green is inverted for DirectX convention. Textures
retain leaf alpha and are capped at 2048 pixels. Roughness/metallic factors are
baked into their texture channels. Collision uses only the far-LOD wood mesh.
`models/realistic-trees-provenance.json` records source hashes, texture hashes,
counts, and planting offsets; `models/realistic-trees-bounds.json` includes every
LOD and its transition distances. The five retired Tree Pack 3D models stay inactive.
