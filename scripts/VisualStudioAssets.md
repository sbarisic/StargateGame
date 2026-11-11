# Visual Studio Server + Client

Select **Server + Client**, then Start/F5. The client project checks the incremental
asset cooker before a normal Visual Studio build and copies the active category
packs and catalog into `assets/packs` in its output. Normal edits publish a small
development override instead of rewriting the base packs. The client profiles
mount `artifacts/cooked/dev-override` from the repository working directory.
The client launch profile connects automatically and retries connection refusal
for up to 90 seconds while the server starts. Protocol, authentication and save
errors still fail normally.

The first vegetation bake can take several minutes. Each successful tree and
canopy stage saves its input/output hashes in `artifacts/cooked/bake-stages.json`.
Unchanged stages are reused; changed inputs and damaged or missing outputs rebuild.
If a later stage fails, completed stages are retained for the next attempt.
Long-running stages report elapsed time every 15 seconds. Simultaneous builds
serialize access to the cache. Failed cooks stop the build and leave the previous
runtime override manifest active. Source assets are preserved.

The two broadleaf canopies use original source extractions, not the reduced game
meshes. The default broadleaf intentionally has an empty canopy folder ID. Supply
each extraction in `artifacts/cooked/canopy-sources/<model>/`, or map model names
to existing extraction directories in `artifacts/cooked/canopy-sources.json`:

```json
{
  "MoreRealisticTree_01": "D:/path/to/tree-volume",
  "MoreRealisticTree_02": "D:/path/to/tree-volume-broadleaf-2"
}
```

The directories must contain `extraction.json` and its GLB files. Generate them
using `tools/extract_tree_prototype.py` as described in `TREE_PROTOTYPE.md`.
Source routing is checked before expensive preparation starts. A source change
requires refreshing its extraction; the pipeline never substitutes a different
geometry source silently.

Visual Studio's fast up-to-date shortcut is disabled for this client build so
F5 checks assets even when C# files have not changed. MSBuild compilation and
output copying remain incremental. Design-time builds do not cook assets.

Python must be available with the dependencies in `tools/requirements-assets.txt`.
If dependencies are missing, build output includes the installation command.
No packages are installed automatically.

For command-line builds, opt in with `-p:CookAssets=true`. Other builds retain
their existing behavior. Supported MSBuild overrides:

- `AssetPython`: Python executable path (default `python`).
- `AssetCookCache`: shared cooker cache directory.
- `UseLooseAssets=true`: explicit loose development mode; disables the automatic
  VS cook and removes the cooked catalog/package/required marker from that build
  output. Launch with `--loose-assets` and remove `--dev-assets` for this mode.
- `CookedAssetPackage`: use a supplied legacy SGA1 package without invoking the VS
  cooker. Remove `--dev-assets` when using a legacy package.
- `CookedAssetPacks`: supplied category-pack directory. Use `CookAssets=false` to
  build against it without cooking.

Keep the server and client enabled in Configuration Manager. The default paired
profiles now use the dedicated development universe described below.

Run bake-resumption and default-canopy routing checks with
`python tools/test_bake_stages.py`, and pack/override checks with
`python tools/test_asset_packs.py`.

## Generated starting planet

The default paired profiles connect on 127.0.0.1:17777 to the persistent
`dev-planet-generator` universe with developer controls enabled. F4 opens the
Planet Generator. Named **Gate pair fixture** profiles preserve the earlier
fixture; select matching profiles on both projects when using it. See
[PLANET_GENERATOR.md](../PLANET_GENERATOR.md) for controls and saved-map reset
behavior. The default client no longer forces time of day.
