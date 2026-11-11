# Landscape materials, distant grass and clear weather

## Scope

Implement the requested visual improvements in the existing renderer. Follow
AGENTS.md: focused builds, a few checks and representative captures;
extended movement and gameplay validation stays with the developer. Preserve
unrelated work and existing saves. No questions are required for this pass.

## Implementation order

1. **Terrain materials.** Restore rocky-rugged-terrain-pbr as the temperate meadow
   substrate, retaining its normal and displacement maps beneath mesh/volume
   grass. Inspect installed rock textures and replace pale mountain marble with
   a darker, suitably scaled rock. Keep imports, attribution and palette defaults
   reproducible, including after an asset rebuild.
2. **Grass visibility.** Extend hybrid grass beyond its current 64/112/160 m
   limits, within streamed terrain availability. Preserve near meshes, exclusions,
   roots and wind. Blend the final grass band into habitat-driven distant meadow
   shading so hills remain vegetated without ray marching individual blades to
   the horizon. Keep Mesh and Off controls working.
3. **Clear sky and clouds.** Use distinct cumulus bodies, sharper edges, bright
   sun-facing regions and darker undersides, with open blue sky between them.
   Reduce clear-day haze without removing distance cues or storm effects. Keep
   sunset color tied to sun elevation rather than applying an orange daytime tint.
4. **Targeted code review.** Review the grass, material, cloud and streaming paths
   for avoidable allocations, repeated work, stale state and obvious bugs. Fix
   demonstrated issues; do not expand into unrelated gameplay refactoring.
5. **Validation and delivery.** Build Release, run focused material/grass checks,
   and inspect a meadow/mountain view, a clear sky view and a sunset view. Record
   one short runtime sample, document fixes and remaining visual risks, and leave
   usable captures. Do not claim speed improvements without measurements.

## Acceptance

- Rocky displaced ground remains visible between grass patches.
- Grass has a farther visible volume band and a continuous distant meadow finish.
- Mountain rock is darker without crushing shadows or tinting snow gray.
- Clear weather shows a bluer sky and distinct clouds; sunset stays warm.
- The existing rendering modes, weather states and streaming cleanup still work.
- No exhaustive seed/quality matrix or manual playthrough is required.

## Implemented first pass

Materials, extended hybrid range, distant meadow shading, clearer atmosphere,
cloud changes and the targeted code review are implemented. Release builds and
40 focused checks passed. Captures, measurements and remaining visual tuning
limits are recorded in `artifacts/landscape-visual/notes.md`.
