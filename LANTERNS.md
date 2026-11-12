# Wooden Lantern 01

Hold Q in Sandbox, then click **Wooden Lantern** to spawn it at the last world
cursor position. Scroll the item list to reach Cable. Cable settings have their
own footer. The server checks the spawn ray and clearance, and allows eight
lanterns per location within the 64-prop limit. Pending gate arrivals and returns
reserve capacity too.

The model is James Ray Cock's **Wooden Lantern 01** from Poly Haven, released
under CC0 and repacked by Patch. Its supplied credits and licence are copied
beside the generated model. Body, door and handle are baked into one rigid prop
at their authored scale: 0.529 m tall. The asset's bottom pivot is offset by
`(0, 0.26555, -0.00012)` metres to the physics origin. `LanternAsset` defines the
same offset, housing/handle collision sections, and interior light position.
Mass is 2 kg. Physics-gun controls, freezing, cable ownership, save records and
gate travel use the existing prop lifecycle.

Opaque surfaces write the G-buffer. Glass retains its supplied alpha and PBR
maps and renders after water, with depth testing, alpha blending, and no depth
writes or refraction. The added teardrop flame sits above the authored candle
and contributes steady HDR emission and existing bloom. Packed AO is unused.

The lamp is always on: linear RGB `(1, 0.55, 0.25)`, intensity 4, source radius
0.04 m, and range 5 m with smooth zero attenuation at the boundary. Its light
uses the presented prop transform. Deferred world and weapon lighting share
the same point-light response; water and glass call the same helpers. Direct
lamp light is independent of surface AO and the gate-light toggle. It adds no
volumetric scattering.

`PropDefinition.CastsShadows` is false for Lantern. The actor shadow path checks
this before drawing either models or loading proxies, so all lantern parts are
excluded from sun, gate and point-light shadows. Receiving shadows, opaque
camera depth, AO, picking and physics remain enabled.

Point shadows use six faces per light and separate cached static/dynamic atlas
regions. Face resolution is 128/256/512 for Low/Performance/High. Atlas storage
tracks the number of contributing lamps and is released on removal, travel,
resize, shadow disable, or renderer shutdown. Light volumes are culled instead
of lamp meshes. Geometry edits, doors, model uploads, moving lights, props,
characters, cables and nearby tree wind invalidate relevant caches. Tree shadows
use the renderer's existing mesh caster representation. Shared helpers preserve
the separate gate light budget. Global shadows off leaves the lamp illuminated.

Regenerate the model and menu image:

```powershell
python tools/import_lantern.py
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' -b --python tools/render_lantern_thumbnail.py
```

The normal asset cooker includes the generated manifest, textures, meshes and
thumbnail. Match client/server builds and assets: protocol 32, updated content
identity and managed collision-source identity. No database migration or native
bridge change is required.

## Harness

The `lantern-lighting` Sandbox recipe spawns three lamps using normal validated
requests and captures a fixed view at night and noon with AO on/off, followed by
night captures at all three presets. Gate lighting is disabled for these captures.
Use a fresh Sandbox session with capacity for three lamps. The recipe leaves
the lamps available for further inspection; normal `run` session cleanup applies.

Harness status exposes `pointLights.count`, `shadowFaceUpdates`, `targetBytes`,
`shadowMs` and `lightingMs`. Lighting timing covers the additive deferred lamp
passes for world and weapon; water/glass remain included in their composition
timings. The `shadows` rendering setting controls the global shadow switch.

Manual checks: inspect walls and offscreen blockers inside 5 m; use the physics
gun to rotate/freeze lamps; attach cables; compare sunlight and another lamp's
light to confirm the lantern never casts a shadow. Check glass, water, foliage,
weapon lighting, multiplayer, save/reload, gate travel, limits and window resizing.

Implementation generated the model and thumbnail only. Builds, harness runs,
profiling and playtesting are left to the developer.
