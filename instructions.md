# Development instructions

This is a new game in active development. Prioritize useful progress, clear code,
and the current design. The developer will playtest regularly and report bugs.

## Testing

- Some testing is expected. Use a relevant build and a small number of focused
  checks to catch obvious errors and verify the behavior you changed.
- Scale validation to the risk. Changes involving persistence, item ownership,
  networking, or destructive operations deserve targeted correctness checks.
- Keep visual checks lightweight: inspect a representative scene or capture when
  it helps verify the change.
- Once the relevant checks pass, move on. Repeat testing when a new change or
  failure gives a concrete reason to do so.
- Avoid exhaustive test matrices, repeated full-suite runs, large capture sets,
  long benchmark sessions, or extensive test infrastructure for routine changes.
  Perform that level of validation when explicitly requested or needed to
  investigate a specific problem.
- Leave broad gameplay coverage and extended play sessions to the developer.
  Briefly report what was checked and any important uncertainty that remains.

## Compatibility and existing features

- Break backward compatibility whenever it is needed to improve the current
  implementation. Compatibility with old saves, protocols, asset formats, APIs,
  and previous behavior is not a project requirement.
- Prefer a clean implementation over migration layers, compatibility shims,
  deprecated code paths, or legacy fallbacks maintained only for old versions.
- Existing features may be redesigned, replaced, or removed when the current
  work calls for it. Their age or previous presence is not a reason to retain them.
- Update directly affected callers, formats, version numbers, and documentation
  together so the current client, server, and assets remain consistent.
- State consequential breaks clearly, including when a fresh save, regenerated
  assets, or matching client/server builds are required. Routine compatibility
  breaks do not need a separate approval step.
- Keep changes scoped to the requested work and preserve unrelated parallel edits.
  Permission to break compatibility does not mean deleting existing user saves
  or source assets without a request to do so.
