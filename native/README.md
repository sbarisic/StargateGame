# Native Jolt runtime

Normal builds use the checked-in x64 binaries under `runtimes/`. They need no C++
compiler. `Directory.Build.targets` excludes the stock JoltPhysics.Native assets
in every consuming application and copies exactly one game-owned native library
for the build/publish runtime. Unsupported runtime IDs fail the build.

## Pins

- JoltPhysicsSharp NuGet: **2.23.0**, managed source `e468f1f30ef74726a799ceef1f8427fa39867e93`.
- joltc submodule: `2bbcf73b1bec261e0351814662922f1b4784454c`.
- Jolt Physics **5.6.0** submodule: `e77f175595e64cb44218cc9d9d56fc365ad0e36a`.
- Bridge ABI: **1**. `rollback.inl` is appended to a build-only copy of `joltc.cpp`;
  the upstream submodules stay unchanged.

## Rebuild

Initialize submodules with `git submodule update --init --recursive`.
On Windows, install Python 3, CMake, and Visual Studio 2022 C++ build tools, then run:

```powershell
./scripts/Build-Native.ps1
```

On Linux, install Python 3, CMake, Clang, make, and the C++ standard library headers:

```sh
python3 tools/build_native.py
```

The script verifies source revisions, builds single precision with
`CROSS_PLATFORM_DETERMINISTIC=ON`, disables optional CPU extensions above SSE2,
and packages the library with a SHA-256 and build options in its `build.json`.
Windows uses the static MSVC runtime; Linux uses the system C/C++ runtime.
The supplied Linux build was verified on Ubuntu through WSL.
Builds occur under ignored `artifacts/`. The recorded binary checksum identifies
that exact build; different compiler/toolchain versions can produce different bytes.

## Ownership and rollback

Each world owns a 16 MiB temporary allocator, job system, physics system,
characters, shapes, and rigid bodies. The small native extension supplies allocator-aware
character stepping and contact refresh, plus checkpoint save/restore/disposal.
Checkpoints include PhysicsSystem and each CharacterVirtual, including contact state.
They stay in-process, are never sent over the network, and cannot restore into a
different world or topology. The client bounds history to 180 checkpoints and 32 MiB.

JoltPhysicsSharp 2.23.0 does not transfer managed collision-filter ownership even
though joltc deletes the filters with PhysicsSystem. PhysicsWorld retains their
wrappers as borrowed objects after successful transfer; it disposes their managed
registrations after the native system destroys them. A forced-GC regression covers this workaround.
The wrapper handles are private and never reused after system disposal.

The native wrapper has a process-global registry, so game-owned lifecycle/step
operations are serialized when multiple worlds run in one process. A dedicated
server and normal graphical client each own one simulation. Foundation is initialized
once and shut down at process exit; every session disposes its simulation resources.

Deterministic compiler settings and stable update order help replay, but the game
does not depend on Windows and Linux producing bit-identical physics. Structured,
validated authoritative snapshots correct positions, rotations, velocities,
sleeping state, and character support. Changed contact caches are invalidated before replay.

The pinned 2.23.0 bindings also leave `OwnsHandle` false in constructors that assign
`Handle` directly (including PhysicsSystem, CharacterVirtual, and span-based
MeshShapeSettings). `Native.Own` uses a .NET UnsafeAccessor to set ownership for
game-created objects. This is essential for repeated terrain/planet teardown; the
20-switch regression exposed the leak and verifies the fix. This managed workaround
does not change the native ABI or packaged binary checksums.
