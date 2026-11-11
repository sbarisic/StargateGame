# Source repository and local assets

This repository contains the game code, shaders, scripts, tests, documentation,
small configuration/bounds/provenance files, and native runtime dependencies.
It does not contain models, textures, audio, fonts, cooked packs, captured evidence,
or generated model/collision payloads. The exclusion includes triangle arrays
stored in JSON prefab catalogs and DHD collision data, not only `.glb` files.
Git LFS is not used: the asset payloads do not belong in this repository's history.

The migration starts with the GitHub repository's initial commit and adds the
current working source snapshot. The previous asset-bearing history is not an
ancestor. On the original development machine the old history is retained under
local `refs/archive/` references, and local assets remain in place and ignored.
Do not push archive refs or use `git push --mirror`.

## Existing development checkout

Continue using the existing solution and asset paths. Models were not deleted
or moved. Normal `git add` ignores the runtime payloads. Before committing, run:

```powershell
python tools/check_source_repository.py
```

The same index check runs on GitHub pushes and pull requests. It rejects model
formats, media, generated output, unexpected asset JSON, and blobs over 5 MiB.

## Fresh checkout

```powershell
git clone https://github.com/sbarisic/StargateGame.git
cd StargateGame
git submodule update --init thirdparty/FastNoiseLite thirdparty/LayerProcGen
./scripts/Restore-LocalAssets.ps1 -SourceRoot E:/Path/To/ExistingAssetCheckout
```

The restore command copies only missing files covered by the ignore rules. It
does not overwrite tracked source or existing local assets. A full restore copies
several GiB; use an existing populated checkout when disk space matters. No asset
download or external hosting is configured by this migration. Obtain the required
local assets separately and retain their licenses.

For compilation and headless checks only, `-MetadataOnly` restores the excluded
embedded collision/prefab data, without copying rendering models or textures.
Rendered gameplay requires the full asset set. Building from source does not make
those assets public: build outputs and native binaries containing embedded data
remain ignored and must not be committed.

```powershell
./scripts/Restore-LocalAssets.ps1 -SourceRoot E:/Path/To/ExistingAssetCheckout -MetadataOnly
dotnet build StargateGame.sln -c Release -p:CookAssets=false
```

Open `StargateGame.sln` in Visual Studio after restoring full assets. Its incremental
asset cooker uses the local library. See [TESTING.md](TESTING.md) for local scenarios.

## Dependencies

FishGfx and FishUI are source snapshots in this repository so the local graphics
fixes are included. Their MIT licenses and upstream revisions are recorded in
[thirdparty/SOURCE_SNAPSHOTS.md](thirdparty/SOURCE_SNAPSHOTS.md). Their sample
models/media are excluded too. This migration does not push changes to those
separate repositories.

FastNoiseLite and LayerProcGen remain pinned source submodules used by normal
builds. Jolt and joltc retain their upstream gitlinks for native rebuild workflows.
Initialize those additional submodules when needed. The small game-owned native runtime libraries
and GLFW dependency are retained; they are executable dependencies, not artwork.
