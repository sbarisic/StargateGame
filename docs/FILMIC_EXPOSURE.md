# Filmic tone mapping and exposure

AgX replaces the fixed ACES-style curve. Rendering remains linear HDR until
exposure and tone mapping; sRGB encoding happens once, before FXAA and UI.
The weapon and bloom use the same exposure as the world. Existing viewmodel
coverage still masks hidden gate emission before bloom blur.

## Controls

Open the pause menu and select **Filmic / exposure**, or press **J** while paused.
Within that panel, **Space** switches automatic/manual exposure, **Left/Right**
adjust by 0.1 stop, **Backspace** restores defaults, and **J** returns to pause.
Mouse buttons provide the same controls. Exposure ranges from -2 to +2 stops.
Positive values brighten; negative values darken. UI brightness is unchanged.

The default is automatic exposure with zero compensation. In manual mode,
the selected number directly controls exposure. These are relative scene stops,
not physically calibrated EV100 values.

Settings are saved in `%LOCALAPPDATA%/StargateGame/tone-mapping.json`.
Startup arguments override the saved values without saving those overrides:

```
--exposure auto --exposure-ev 0
--exposure manual --exposure-ev -0.5
```

Changes made through the menu are saved. The old tone mapper is not retained.

## Metering and lifetime

The meter samples the world after water, fog, snow, dust and gate spray, but
before placement/debug overlays, the viewmodel, bloom blur, and UI. It uses
160x90 pixels, four samples per pixel, centre weighting, and quarter-weight sky.
Sample log2 luminance is clamped to [-10, 4] to bound dark voids and highlights.
Weighted sums reduce through 40x23, 10x6, 3x2 and 1x1 RG32F targets.
The odd-sized edges retain their actual weights.

Two 1x1 RGBA32F targets carry current display EV, target automatic EV,
uncompensated automatic EV and history validity. Adaptation stays on the GPU.
The reduction and history targets together use approximately 121 KiB,
excluding driver allocation overhead, and survive resizing and quality changes.

Day/night settings blend smoothly between sun elevations -6 and +15 degrees:

| Setting | Day | Night |
| --- | --- | --- |
| Target luminance | 0.18 | 0.045 |
| Automatic EV limits | -1.5 to +1.5 | -2.0 to +0.5 |

Compensation is added after those automatic limits. Brightening uses a
2.5-second time constant; darkening uses 0.7 seconds, with a 0.05-stop dead band.
Elapsed real frame time drives adaptation even in a fixed-animation inspection.
After a long stall, adaptation advances by at most 0.1 second per rendered frame.

Visible gate opening/closing phases hold adaptation. Transit white also holds
existing history. Planet replacement invalidates history, allowing the first
valid destination measurement to initialize under white. Initial exposure stays
neutral until terrain and atmospheric lighting exist; missing or nonfinite
measurements retain the previous value. Gate flashes defer first initialization.

F3 and the exposure panel show current/target EV, hold state, meter GPU time and
tone-map GPU time. CPU exposure readback is queued at most twice per second only
while either diagnostic view is visible. A PBO/fence is polled with zero timeout;
there is no synchronous pixel readback. Readback buffers are disposed after use
or planet reset. Renderer disposal releases all exposure resources.

## Atmosphere

The exposure multiplier is colour-neutral. Existing golden temperate sun,
elevation-dependent sunrise/sunset spectrum, cool night fill, weather and sandy
desert dust remain the lighting sources. Winter indirect light receives a
(0.92, 0.98, 1.06) tint with luminance preserved. This affects shared sky fill,
ground bounce, cloud ambient lighting and fog; direct sunlight is not tinted.
Fallback fog uses the same tint. Tree material settings are unchanged.

## Review in game

No build, tests, captures or game launches were performed for this change.
The user will review:

- Sunny clearing to forest shade to interior and back outdoors.
- Noon, sunrise, sunset and night on temperate, desert and icy planets.
- Turning between sky and ground without sudden exposure pumping.
- Gate flashes and transit, then aiming/reloading in front of lit chevrons.
- Stable UI brightness, readable dark foliage and smooth highlight roll-off.

Added exposure/tone-mapping GPU cost targets <=0.2 ms at 1080p Performance.
The overall frame-p95 target remains <=8.33 ms. Neither target has been measured
for this change. Tone-map timing includes the replacement pass itself, so a
future comparison must subtract the previous tone-map cost to find added cost.

## Attribution

The AgX approximation and colour-space matrices are adapted from the
[Three.js tone-mapping shader](https://github.com/mrdoob/three.js/blob/dev/src/renderers/shaders/ShaderChunk/tonemapping_pars_fragment.glsl.js),
read on 2026-11-10. Upstream credits Filament and Blender. The adapted shader
keeps the full Three.js MIT notice in `assets/shaders/agx.glsl`.

This is the analytic AgX approximation, not Blender's complete OCIO pipeline.
Exposure is applied by the game before the transform; no extra AgX look is used.
