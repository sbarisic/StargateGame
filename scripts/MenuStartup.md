# Menu startup

The client presents the connection menu before creating a scene renderer, opening
the cooked model index, initializing audio, or starting VS auto-connect. The
preparation status appears in cyan beneath the connection prompt.

Common resources are prepared without a dummy planet. File reads, mesh parsing,
character decoding, compatibility texture conversion, and prefab catalog setup
run on the existing two-worker asset pool. The reader retains its 64 MiB queued
block limit. GPU uploads run on the graphics thread in a 2 ms frame budget;
texture mip uploads are split into 256 KiB row strips. Individual shader or driver
calls can exceed this budget and are logged rather than hidden.

Joining runs alongside preparation. The menu remains active while the connection,
collision session, destination palette, and arrival terrain prepare. The pending
session continues to poll networking and send neutral movement. Cancel releases
the pending connection but retains common resources. Reconnecting reuses them.
Preparation errors remain in the menu; Join retries preparation. Audio failures
leave the client silent without blocking the menu. Audio initialization is
serialized on one task, with UI cues registered before world cues.

Use `--startup-log PATH` to save timestamped JSONL stages. Times start at process
creation and exclude build/cooking time. The trace includes context/UI setup,
first presented menu, asset index, shader compilation, preparation overruns,
aggregate read/decode/CPU preparation, audio, connection, arrival, and first
playable frame. Menu-frame p95/max include presentation waits and preparation.

For a menu-only capture, launch a prebuilt client with:

```powershell
dotnet PATH/TO/StargateGame.Client.dll --menu-seconds 12 --menu-capture --capture-after 3 --capture-dir PATH/TO/captures --startup-log PATH/TO/startup.jsonl
```

The existing `--connect`, `--connect-retry-seconds`, fixture, smoke, and
`--smoke-reconnects` options continue to work. No protocol or save format changes
are introduced by startup preparation.

## Validation, 2026-11-09

Isolated output: `D:\Games\Stargate Network\BuildCache\menu-startup`.
Debug and Release builds passed with zero warnings/errors. Existing development
servers and saves were not used by the fixture tests.

| Prebuilt launch | First presented menu | Other result |
| --- | ---: | --- |
| Release, initial implementation run | 746 ms | Common resources at 18.2 s |
| Release, before final upload/PNG conversion changes | 780 ms | Graphics lab playable at 18.7 s |
| Release, revised conversion/upload path | 736 ms | Graphics lab playable at 11.9 s; common resources at 9.25 s |
| Release, final menu-only run | 800 ms | Menu-frame p95 5.18 ms, max 115.29 ms |
| Debug, quit during preparation | 761 ms | Clean exit at one second |
| Debug, first measured full preparation | 782 ms | Menu-frame p95 5.63 ms, max 145.93 ms |
| Debug, repeated preparation | 766 ms | Menu-frame p95 5.88 ms, max 126.50 ms |

The fixture tests start the client before their private loopback server, exercising
the VS retry path and immediate connection during preparation. Missing and corrupt
cooked packages both showed a usable menu and an actionable preparation error,
then exited cleanly. Audio-enabled and `--audio off` launches passed. The final
menu screenshot was inspected for prompt/status spacing and colour.

The reconnect run also passed: shared assets were retained and the second join
became playable in 2.84 s. Its logs are in
`artifacts/menu-join/20261109-214412`.

Evidence lives under `artifacts/menu-join/20261109-213955`,
`artifacts/menu-layout-final`, `artifacts/menu-release-final.jsonl`,
`artifacts/debug-*.jsonl`, and `artifacts/menu-{missing,corrupt}.jsonl`.

These are launches on the current machine with existing OS/driver caches, not a
cache-cleared first-install benchmark. The earlier measurements are intermediate
implementation comparisons, not an instrumented pre-change baseline. The menu
target is met in these samples; preparation is not free of short stalls. First
world rendering still initializes destination-specific effects. Normal-world
startup, manual cancellation during each loading stage, and physically unavailable
audio hardware have not all been covered by these runtime checks.
