"""Incremental, process-locked entry point for the Visual Studio asset build."""

import argparse
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time

VERSION = 1


def inventory(root, paths):
    result = {}
    for path in paths:
        if not path.exists():
            continue
        files = path.rglob("*") if path.is_dir() else [path]
        for file in files:
            if file.is_file():
                stat = file.stat()
                result[file.relative_to(root).as_posix()] = [
                    stat.st_size,
                    stat.st_mtime_ns,
                ]
    return dict(sorted(result.items()))


@contextlib.contextmanager
def cache_lock(path):
    with path.open("a+b") as stream:
        stream.seek(0, 2)
        if not stream.tell():
            stream.write(b"0")
            stream.flush()
        announced = False
        while True:
            try:
                stream.seek(0)
                if os.name == "nt":
                    import msvcrt

                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl

                    fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                break
            except (OSError, BlockingIOError):
                if not announced:
                    print("Assets: waiting for another cook to finish...", flush=True)
                    announced = True
                time.sleep(0.2)
        try:
            yield
        finally:
            stream.seek(0)
            if os.name == "nt":
                msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)
            else:
                fcntl.flock(stream, fcntl.LOCK_UN)


def load_state(path):
    try:
        return json.loads(path.read_text())
    except (OSError, ValueError):
        return {}


def build(root, cache):
    root, cache = root.resolve(), cache.resolve()
    cache.mkdir(parents=True, exist_ok=True)
    package = cache / "models.sga"
    state_file = cache / "build-state.json"
    generated = cache / "models"
    inputs = [root / "assets" / name for name in ("models", "characters", "foliage")]
    inputs += [
        root / "tools" / name
        for name in (
            "prepare_vegetation.py",
            "cook_assets.py",
            "build_assets.py",
            "requirements-assets.txt",
        )
    ]
    metadata = [
        root / "assets/vegetation/trees.json",
        root / "assets/vegetation/provenance.json",
        root / "assets/vegetation/world-canopies.json",
    ]
    inputs += [root / "assets/vegetation/prototype", root / "assets/vegetation/grass-volume"]
    with cache_lock(cache / "cook.lock"):
        before = inventory(root, inputs)
        old = load_state(state_file)
        outputs = inventory(cache, [package, generated])
        mapping = inventory(root, metadata)
        if (
            old.get("version") == VERSION
            and old.get("inputs") == before
            and package.exists()
            and outputs
            and mapping
            and old.get("outputs") == outputs
            and old.get("metadata") == mapping
        ):
            print(f"Assets: up to date ({package})", flush=True)
            return
        missing = [
            name
            for name in ("numpy", "PIL", "brotli")
            if importlib.util.find_spec(name) is None
        ]
        if missing:
            raise RuntimeError(
                "Missing asset-cooker Python dependencies: "
                + ", ".join(missing)
                + f'. Run: "{sys.executable}" -m pip install -r "{root / "tools/requirements-assets.txt"}"'
            )
        # A failed or interrupted cook must never mark the old package current.
        state_file.unlink(missing_ok=True)
        print(
            "Assets: preparing vegetation and cooking models; first build can take several minutes.",
            flush=True,
        )
        subprocess.run(
            [
                sys.executable,
                str(root / "tools/prepare_vegetation.py"),
                "--output",
                str(generated),
            ],
            cwd=root,
            check=True,
        )
        subprocess.run(
            [
                sys.executable,
                str(root / "tools/cook_assets.py"),
                "--assets",
                str(root / "assets"),
                "--vegetation",
                str(generated),
                "--output",
                str(package),
            ],
            cwd=root,
            check=True,
        )
        if not package.is_file() or package.stat().st_size == 0:
            raise RuntimeError("Cooker did not produce models.sga.")
        if before != inventory(root, inputs):
            raise RuntimeError(
                "Asset inputs changed during cooking. Build again to finish with consistent assets."
            )
        state = dict(
            version=VERSION,
            inputs=before,
            outputs=inventory(cache, [package, generated]),
            metadata=inventory(root, metadata),
        )
        staging = state_file.with_suffix(".tmp")
        staging.write_text(json.dumps(state, sort_keys=True, indent=2))
        os.replace(staging, state_file)
        print(f"Assets: ready ({package.stat().st_size / 1048576:.1f} MiB)", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--root", type=Path, default=Path(__file__).resolve().parents[1]
    )
    parser.add_argument("--cache", type=Path, required=True)
    args = parser.parse_args()
    try:
        build(args.root, args.cache)
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"Asset build failed: {error}", file=sys.stderr, flush=True)
        sys.exit(1)
