"""Small deterministic visibility fixtures and cooked dependency validation."""
import hashlib
import json
from pathlib import Path
import numpy as np
from bake_canopy_ambient import hemisphere_visibility

empty = np.zeros((5,5,5),np.float32)
assert np.all(hemisphere_visibility(empty,np.ones(3),1)==1)
roof=empty.copy();roof[:,3:,:]=2
first=hemisphere_visibility(roof,np.ones(3),1)
second=hemisphere_visibility(roof,np.ones(3),1)
assert np.array_equal(first,second) and np.isfinite(first).all()
assert first[2,1,2,0] < first[2,1,2,1], "roof must occlude sky more than ground"
assert first.min()>=0 and first.max()<=1
for path in Path("assets/vegetation/prototype").rglob("canopy.json"):
    descriptor=json.loads(path.read_text())
    assert descriptor["version"]==3
    ambient=descriptor["ambient"];total=0
    size=ambient["levels"][0]["size"]
    assert max(size)<=64
    for level,entry in enumerate(ambient["levels"]):
        assert entry["size"]==[max(1,n>>level) for n in size]
        data=(Path("assets")/entry["path"]).read_bytes()
        assert len(data)==int(np.prod(entry["size"]))*2
        assert hashlib.sha256(data).hexdigest()==entry["sha256"]
        total+=len(data)
    assert total==ambient["bytes"]
print("PASS: empty visibility, directional roof, deterministic bake, dimensions, checksums and budgets")
