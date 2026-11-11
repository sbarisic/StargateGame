"""Rebuild the game-owned Jolt C ABI. Normal dotnet builds use packaged binaries."""

from pathlib import Path
import hashlib, json, os, platform, shutil, subprocess

ROOT = Path(__file__).resolve().parents[1]
PINS = {
    "joltc": "2bbcf73b1bec261e0351814662922f1b4784454c",
    "JoltPhysics": "e77f175595e64cb44218cc9d9d56fc365ad0e36a",
}
for name, revision in PINS.items():
    actual = subprocess.check_output(
        ["git", "-C", str(ROOT / "thirdparty" / name), "rev-parse", "HEAD"], text=True
    ).strip()
    if actual != revision:
        raise SystemExit(f"{name}: expected {revision}, found {actual}")
rid = "win-x64" if os.name == "nt" else "linux-x64"
if platform.machine().lower() not in ("amd64", "x86_64"):
    raise SystemExit("Only x64 native builds are supported.")
source = ROOT / "artifacts" / "native-source" / rid
shutil.copytree(
    ROOT / "thirdparty" / "joltc",
    source,
    dirs_exist_ok=True,
    ignore=shutil.ignore_patterns(".git"),
)
shutil.copy2(ROOT / "native" / "rollback.inl", source / "src" / "rollback.inl")
with (source / "src" / "joltc.cpp").open("a", encoding="utf-8") as output:
    bridge_hash = hashlib.sha256((ROOT / "native" / "rollback.inl").read_text().replace('\r\n', '\n').encode()).hexdigest()
    output.write(f'\n#define SG_BRIDGE_SOURCE_SHA "{bridge_hash}"\n')
    output.write('\n#include "rollback.inl"\n')
build = ROOT / "artifacts" / "native-build" / rid
options = [
    "-DCMAKE_BUILD_TYPE=Release",
    "-DCROSS_PLATFORM_DETERMINISTIC=ON",
    "-DDOUBLE_PRECISION=OFF",
    "-DJPH_SAMPLES=OFF",
    "-DJPH_TESTS=OFF",
    "-DINTERPROCEDURAL_OPTIMIZATION=OFF",
    "-DJPH_USE_DX12=OFF",
    "-DJPH_USE_VK=OFF",
    "-DJPH_USE_MTL=OFF",
    "-DFLOATING_POINT_EXCEPTIONS_ENABLED=OFF",
]
options += [
    f"-DUSE_{feature}=OFF"
    for feature in (
        "SSE4_1",
        "SSE4_2",
        "AVX",
        "AVX2",
        "AVX512",
        "LZCNT",
        "TZCNT",
        "F16C",
        "FMADD",
    )
]
options += [f"-DJOLT_PHYSICS_ROOT={(ROOT / 'thirdparty' / 'JoltPhysics').as_posix()}"]
if os.name == "nt":
    options += ["-G", "Visual Studio 17 2022", "-A", "x64"]
else:
    options += ["-DCMAKE_CXX_COMPILER=clang++", "-DCMAKE_C_COMPILER=clang"]
subprocess.run(["cmake", "-S", str(source), "-B", str(build), *options], check=True)
subprocess.run(
    [
        "cmake",
        "--build",
        str(build),
        "--config",
        "Release",
        "--target",
        "joltc",
        "--parallel",
        "8",
    ],
    check=True,
)
filename = "joltc.dll" if os.name == "nt" else "libjoltc.so"
binary = build / "bin" / "Release" / filename if os.name == "nt" else build / "lib" / filename
if not binary.is_file():
    raise SystemExit(f"Expected freshly linked native library is missing: {binary}")
target = ROOT / "native" / "runtimes" / rid / "native"
target.mkdir(parents=True, exist_ok=True)
shutil.copy2(binary, target / filename)
metadata = {
    "rid": rid,
    "sources": PINS,
    "cmakeOptions": options,
    "bridgeSha256": hashlib.sha256(
        (ROOT / "native" / "rollback.inl").read_bytes()
    ).hexdigest(),
    "sha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
}
(target.parent / "build.json").write_text(
    json.dumps(metadata, indent=2) + "\n", encoding="utf-8"
)
print(f"Packaged {target / filename}")
