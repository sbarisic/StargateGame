# Asset packs and streaming

## Category-pack migration

The client now accepts a version 1 `assets/packs/catalog.json` that mounts independently
readable SGA1 archives. Mounting checks indexes and ownership without decoding payloads.
The legacy single `assets/models.sga` package remains supported. Sources and licenses stay
in their existing directories; generated content belongs under `artifacts/cooked`.

The incremental entry point is:

```powershell
python tools/build_asset_packs.py --root . --cache artifacts/cooked
```

The first build publishes a base catalog and category archives. Subsequent development
builds leave those archives alone and atomically replace
`artifacts/cooked/dev-override/manifest.json`. Start the client with
`--dev-assets artifacts/cooked/dev-override`; the path is relative to the launch working
directory. `--loose-assets` and `--dev-assets` are mutually exclusive.

Vegetation preparation checkpoints each completed tree/canopy stage in
`artifacts/cooked/bake-stages.json`. Later failures do not invalidate completed
stages. Each checkpoint verifies the current input hashes and generated outputs;
long-running bakes emit progress every 15 seconds. Source-derived broadleaf
canopies use their original extraction directories, configured as described in
`scripts/VisualStudioAssets.md`, including the default canopy's empty folder ID.

Payloads are immutable SHA-256-named files. The manifest contains the latest complete
replacement descriptor for each changed asset and tombstones for removed entries.
Unchanged dependencies resolve from mounted packs. Mount precedence is development
override, declared patches in order, then base packs. Cooker/base incompatibility is an
error with a rebuild instruction. Failed cooking leaves the published manifest intact.
Old blobs and archives are retained because running readers may still reference them.

Static model replacements load asynchronously and swap before drawing. Changes involving
collision, skeletons, deleted loaded models or other unsupported live-resource types
currently require restarting the client. This conservative boundary preserves the running
session rather than changing gameplay data under it.

`--release` publishes clean category packs from the authoritative inputs and incremental
cache. `scripts/Publish.ps1 -Client` builds a candidate, verifies every payload, and keeps
the previous client directory until validation passes. Only archives listed by the active
catalog are copied; the development overlay is not a release input.

Cooked model textures include independent BC7 colour/emission and BC5 normal mip blocks,
plus RGBA compatibility mips. FishGfx selects supported storage; model normals already
reconstruct the third component from RG. A complete small mip chain becomes usable before
a larger allocation replaces it. Terrain palette payloads are split into independently
readable mips. Colour, normal and alpha-cutout semantics remain distinct in cache keys.

Two preparation workers share bounded block staging. Upload leases retain their reservation
through the upload, including cancellation. Model and terrain admission share a 2 ms frame
budget; texture and geometry writes are split into 256 KiB pieces. Static model vertices stream directly from a staged packed buffer; modular instances share that mesh. An indivisible driver operation may
exceed that budget and is recorded. Optional atmosphere, grass and AO shaders are requested
by rendering and admitted after the first playable frame. Program binaries are cached by
expanded shader source and driver fingerprint, with source compilation fallback.

Model residency includes skeletal geometry, textures, shared meshes and canopy allocations, with
256/512/1024 MiB targets for Low/Performance/High. Unreferenced resources are reclaimable;
canopy admission selects a complete, smaller mip chain when capacity is limited.
Requested crowns remain usable instead of reverting to old leaf meshes.
These are currently soft targets for essential models, not a universal hard VRAM cap.

### Validation and remaining migration work

`tools/test_asset_packs.py` covers no-op cooking, category isolation, failed publication,
tombstones, compatibility and clean release contents. `tools/test_texture_compression.py`
checks BC5 channel layout and mip extents. `--vegetation-only` includes runtime mount,
patch precedence, snapshot replacement, cancellation and upload-lease checks.

The implementation is not yet the complete startup plan. Generic CPU decode jobs and legacy
terrain decoding still need full staging accounting. Indexed static geometry, full node/pivot import contracts, pack-backed audio
and UI fonts, more general visual hot replacement, and full residency admission remain
to be completed. Existing model IDs and authoritative collision are unchanged.

The fresh category-pack publication verified all 20,013 indexed blocks. A no-op development
cook reused 9,982 jobs in 6.35 seconds, with zero cooked jobs or rewritten archives. Editing
the landscape probe shader rebuilt one job and only the bootstrap archive. The packaged
client completed a 25-second graphics-lab run with quality changes and GPU readback checks
for BC7, BC5, packed/streamed meshes and terrain material maps. It reached its first playable
frame at 3.48 seconds and ended with 449.9 MiB model residency against the 512 MiB target,
no pending model jobs and zero staged bytes. The observed worst frame was 230 ms; indivisible
shader/driver work still causes spikes. These are warm development-machine observations,
not a matched before/after benchmark. Audio was disabled for this graphics validation.

Evidence is stored locally under `artifacts/asset-packs`. The legacy-pack smoke tests reached
playable state, resized, paused/resumed and captured rendered output. A warm-cache run reached
its first playable frame at 3.68 seconds. This is not a matched cold-start benchmark or a
claim that the full plan has met its performance acceptance criteria.

## Legacy model package and vegetation imports

The SGA1 package contains indexed independently readable blocks with decoded
sizes and SHA-256 checksums. Offline Brotli is used only when smaller than raw.
Geometry streams, precomputed bounds, material roles, normal/colour semantics,
texture mip chains and skeletal arrays are cooked together. Texture payloads are
deduplicated only when their sampling semantics also agree. Publication verifies
all blocks before replacing the previous package atomically.

Static models, skeletal models and imported ground foliage use the shared provider.
Two background workers prepare bounded data (64 MiB including queued work).
GPU creation stays on the render thread. Static scenery and building dependencies
load by active region; far tree variants can appear before detailed variants.
Travel readiness requires nearby terrain and authoritative collision; decorative models can use proxies. The existing speculative upload
budget remains 0.5 ms/frame. Driver upload calls cannot be interrupted mid-call;
skeletal first-use GPU construction and some essential startup work are still
synchronous. The legacy SGA1 texture path uses uncompressed GPU storage; the category pipeline above adds BC storage. Neither format promises a fixed loading time.

Release publishing: `scripts/Publish.ps1 -Client` runs the vegetation bake and
asset cooker, then verifies the published package. `-BuildCache`, `-OutputDirectory`
and `-ReuseCooked` support reuse of a verified cook. Releases write
`assets/cooked.required`; missing/incompatible/corrupt packages fail clearly.
`--loose-assets` is the explicit development fallback. Keep source/importer outputs.

Reproduce the textures from the original extracted PNG downloads:

```powershell
python tools/import_grass_blades.py '<source folder containing Foliage008_2K-PNG maps>'
python tools/import_meadow_ground.py '<source folder containing Grass005_2K-PNG maps>'
python tools/prepare_vegetation.py --output '<cache>/models'
python tools/cook_assets.py --assets assets --vegetation '<cache>/models' --output '<cache>/models.sga'
```

Sources: [Foliage008](https://ambientcg.com/a/Foliage008) is a photometric grass
blade scan; [Grass005](https://ambientcg.com/a/Grass005) is a procedural ground
material with bitmap elements. Both use [CC0](https://docs.ambientcg.com/license/).
The blade importer unwraps seven individual blades into strips, preserving colour
and normal detail on curved geometry. The ground importer applies a fixed linear
RGB gain of 0.82/0.90/0.90 and a material gain of 0.40, packs maps and generates filtered mips. The original
terrain importer preserves this separately imported meadow record on rebuild.
Source and output hashes are retained in the adjacent provenance/manifest files.

No world regeneration, saved placement changes or protocol changes are required.

More Realistic Tree 01 has a near-only 99,999-triangle refinement (19,999 wood,
80,000 leaf triangles), replacing its 50,000-triangle close view. The retained
base model still supplies the same distant cluster bakes; collision, bounds,
planting origin and scenery IDs are unchanged. The refinement keeps 40,000 leaf
cards and 400 branch components, with less leaf enlargement than the old model.
The original Nicholas-3D attribution and CC BY 4.0 license apply.

Reproduce with Blender's background mode running `tools/refine_tree_detail.py`
and `-- --output <cache>`, then `python tools/import_tree_detail.py --source <cache>`.
The importer adds an optional `_detail.json` near-view asset; the vegetation bake
uses it only for level zero. Run the normal asset build after importing, or use
`prepare_vegetation.py --only MoreRealisticTree_01 --output <existing cache>/models`
followed by the cooker for a targeted update. Source/output hashes are recorded
in `assets/vegetation/tree-detail-provenance.json`. The comparison is a Blender
studio preview, not an in-game performance measurement.

## Loose vegetation mip preparation

`UseLooseAssets=true` now includes generated vegetation from
`$(AssetCookCache)/models` (normally `artifacts/cooked/models`). Prepare that
folder with `python tools/prepare_vegetation.py --output artifacts/cooked/models`
if absent. The build runs `tools/prepare_leaf_mips.py` incrementally and copies
coverage-preserving sidecars; alpha-cutout textures no longer use driver mips.
Cooked releases keep their existing texture descriptors and mip blocks.

Tree canopies contribute checked volume slices to the package. Normal worlds now
load the species they need; the graphics-lab inspection uses the same resources.
See [TREE_PROTOTYPE.md](TREE_PROTOTYPE.md) for reproduction and limitations.

The inspection canopy descriptor is now version 2. Its normal texture alpha
stores density at the descriptor's reference distance. Recook with
`tools/cook_tree_prototype.py` before rebuilding `models.sga`; the loader rejects
version 1 to prevent mismatched density/shader interpretation. The two-texture
allocation remains 146.3 MiB.

Inspection canopies can now live in species subdirectories beneath
`vegetation/prototype`. The cooker packages every `canopy.json` and its referenced
slices. Runtime species selection releases the previous volume before loading
the next; the prepared-data queue and upload budget are unchanged. The four
prototypes together add about 19.3 MiB to the compressed archive compared with
the original one-tree package. Single-tree inspection allocates only the selected
volume. `--tree-forest` shares all four volumes across the grove (475.4 MiB total),
loading them through the existing upload budget and releasing them on layout changes.

Inspection canopy descriptor version 3 adds shared RG8 hemisphere visibility.
`python tools/bake_canopy_ambient.py` upgrades retained cooked density fields without
reimporting geometry; the source canopy cooker also invokes this step. Eight fixed
rays per hemisphere integrate optical density, with the source cell excluded.
The maximum grid axis is 64; mip levels average linear visibility. The cooked
package includes every block. Runtime checks encoding, dimensions, byte counts
and SHA-256 before uploads. Version 2 is rejected with a recook instruction.
All four species add 1.86 MiB total GPU visibility data, shared by all instances.

The normal-world registry covers ten leafy variants, including winter/summer
birch. Each species shares one volume across all its placements. All visible
foliage uses custom canopies, including distant trees. Old leaf meshes remain
only as invisible shadow/shelter proxies or in explicit `--trees mesh` comparison
mode. Required canopies upload through the existing model budget.
Sixteen slices are prefetched ahead (at most 4 MiB), within the existing two-worker,
64 MiB preparation queue. Changing location cancels uploads and releases volumes.
The expanded verified SGA package is 1644.1 MiB; this is compressed disk size, not
GPU residency. Full 256-cube inspection species require 146.9 MiB including
canopy AO; normal-world uploads cap the axis at 128 (18.9 MiB) or 64 for Low,
selecting smaller complete chains under memory pressure. The narrower spruce
variants cost less. No canopy texture GPU compression added.
