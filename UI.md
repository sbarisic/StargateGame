# Amber expedition interface

The client uses the existing FishGfx UI pass and Consolas font. `UiTheme` owns colours,
`UiLayout` owns logical rectangles and compass mathematics, and `UiPainter` owns one
reusable mesh for clipped panels. Rendering and pointer tests share those rectangles.
The HUD reads current session state; it does not create health, stamina, ammunition,
or resource counters. The rifle silhouette is generated from the existing rifle FBX
by `tools/render_ui_rifle.py`; its provenance is beside the PNG in `assets/gui`.

Windows content scale determines UI size, capped to retain at least 960 by 600 logical
pixels in small windows. Font positions and panel vertices snap to framebuffer pixels.
Menus reflow: the picker computes its page size from available rows and columns.
F3 diagnostics retain all original metrics and scroll with the mouse wheel when build
mode is inactive. Pause supports the existing shortcuts, mouse controls, and Tab focus
with Space activation and arrow adjustment of focused audio sliders.

Opening and closing overlays clears movement and queued jump input for that frame.
Pointer actions stay suppressed until buttons are released. Existing DHD highlighting
and controls remain; the additional DHD hover instruction panel was removed at the
user's request. Existing pickup prompts remain.

## Verification

Run `scripts/Test-Ui.ps1` for Release compilation, deterministic layout checks,
address-book regression tests, and 108 offscreen FishGfx captures. Add `-Live` for a
temporary local server and real-session captures in the existing gate-pair fixture.
The script uses an isolated build directory and an asset junction. It does not delete
saves, change networking, or migrate generated worlds.

The capture matrix covers 960x600, 1920x1080, 2560x1440, and 3440x1440 logical
viewports at 100%, 150%, and 200% framebuffer scales. These are exact-size render
targets. They validate scaling mathematics and rasterization, not Windows display
settings. Native window sizes can be constrained by the desktop work area; live logs
record the actual logical and framebuffer dimensions.

`--ui-preview --ui-check --ui-hidden` renders explicit fixture data without opening a
visible window or starting a world. Preview data never enters gameplay or the saved
address book. `--ui-interactive` enables F1 HUD, F2 pause, F3 address book, F4 picker,
F5 connection, and F12 exit in the preview. The picker is pinned open in that mode.
`--ui-record` records at most 800 live gameplay frames, or 1200 preview frames, as PNGs
at up to eight frames per second. Use a dedicated `--capture-dir`.

During implementation, Release compilation, 5,559 layout assertions and ten existing
address-book checks passed. Live gate-pair UI captures completed, with one painter mesh
and 18 cached building thumbnails across the screen matrix. Measured CPU submission
cost across mixed HUD/menu/diagnostic screens was 0.569 ms mean and 1.214 ms p95 on
the test machine; this is not a GPU overhead comparison against the old UI.

Full acceptance still requires native Windows 100/150/200% display-setting checks,
all gameplay interaction cases (particularly held-B release, connection cancellation,
pickup, and actual travel recovery), and a complete snow/desert/forest/gate-effects
walkthrough. The live check used the tree-free gate-pair fixture because parallel
vegetation exports were incomplete. The recovery screenshot exercises the real UI
controls over a fixture whiteout; it is not proof of a completed transit.
