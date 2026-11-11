# Development instructions

This is a work-in-progress game. Prioritize useful progress, clear code, and the
current design. Breaking changes are expected and frequent. The developer will
playtest regularly and report bugs.

## Development

- Change, replace, or remove existing implementations when the task calls for it.
  Compatibility with old saves, protocols, asset formats, APIs, or behavior is not
  a requirement. Avoid migration layers, compatibility shims, and legacy fallbacks
  that exist only to preserve old versions.
- Models and other assets may be modified or replaced directly as part of the
  task. Keeping backup copies is not required, including for models outside Git.
- Do not routinely back up files, models, saves, or builds before every change.
  Use version control for tracked source history. Create a separate backup only
  when requested or when there is a specific recovery need.
- Update affected callers, formats, version numbers, and documentation together.
  Briefly explain consequential breaks, such as needing fresh saves, regenerated
  assets, or matching client/server builds. Routine breaking changes and in-scope
  model edits do not need a separate approval step.
- Keep changes scoped to the task and preserve unrelated work and user data.

## Testing

- Use the unified harness in [TESTING.md](TESTING.md): `scripts/Test.ps1` for
  automation and focused checks, and Local Testing for manual playtesting.
- Implement and test everything that can reasonably be exercised in Sandbox
  there. Extend existing stations, settings, bookmarks, and recipes as needed.
- Reuse or modify existing scenarios before adding new ones. Do not create a new
  scenario when a straightforward change to Sandbox, Planet Explorer, or Travel
  would cover the case. Register headless checks without creating extra worlds.
- Prefer `Test.ps1 run` for automated startup, readiness, evidence, and cleanup.
  Use harness actions and readiness conditions instead of custom launchers or
  startup sleeps. Stop persistent `start` sessions when finished.
- Validate changes with a relevant build and a small number of focused checks.
  Scale testing to the risk, especially for persistence, ownership, and networking.
  Keep visual checks to representative scenes or captures.
- Once relevant checks pass, move on. Repeat or broaden testing only for a new
  change, failure, unresolved concern, or explicit request. Avoid routine full
  matrices, long benchmarks, large capture sets, and repeated full-suite runs.
- Leave extended playtesting to the developer. Report what was checked and any
  important uncertainty that remains.

## Disk usage and cleanup

- Watch disk usage. Check available space before large builds, asset cooks,
  exports, or captures, and avoid filling a drive with generated output.
- Reuse existing build directories and incremental caches. Do not create another
  full build, asset copy, package, backup, or test run unless it serves the task.
- Generate only the logs, captures, recordings, and reports needed to verify or
  explain the change. Do not accumulate redundant artifacts for routine work.
- Clean up after yourself before finishing: remove temporary files, disposable
  validation builds, duplicate packages, and superseded artifacts from your work.
  Keep the current usable outputs and evidence that is still useful.
- Use `scripts/Clean-Artifacts.ps1` where appropriate. Check cleanup paths and
  active processes; do not remove outputs in use, unrelated work, or user data.
