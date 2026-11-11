# SLTerrainGeneration attribution

Source: https://github.com/acellular/slterraingeneration

Author: Michael Tuck, 2024. License: MIT (see LICENSE).

Consulted files: slterrain.h/.cpp, slhydrology.h/.cpp and utils/slmath.h,
commit 45088bf1baa8a0e7142a6c9acf0b99d9c38f92fe, accessed 2026-11-11.
The game has no native dependency on this repository.

Revision 9 integrates the approved offline terrain study into C#:

- RegionalTerrainNoise: layered Perlin/fBm, Hurst gain and power shaping.
- RegionalHydrology: D8 downhill selection, priority-flood routing and iterative
  accumulation with physical cell distances and stable tie-breaking.
- RegionalErosion: the USPED capacity/flux/divergence chain, sheet/rill alternation,
  cover feedback, filtered height updates and Strahler channel hierarchy.

Continuous gradients replace eight-direction aspects. Custom distributed runoff
reduces directional erosion artifacts. Bounded channel incision and isotropic
spreading replace the upstream ad hoc directional bank offsets. Fixed seeded
inputs replace global/random-per-iteration state. The C# seed-local permutation
differs from NumPy's prototype RNG, so matching seeds are not pixel-identical.

Broad ridge composition, coastal shaping, dunes, hillslope relaxation, habitat
selection and gameplay placement are custom. Lake levels/spills remain in
PlanetPlan. This bounded visual model is not sediment-mass-conserving and its
160 iterations do not represent geological years. No wildfire/resource logic
or native runtime dependency is imported.
