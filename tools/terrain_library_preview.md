# Offline terrain library study

This records the **approved 2D review stage**. Its algorithms are now integrated
into the revision-9 C# regional generator; the script remains an isolated study.
`terrain_library_preview.py` writes only to `artifacts/terrain-library-prototype`.
It does not change the game's generator, caches, assets, versions or saves.

## Source and attribution

[SLTerrainGeneration](https://github.com/acellular/slterraingeneration), Michael
Tuck, MIT, pinned to commit `45088bf1baa8a0e7142a6c9acf0b99d9c38f92fe`.
The upstream license is in `thirdparty/slterraingeneration/LICENSE`.
Consulted source snapshots and their commit are saved in the output's `upstream`
directory. This Python prototype is an adaptation, not a binary-compatible port.

| Upstream method | Offline use |
| --- | --- |
| `SLTerrain::FBMGenerator`, `PerlinNoise2D` | Layered Perlin with Hurst gain, normalized octaves and power-shaped elevation. Local seeded permutations replace global random state; height and wavelength use metres. |
| `SLHydrology::fillSinksWangLiu` | Priority flood creates a spill-connected routing surface while retaining the original ground elevations. |
| D8 flow accumulation | Metric steepest descent with deterministic ties, iterative traversal and contributing-area accumulation. |
| Strahler order and channel identification | Upstream tributary orders select potential channels, including downstream confluences. |
| `SLHydrology::USPED` | Full capacity-to-flux-to-divergence erosion/deposition chain, alternating sheet/rill exponents and using cover, soil and rainfall factors. Previously the game used only the capacity formulas in routed sediment transport. |
| Terrain cover feedback | Forest/meadow/rock/plateau cover factors modulate erosion. This is an offline approximation, not the game's habitat map. |
| `blurAndOffsetUSPEDErosion`, iterative height adjustment | Spatial filtering of the erosion field and bounded seeded updates over 160 iterations. Omit arbitrary border erosion. |
| `additionalErosionDeposition` | Its channel-shaping role is retained, but direction-specific bank offsets are replaced by bounded incision and isotropic spreading. This is a custom substitute, not a literal port. |

Further deliberate changes:

- Divide slopes, flow distances and flux derivatives by the physical 8 m spacing.
- Use continuous gradients for aspect and flux divergence. Positive outward
  sediment divergence lowers the surface.
- Split hillslope runoff across downhill neighbours with slope^1.1 weights. This
  custom multi-directional extension addresses the D8 bias noted by the author;
  D8 remains available for the single-channel drainage hierarchy.
- Suppress erosion below standing basin water in this study and below sea level
  in the coastal study. Lake fill is potential storage, not a water-budget model.
- Use custom regional envelopes, plateau/coast shaping, dune geometry and limited
  hillslope relaxation. These are not attributed to the upstream library.
- Do not include wildfire, resource generation or the library's game-specific
  terrain category thresholds.

## Reproduce

Python dependencies: numpy, scipy, numba and Pillow. The current study uses an
isolated `artifacts/terrain-library-prototype/venv` with system numerical packages.

```powershell
artifacts/terrain-library-prototype/venv/Scripts/python.exe tools/terrain_library_preview.py
```

`--render-only` redraws saved height fields without repeating erosion.
`--iterations N` changes the experiment duration; use the matching value when
rendering previously generated fields.

For the baseline comparison, the first run reads the earlier seed-7319 regional
cache named in the script and saves its heights as `baseline-input.npy` beside
the experiment. Subsequent runs use that snapshot. The cache is never modified.
This is a captured baseline, not a live invocation of the game generator.

## Outputs and interpretation

- `01-comparison.png`: existing terrain, more erosion on that same terrain,
  and a new library-based starting field plus erosion.
- `02-mountain.png`: same proposed mountain field before and after erosion.
- `03-climates.png`: desert mesas/dunes and a coastal study.
- `04-drainage.png`: stream hierarchy and signed total terrain change.
- `*-height16.png`: grayscale 16-bit elevation, independently normalized to each
  map's min/max in `metrics.json`; the `.npz` height arrays contain exact metres.
- `*.npz`: initial/final height fields and separate USPED/channel changes. Desert
  final heights also contain custom dunes; thermal changes are not in USPED.
- `metrics.json`: timings, height hashes, ranges and focused checks.
- `first-pass`: retained initial D8 experiment showing the artifacts that prompted
  distributed runoff and revisions to dune/coast composition.

All maps cover 4,096 x 4,096 m at 8 m spacing (513 x 513 vertices).
Hillshade uses physical gradients, no vertical exaggeration, and NW sunlight
40 degrees above the horizon. The illustration colors are not game textures.
Blue basin areas assume filling to spill height; no claim is made that rainfall
can sustain all of them. Dry desert channels are not shown as rivers.

Focused checks cover finite heights, acyclic descending routing, outlet-area
conservation, repeatable routing and metre-based slopes. No game build or runtime
walkthrough is needed for this isolated script. This is not a calibrated erosion
simulation: clipping, channel incision and smoothing do not conserve sediment
mass globally, and iterations do not represent geological years.

Before integration, review the silhouettes and usable low-gradient areas. Remaining
work includes lake water budgets/outlets, robust channel profiles at 1 m terrain
resolution, further reduction of directional traces, habitat/settlement placement
and validation in actual game lighting. The user approved the image review before the revision-9 integration.
