"""Exercise incremental cooking without touching real assets or invoking Blender."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time

ENTRY = Path(__file__).with_name("build_assets.py").resolve()

with tempfile.TemporaryDirectory(prefix="stargate-vs-assets-") as folder:
    root = Path(folder)
    tools = root / "tools"
    tools.mkdir()
    for name in ("models", "characters", "foliage", "vegetation"):
        (root / "assets" / name).mkdir(parents=True)
    source = root / "assets/models/source.mesh"
    source.write_text("original")
    (tools / "build_assets.py").write_bytes(ENTRY.read_bytes())
    (tools / "requirements-assets.txt").write_text("test fixture")
    (tools / "prepare_vegetation.py").write_text("""import argparse,json
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--output',type=Path);a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
(a.output/'tree.mesh').write_text('prepared')
for name in ('trees','provenance'):Path('assets/vegetation/'+name+'.json').write_text('{}')
""")
    cooker = tools / "cook_assets.py"
    good_cooker = """import argparse,time
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--assets');p.add_argument('--vegetation');p.add_argument('--output',type=Path);a=p.parse_args()
count=a.output.parent/'count';n=int(count.read_text()) if count.exists() else 0;count.write_text(str(n+1));time.sleep(.15)
a.output.write_bytes(b'SGA1 test package')
"""
    cooker.write_text(good_cooker)
    cache = root / "cache"
    command = [sys.executable, str(ENTRY), "--root", str(root), "--cache", str(cache)]

    def run(ok=True):
        result = subprocess.run(command, capture_output=True, text=True)
        assert (result.returncode == 0) == ok, result.stdout + result.stderr
        return result.stdout

    def count():
        return int((cache / "count").read_text())

    run()
    assert count() == 1
    stamp = (cache / "models.sga").stat().st_mtime_ns
    assert (
        "up to date" in run()
        and count() == 1
        and (cache / "models.sga").stat().st_mtime_ns == stamp
    )
    source.write_text("changed source")
    run()
    assert count() == 2
    source.unlink()
    run()
    assert count() == 3
    (cache / "models/tree.mesh").unlink()
    run()
    assert count() == 4
    (cache / "models.sga").unlink()
    run()
    assert count() == 5
    (root / "assets/vegetation/trees.json").unlink()
    run()
    assert count() == 6
    before = (cache / "models.sga").read_bytes()
    cooker.write_text('raise RuntimeError("intentional cook failure")')
    run(False)
    assert (
        not (cache / "build-state.json").exists()
        and (cache / "models.sga").read_bytes() == before
    )
    cooker.write_text(good_cooker)
    first = subprocess.Popen(
        command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True
    )
    second = subprocess.Popen(
        command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True
    )
    a = first.communicate(timeout=30)
    b = second.communicate(timeout=30)
    assert first.returncode == second.returncode == 0, (a, b)
    assert count() == 7, "parallel builds must cook only once"
    print(
        "PASS: cold cook, no-op reuse, changed/deleted inputs, missing outputs/metadata, failure preservation, parallel cache locking"
    )
