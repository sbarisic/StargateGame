"""Regression checks for resumable VS bakes and extracted/default canopy routing."""
import json
from pathlib import Path
import tempfile
from build_asset_packs import BakeStages, canopy_command


with tempfile.TemporaryDirectory() as directory:
    root = Path(directory)
    cache = root/'cache'; cache.mkdir()
    source = root/'input'; source.write_bytes(b'input')
    first = root/'first'; second = root/'second'
    calls = []
    def action(output):
        calls.append(output.name)
        output.write_bytes(source.read_bytes())
    stages = BakeStages(cache)
    assert stages.run('first', [source], lambda: [first], lambda: action(first))
    checkpoint = (cache/'bake-stages.json').read_bytes()
    def fail():
        second.write_bytes(b'incomplete')
        raise RuntimeError('interrupted later stage')
    try:
        stages.run('second', [source], lambda: [second], fail)
        raise AssertionError('Expected interruption')
    except RuntimeError:
        pass
    assert (cache/'bake-stages.json').read_bytes() == checkpoint
    resumed = BakeStages(cache)
    assert not resumed.run('first', [source], lambda: [first], lambda: action(first))
    assert resumed.run('second', [source], lambda: [second], lambda: action(second))
    assert calls == ['first', 'second']
    first.write_bytes(b'broken')
    assert resumed.run('first', [source], lambda: [first], lambda: action(first))
    source.write_bytes(b'changed input')
    assert resumed.run('first', [source], lambda: [first], lambda: action(first))
    assert resumed.run('second', [source], lambda: [second], lambda: action(second))

    extraction = cache/'canopy-sources/MoreRealisticTree_01'
    extraction.mkdir(parents=True)
    (extraction/'extraction.json').write_text(json.dumps(dict(lods=[dict(file='tree.glb')], sources={})))
    (extraction/'tree.glb').write_bytes(b'glb fixture')
    command, inputs = canopy_command(root, cache, 'MoreRealisticTree_01', '', cache/'models')
    assert command[command.index('--id')+1] == ''
    assert command[command.index('--source')+1] == str(extraction)
    assert '--prepared' not in command and extraction/'tree.glb' in inputs
    (extraction/'extraction.json').unlink()
    try:
        canopy_command(root, cache, 'MoreRealisticTree_01', '', cache/'models')
        raise AssertionError('Missing source extraction accepted')
    except ValueError as error:
        assert 'source extraction' in str(error)
print('PASS: default canopy uses extracted source, missing source fails early, completed bakes resume, failed stages retain checkpoints, changed inputs and damaged outputs rebuild')
