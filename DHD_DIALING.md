# DHD dialing and galaxy connections

Connections and one-way travel are implemented in the working build. See
[STARGATE_TRAVEL.md](STARGATE_TRAVEL.md) for capture, readiness and identity rules.
Manual ring dialing, intergalactic routes, power inventories, damage and membrane
resistance remain deferred. Open membranes retain contact ripples.

Successful reservations start opening without waiting for the receiving world.
Players and props wait at least two seconds in transit, longer if readiness needs
it. This loading delay does not delay the source kawoosh.

## Controls

- Aim at a physical DHD key within three metres and left-click.
- Enter six distinct destination glyphs, key 01 (local origin), then the orb.
- Backspace removes the last accepted symbol; hold one second to reset. Keep
  aiming at this DHD within reach. Build mode retains left-click for building.
- Press either connected DHD orb to close. Otherwise the connection stays open
  for 120 seconds; closing takes 4.083 seconds to match the shutdown cue.

Hover shows a glyph name and outline. Keys depress about 6 mm over 120 ms;
accepted glyphs stay orange and replicate to other observers. Invalid glyph input
preserves progress. A failed orb activation resets the gate, clears all selected
symbols and chevrons, and releases the dial session. This includes an incomplete
address, failed lock, unavailable/busy receiver, and destination preparation failure
or timeout. The error remains visible; enter a new sequence to try again.
Clicking a lit glyph does not toggle it. Origin locks the departure context and
never supplies a destination coordinate. Hidden overlapping key 13 is unavailable.

The first accepted key owns the session. Reset, disconnect, two seconds outside
reach, or 30 seconds without input release an unfinished session. An established
connection survives its dialer disconnecting. The orb is dark while idle, softly
lit after origin, and fully lit during preparation/opening/connection.

## Catalogue and persistence

The adopted geometry is `rda-compatible-fit-20261106-v1`, using the published
double-precision reference coordinates in light-years. Enumerating all 41,410,215
canonical pairings produces 19,020,803 valid entries. Canonicalization sorts
inside adjacent pairs, then sorts the three pairs; all 48 equivalent orders share
one key. It never sorts all six glyphs independently.

Each valid key provisions a distinct location and primary receiver. Proximity
does not merge locations. The inclusive one-light-year acquisition check applies
after retained corrections; a failed established association never redirects.
Explicit aliases are supported by the repository, with none added automatically.

The memory-mapped artifact uses 32-byte key/double-position records and a uniform
spatial index. Its complete census and SHA256 are verified. It stays server-side;
unvisited locations do not create SQLite rows. IDs derive from universe identity
and canonical key; content derives from the retained universe seed and location.

Schemas 5–6 add associations, aliases, revisioned corrections, and retained
ordinary-route capability flags. All initial receivers support ordinary routes
and are available and powered. Existing SQLite
backup API migrations retain three backups. The current world becomes home,
retaining its ID, buildings, terrain, and environment. Other developer worlds
remain separate. Subsequent starts load retained home by ID, independent of
preset/seed arguments. Materialization persists full definitions only on demand.
Unvisited clocks derive from the universe epoch. Gates restart idle.

The 35 ordinary named records use procedural terrain: Earth temperate, Abydos
desert. Land of Light, P3X-116, and P3X-984 are labelled game-authored completions.
Othala remains unsupported intergalactic metadata. Two ordinary named routes need
glyph 13 and cannot be entered on the standard DHD.

## Developer tools

Normal startup builds a missing complete catalogue once, with progress. A corrupt
or incompatible artifact fails clearly; no smaller fallback galaxy is used.
`--catalogue PATH` selects its path; `--build-catalogue` builds it and exits.
The separate tool supports cancellation and atomic publication:

```powershell
dotnet run --project tools/GalaxyCatalogue -c Release -- build artifacts/galaxy/galaxy.bin
dotnet run --project tools/GalaxyCatalogue -c Release -- verify artifacts/galaxy/galaxy.bin
dotnet run --project tools/GalaxyCatalogue -c Release -- resolve 28-26-5-36-11-29
dotnet run --project tools/GalaxyCatalogue -c Release -- named
dotnet run --project tools/GalaxyCatalogue -c Release -- benchmark artifacts/galaxy/galaxy.bin
dotnet run --project tools/GalaxyCatalogue -c Release -- explain artifacts/galaxy/galaxy.bin SAVE_DIRECTORY default 28-26-5-36-11-29
```

With `--dev-planets`, loopback clients print their return address and named routes.
F4 accepts six hyphen-separated glyph IDs; Tab selects a named route, Enter moves
only this client's session; C cancels preparation. This is developer relocation, not gate travel. Use
two clients to observe both endpoints. F3 shows gate phase, revision, owner,
connection ID, and gate clock. Server logs retain phase changes and failures.
Tab opens a persistent, universe-scoped address book of visited worlds. It shows the current world and lets players pin a glyph address in the HUD. Selection does not dial automatically. Discovery and galaxy-map UI remain deferred.

## Runtime and rendering

Protocol 7 retains the 64 KiB frame limit and bounded terrain transfers. DHD
commands carry request ID, expected revision, location, physics epoch, and aim.
The server reconstructs the ray from its player eye and checks surface, reach,
occlusion, sequence, and ownership. Duplicate requests return their original
result. Queued local input can retry transient epoch/revision refusals with fresh
request IDs. Address reads and world saves do not block the simulation tick.

Each location owns physics, terrain preparation, building changes, and gate state.
Loads deduplicate by LocationId, with two generation jobs and eight connections
maximum. Both endpoints are reserved and pinned; source players remain in place.
Preparation has a 30-second deadline and requires endpoint visual readiness.
Empty physics freezes while gate/environment clocks continue. Unreferenced
destination runtimes save and unload after a 30-second grace period.

Opening is scheduled at least 12 ticks ahead. The receiver locks seven chevrons
over 42 ticks, then runs the same four-second opening as the source. Source parts
02,03,04,07,08,09 light in order, ending with top part 01; 05/06 stay dark.
The source becomes open at +240 ticks and the receiver at +282. Closing takes
245 ticks (4.083 seconds), after pending transfers finish. Source opening does not wait for client
prefetch. Phase timestamps reconstruct late presentation without restarting cues.

The plume reaches 1.2 aperture diameters and has no collider. Its broad leading
mass contracts through a narrowing neck. Shutdown keeps the aperture size fixed,
flashes white and breaks outwards through irregular holes. Depth, bloom and nearby
light spill use the existing HDR pipeline. See [GATE_EFFECTS.md](GATE_EFFECTS.md).

`--event-horizon off` disables active gate effects. `--inspect-active-gate` enables
the old always-active render diagnostic explicitly. `--dial-check`, during a
smoke/benchmark launch, walks to the DHD, performs real validated network presses,
captures phases, and closes the connection. `--dial-address` selects its target.

See [VALIDATION.md](VALIDATION.md) for measured checks and limits, and
[STARGATE_TRAVEL.md](STARGATE_TRAVEL.md) for travel rules and validation status.

## Six-symbol destination prefetch

After six accepted destination symbols, the server resolves their canonical address
and starts destination generation in parallel. Origin and orb remain required;
prefetch neither reserves the receiver nor opens a connection. Orb activation
revalidates availability, power, address association and gate ownership, then reuses
the pending or ready destination. Invalid addresses still report their error at
the orb and follow the normal reset behaviour.

Only one speculative generation job runs at a time, leaving the second slot for
explicit activation/relocation. Speculative cache growth is capped at two empty
locations. Undo/reset releases interest; unreferenced jobs have a two-second
cancellation grace, and loaded locations use the existing 30-second reuse grace.
Destination collision starts with a 3 x 3 gate region instead of 7 x 7, plus spawn,
saved-body and saved-building coverage. Streaming expands normally when a player
arrives. Home-world startup coverage is unchanged. Server logs report prefetch
start/reuse, location-ready wall time and orb-to-ready simulation time.


## Direction-aware client preloads (2026-11-07)

Six accepted destination symbols start the dialer cache after server resolution. Orb acceptance also prepares nearby outgoing-side clients; incoming clients never preload the source. Opening does not wait for client prefetch. Completed caches remain briefly reusable through developer relocation. Travel is still deferred; see [STREAMING.md](STREAMING.md).

## Address hints and home daylight (2026-11-08)

DHD hover has no outline or glyph/control label. Picking and click validation are
unchanged. A selected address in the TAB book supplies a local white emission
hint on the physical keys. A smooth three-symbol envelope travels through the
six symbols in order, with a brighter centre and fading neighbours, then pauses
for three seconds. Keys are 0.75 seconds apart. Peak linear emission is 0.045
(one tenth of the previous 0.45 hint). Accepted keys retain their orange emission;
hints never press keys, change server state, or illuminate chevrons. They stop
while the book/build menu is open or the gate is preparing/opening/open/closing.

The retained universe home is fixed at 11:00, including existing saves and server
restarts. Other destinations retain their normal clocks. This changes only the
home environment definition, not terrain or buildings. Weather and surface
animation keep running. Developer visual-time overrides still take precedence.

## Compact dialing HUD (2026-11-08)

The crosshair area no longer displays dialed glyphs or dialing reason/status text,
including origin-lock prompts. With TAB closed, the top-right selected-address
panel shows the current accepted dial sequence in a smaller second row, including
origin. The selected world's name is right-aligned under its address. Empty dial
sequences do not reserve an extra row. Server results and diagnostic logs remain
available; this change only removes their normal HUD presentation.
