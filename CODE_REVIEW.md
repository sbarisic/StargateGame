# First-party review and readability pass — 10 November 2026

This pass combined a repository-wide static review and mechanical cleanup with
manual review of rendering/loading, DHD ownership, gate travel, persistence,
streaming and resource lifetime. It is not a claim that every possible bug is
excluded. Third-party libraries, native dependency sources and saved data were
left alone. Existing prop-emergence work was retained.

## Fixed findings

| Finding | Change |
|---|---|
| DHD session reach ignored entity rotation and measured only triangle vertices | Query the closest point on each pressable triangle in DHD-local space, with rotation, scale and finite-input validation. |
| A pending prefab model delayed all building-state changes, including scenery removals and doors | Apply available state immediately; refresh missing prefabs when the loaded-model set changes. |
| Async model arrival invalidated sunlight cascades but could leave gate shadows and weather shelter stale | Invalidate those caches when a model completes. |
| Travel crossing samples survived physics epoch changes or unloaded locations | Clear the location's observation history on epoch changes/unload and prune disconnected players. Fresh observations cannot sweep from stale poses. |
| Kawoosh timing samples accumulated indefinitely when an opening hit nothing | Bound the diagnostic sample list. |
| Loose leaf textures used driver-generated mips while releases used coverage-preserving mips | Generate loose sidecars with the exact release filter and upload their explicit mip levels. |
| Explicit loose builds omitted generated vegetation models | Copy the prepared vegetation directory into the loose output. |
| Imported building parts ignored alpha cutoffs in colour and depth passes | Bind the part cutoff and use its alpha texture in shadow rendering, then reset material state. |
| An unused synchronous model loader could bypass the upload scheduling path | Remove it; model requests continue through the existing background preparation and render-thread upload queue. |

## Readability changes

- Added `.editorconfig` for consistent first-party C# and Python layout.
- Expanded compressed C# statements and blocks with an SDK Roslyn tool, checking
  syntax equivalence after reparsing every rewritten file. Applied `dotnet format`
  to explicit first-party file paths, excluding dependencies and generated output.
- Formatted first-party Python tools with Black, which checks AST equivalence.
- Separated leaf-mip loading and tree inspection from the main renderer.
- Extracted the travel ownership-commit operation and documented its reservation
  order. Wire names, serialized members, enum values and simulation rules were
  not renamed.

The physics compatibility hash includes simulation source text. Both packages
must therefore be updated together even though formatting does not change the
simulation. Terrain generation settings/version and protocol 19 are unchanged.

## Focused validation

- Release client and server/test builds completed. The test project retains an
  existing nullable warning in `GalaxyChecks`; application builds are warning-free.
- Interaction: 160 checks, including rotated/scaled/translated DHD reach.
- Travel: 149 checks; emergence: 37 checks.
- Two-client gate-pair checks passed, including player travel, receiving prop
  emergence, closure and the hard connection limit.
- Vegetation/cooked loading: 52 checks.
- Offline checks passed for alpha coverage, normal mips, border padding,
  reproducible package blocks, incremental-cook invalidation/failure handling,
  canopy dimensions/resource limits, trunk LOD bounds and byte-identical loose
  versus cooked leaf filtering.
- Inspected current, improved-trunk and volume screenshots. See
  [tree prototype findings](TREE_PROTOTYPE.md). Broad performance testing was
  deliberately omitted.

## Remaining review work

The renderer and server still contain large coordination methods. Continue
extracting them only when changing their behaviour, with the corresponding
regression checks. Full lifecycle stress, all-preset/night tree captures, a Linux
rebuild, and a new exhaustive multiplayer/network campaign were not performed in
this pass. None is represented as completed validation.

Windows delivery completed: `artifacts/publish/client-win-x64` and
`artifacts/publish/server-win-x64`. The final cooked package verified all 7,302
blocks. A capture from the packaged volume path completed without render errors;
its screenshot is `artifacts/review-evidence/trees/Volume-1080p.png`.
