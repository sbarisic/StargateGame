# Interactive AI playtesting

Use the same local harness for scripts, menu sessions, and MCP tools. No remote
server control, desktop input injection, or arbitrary code execution is exposed.

## Prepare once, inspect cheaply

```powershell
./scripts/Test.ps1 list -Json
./scripts/Test.ps1 doctor -Json
./scripts/Test.ps1 prepare
./scripts/Test.ps1 check -Group harness -NoBuild
```

`list` bootstraps only the small TestHost/Testing projects when needed. It does
not compile the game, cook assets, or open a window. `doctor` reports missing
local assets and the intended projects without building. `prepare` explicitly
builds the rendered runtime; `prepare -Headless -Group harness` builds only the
selected headless prerequisites and coordinator. A normal `check` chooses these
prerequisites automatically. `-NoBuild` uses the exact selected build root.

The host cache is `artifacts/testing-tools`; game outputs default to
`artifacts/testing-build`. Keeping these separate lets a running MCP host prepare
the game without overwriting its own assemblies. Active game sessions prevent
rebuilding their runtime. After code changes, stop, prepare, and start again.

## Connect an MCP client

Register this local stdio command in your MCP client, replacing the checkout path:

```json
{
  "command": "pwsh",
  "args": ["-NoProfile", "-File", "E:/Projects/StargateGame/scripts/Test.ps1", "mcp"]
}
```

For a published client, use `dotnet` with arguments
`<package>/testing/StargateGame.TestHost/StargateGame.TestHost.dll`, `mcp`,
`--build-root`, `<package>`. Packaged sessions use bundled binaries; source-build
preparation requires a checkout. No AI-client configuration is changed by the game.

The host uses the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk).
Only MCP messages go to stdout; diagnostics go to stderr or artifact logs.

1. Call `game_catalog` for schemas, scenario prerequisites, and recipe verification.
2. Call `game_prepare` with `diagnose:true`; use `diagnose:false` to build if needed.
3. Call `game_start` with Sandbox options, or `game_sessions` and then
   `game_attach` with an explicit session ID.
4. Call `game_observe`; request `ui`, `physics`, `npcs`, or another section only
   when needed. Collections support offset/limit and nearby entity filtering.
5. Call `game_action` with a typed request. Poll `game_operation` using its ID;
   `waitSeconds` is capped at ten. Set `cancel:true` to stop it.
6. Call `game_capture` to receive an image and frame metadata. Set `retain:true`
   to keep evidence; transient observations use a reusable filename.
7. Call `game_stop` when done. It closes AI-owned sessions and only detaches from
   user-owned sessions. Host exit also closes its owned sessions.

## Manual handoff

An attached menu session starts observation-only. In its F6 panel, click
**AI control: OFF** once to enable control. Physical Escape cancels synthetic
input and revokes control immediately. Detaching releases the controller without
closing the user's client. Controls operate only within the selected game client.

Input requests use key/button enum names, normalized top-left pointer coordinates,
pixel mouse deltas, scroll, and optional text. Durations are bounded to 30 seconds.
`hold:true` completes admission immediately while retaining the supplied keys for
that bounded duration; use it to observe a held Q/B picker before the next input.
A replacement input, explicit cancellation, Escape, or expiry releases the hold.
For clicking a reported control, supply its `controlId` and current `uiRevision`.
Changed or disabled UI targets fail rather than silently clicking another control.

```json
{
  "action": "input",
  "timeoutSeconds": 10,
  "data": { "seconds": 10, "keys": ["Q"], "hold": true }
}
```

Observe the UI, then submit another input with `keys:["Q"]`, a short duration,
and the desired control ID/revision. Both requests run the ordinary spawn-menu
input handlers. Inventory, construction, physics-gun, and DHD interactions also
use normal input or existing gameplay commands. `aim` changes player aim;
`camera` changes the inspection view. `teleport` and resets are labeled setup.

## CLI equivalents

```powershell
$s = ./scripts/Test.ps1 start -Scenario sandbox -NoBuild -Json | ConvertFrom-Json
try {
    $action = Join-Path $s.artifacts 'action.json'
    @{ action='camera'; data=@{ bookmark='props' } } | ConvertTo-Json -Depth 4 | Set-Content $action
    ./scripts/Test.ps1 observe -Session $s.session -Sections ui,diagnostics -Json
    $op = ./scripts/Test.ps1 action -Session $s.session -Request $action -Async -Json | ConvertFrom-Json
    ./scripts/Test.ps1 operation -Session $s.session -Id $op.request -WaitSeconds 10 -Json
    ./scripts/Test.ps1 recipe -Session $s.session -Recipe sandbox-check -Async -Json
    ./scripts/Test.ps1 recipe -Session $s.session -Export -Json
} finally {
    ./scripts/Test.ps1 stop -Session $s.session
}
```

CLI `start` remains persistent and enables automation at launch. Menu-created
sessions require the F6 grant. Physical Escape always returns control to the user. Explicit `-Client`
overrides an action file; otherwise its client selection is preserved. Use a
fresh request ID for each action and reuse an ID only for identical retries.
An old generation cannot mutate a newly reset world.

## Results, replay, and disk usage

Operations report queued/running/completed/failed/cancelled/timed-out independently
of test assertions. Capture-only recipes report evidence production, not visual
correctness. Cancellation does not undo gameplay effects already committed.
Recipes release their synthetic input and pending client work on completion,
failure, or cancellation, including steps targeting a second client. A step
deadline reports `timed-out`. MCP tool errors include structured JSON details.
`assert` and `wait` support paths with equality, numeric tolerance, presence, or
distance conditions; no expressions or scripts are evaluated.

Exported recipes declare their initial scenario and reset it before replay.
Known prop/building/NPC IDs become selectors resolved against the replay world;
unbound generated IDs are rejected. UI targets resolve against the replay's
current layout. Replay reproduces recorded operations; add assertions to define
success. Physics/network timing is not a promise of bit-identical replay.

Failures retain the active request, recipe progress, relevant state, bounded log
excerpts, and an available frame. Failure frames explicitly do not guarantee
capture readiness. Execution results and lifecycle/cleanup records are separate.

On stop, compact retention removes owned world state, temporary profiles, and
transient images. Summaries, traces, requested captures, failure evidence, and logs
remain. `-RetainState` preserves disposable state for debugging. Normal saves and
shared build/content caches are untouched. For older completed sessions:

```powershell
./scripts/Clean-Artifacts.ps1 -Sessions
./scripts/Clean-Artifacts.ps1 -Sessions -Delete
```

Run focused MCP acceptance against prepared binaries with
`python tools/check_ai_harness.py --host artifacts/testing-tools/bin/StargateGame.TestHost/release/StargateGame.TestHost.dll --build-root artifacts/testing-build --rendered`.
Without `--rendered`, it checks only actual stdio discovery. The report records
startup/reset timings, observation latency/size, and retained bytes; these are
measurements, not performance targets.

Add `--control-only` to `--rendered` for the focused two-client recipe regression:
timeout, cancellation, successful completion, input cleanup, and structured
observation/capture errors. It reuses Sandbox and stops its session afterward.


## Measured acceptance baseline

Windows x64 Release, Sandbox, performance quality, one client unless the check
adds a second. These are individual measurements, not latency targets or percentiles.

| Measurement | Result |
| --- | ---: |
| Clean managed build, existing shared cooked assets | 42.14 s |
| Packaged launch with fresh session terrain state | 6.65 s |
| Warm full scenario reset, same client | 3.52 s |
| One compact observation including UI | 97.5 ms / 2,341 bytes |
| Retained playtest evidence after compact cleanup | 2,194,976 bytes |

The packaged runtime was exercised outside the checkout through a real stdio
MCP connection. Checks covered the actual spawn menu, lantern physics-gun input,
inventory equip/holster buttons, building picker, two-client replication,
cancellation, duplicate/conflicting requests, stale generations, reset, and
abrupt host exit. A recorded NPC failure was inspected, exported, and reproduced
from a fresh Sandbox, including semantic NPC targeting. Menu attachment, F6
control grant, Escape takeover, detach, and host-loss behavior were checked using
native window events on an owned test client; the driver did not move the desktop
cursor. The focused harness suite has 36 checks, and the separate MCP discovery
group launches no rendered client. Interrupted checks finalize cancelled/skipped
results. Game builds and publication require an exclusive runtime lease; live
sessions hold shared leases until cleanup.

Fresh terrain state does not imply a cold operating-system or graphics-driver
cache. The clean build measurement reuses cooked assets deliberately. Visual
captures remain evidence for inspection, not automatic visual-quality assertions.
