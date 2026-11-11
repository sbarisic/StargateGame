# Forest clearing inspection

The temperate `gate-pair` fixture contains 140 trees, with 35 instances of each existing realistic leafy species. Fixture revision 4 gives it a new cache identity. Production world generation and saves are unchanged.

Visual Studio's default client profile connects to the gate-pair fixture at 10:00. Its two nearby gates, arbitrary six-symbol addressing, flat terrain, and central physics-testing area remain available. The tree catalogue is shared by rendering, collision, interaction clearance, canopy masks, and destruction.

Woodland and low mist reuse the existing shadowed fog volume. Canopy coverage controls their density, so the clearing stays clearer. The profile only runs in the temperate gate-pair fixture and respects the volumetrics toggle and existing interior shelter suppression. Tree removal invalidates the canopy input used by materials, undergrowth, and mist.

Temperate leaves use a restrained olive albedo grade in linear colour, a rougher surface, less specular reflection, and reduced transmission. Crown occlusion affects indirect light. Bark keeps its source colour; canopy shading reduces ambient light on both bark and leaves. The actual sun direction and exposure are unchanged.

## Capture commands

Build the client/server into isolated output directories, then pass their DLL paths to:

```powershell
./scripts/Test-ForestLab.ps1 -Client 'PATH/TO/StargateGame.Client.dll' -Server 'PATH/TO/StargateGame.Server.dll' -Movie
```

The script starts an owned loopback server on an available port and stops only that process. Captures and logs go into a unique `artifacts/forest-lab-*` folder. It captures spawn, each gate, the forest edge, and the woodland with volumetrics off and on. The movie is a scripted inspection-camera walk, not a player movement or networking acceptance test.

Run `StargateGame.Tests --forest-fixture` for placement, species balance, central bounds, path clearance, flat terrain, chunk-order determinism, and canopy-removal checks. Run `--fixture-gates` separately for network-driven gate and prop travel checks.

## Evidence

- `artifacts/forest-fixture-tests.log`: ten placement/ecology checks.
- `artifacts/forest-gate-network.log`: both dialing directions, arbitrary addresses, late join, crate/barrel transfer, wrong-way destruction, unique ownership, and player transfer.
- `artifacts/forest-lab-20261109-235022/`: final olive-leaf views, volumetrics comparisons, and per-view timing logs.
- `artifacts/forest-lab-20261109-235812/forest-walk.mp4`: final camera walk, limited to the prepared near-cover area.
- `artifacts/forest-client-build.log` and `artifacts/forest-build.log`: isolated Release builds.

Timing logs report total frame, terrain, combined grass/clumps, and fog separately. They do not isolate tree and shadow GPU costs. Screenshots can add a single large frame-time spike; use the warmed p95 rather than that maximum. Fixed inspection views do not establish latency, interactive pickup, indoor building edits, or every moving-camera LOD transition as tested.
