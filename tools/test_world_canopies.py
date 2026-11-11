"""Validate normal-world coverage and production asset dependencies."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1] / "assets"
variants = json.loads((root / "vegetation/trees.json").read_text())
canopies = json.loads((root / "vegetation/world-canopies.json").read_text())
assert set(canopies) == set(variants) - {"Broadleaf_Desktop"}
bare = json.loads((root / "models/Broadleaf_Desktop.json").read_text())
assert all(part.get("alphaCutoff", 0) == 0 for part in bare["parts"])
for source, model in canopies.items():
    assert model["Scale"] == 1, "world placements must not receive the fixture's enlargement"
    assert model["LeafModel"] == variants[source][0]
    descriptor = json.loads((root / model["Canopy"]).read_text())
    # The original broadleaf bake predates the descriptive model/level fields.
    assert descriptor.get("model", "MoreRealisticTree_01") == source and descriptor["version"] == 3
    assert descriptor.get("trunkLevels", 3) == model["TrunkLevels"]
    assert descriptor["ambient"]["bytes"] > 0
    for level in range(model["TrunkLevels"]):
        trunk = json.loads((root / "models" / f'{model["TrunkPrefix"]}{level}.json').read_text())
        assert trunk["parts"] and all(part.get("alphaCutoff", 0) == 0 for part in trunk["parts"])
    for level in descriptor["levels"]:
        width, height, depth = level["size"]
        assert len(level["slices"]) == depth
        for pair in level["slices"]:
            assert all((root / path).stat().st_size == width * height * 4 for path in pair)
print(f"PASS: all {len(canopies)} leafy variants, winter/summer identity, unscaled placements, wood-only trunks and volume dependencies")
