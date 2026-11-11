"""Create the first Gaea 2 valley study from the preserved empty project.

Usage: python create_test1.py --output valley-preview.terrain
An existing output is never overwritten unless --overwrite is supplied.
Node properties/ports follow the installed Gaea 2.3.1 node catalog and examples.
"""
import argparse
import datetime
import json
import math
import struct
import zlib
from pathlib import Path


def clearing_weight(x_metres, y_metres):
    """A solid 22 m pad with an approximately 8 m smooth, irregular apron."""
    dx, dy = x_metres - 512.0 * 0.38, y_metres - 512.0 * 0.58
    radius = math.hypot(dx, dy)
    angle = math.atan2(dy, dx)
    outer = 19.0 + 0.6 * math.sin(3 * angle) + 0.3 * math.cos(5 * angle)
    t = max(0.0, min(1.0, (radius - 11.0) / (outer - 11.0)))
    # Quintic falloff has zero first and second derivatives at both ends.
    return 1.0 - t * t * t * (t * (t * 6.0 - 15.0) + 10.0)


def write_clearing_mask(destination):
    """Write numeric mask data as lossless, linear 16-bit grayscale PNG."""
    resolution = 1024
    raw = bytearray()
    for y in range(resolution):
        raw.append(0)  # PNG filter: none
        for x in range(resolution):
            weight = clearing_weight(x * 512.0 / (resolution - 1),
                                     y * 512.0 / (resolution - 1))
            raw.extend(struct.pack('>H', round(weight * 65535)))

    def chunk(kind, data):
        return (struct.pack('>I', len(data)) + kind + data
                + struct.pack('>I', zlib.crc32(kind + data)))

    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', resolution, resolution, 16, 0, 0, 0, 0))
    png += chunk(b'gAMA', struct.pack('>I', 100000))
    png += chunk(b'IDAT', zlib.compress(raw, 9))
    png += chunk(b'IEND', b'')
    destination.write_bytes(png)


def build():
    original = Path(__file__).with_name('test1.original.terrain.bak')
    project = json.loads(original.read_text(encoding='utf-8-sig'))

    def strip_ids(value):
        if isinstance(value, dict):
            return {k: strip_ids(v) for k, v in value.items() if k != '$id'}
        if isinstance(value, list):
            return [strip_ids(v) for v in value]
        return value

    project = strip_ids(project)
    asset = project['Assets']['$values'][0]
    terrain = asset['Terrain']
    nodes = terrain['Nodes'] = {}

    def node(number, kind, name, x, y, properties=None, inputs=None,
             outputs=('Out',), mask=False):
        n = {'$type': f'QuadSpinner.Gaea.Nodes.{kind}, Gaea.Nodes',
             **(properties or {}), 'Id': number, 'Name': name,
             'Position': {'X': x, 'Y': y},
             'Ports': {'$values': []}, 'Modifiers': {'$values': []}}
        inputs = inputs or {}
        in_names = ['In'] + [p for p in inputs if p != 'In']
        if kind == 'Combine' and 'Mask' not in in_names:
            in_names.append('Mask')
        ports = n['Ports']['$values']
        for port in in_names:
            p = {'Name': port, 'Type': ('PrimaryIn, Required' if port in inputs else 'PrimaryIn') if port == 'In' else 'In',
                 'IsExporting': True}
            if port in inputs:
                source, source_port = inputs[port]
                p['Record'] = {'From': source, 'To': number,
                               'FromPort': source_port, 'ToPort': port, 'IsValid': True}
            ports.append(p)
        for port in outputs:
            ports.append({'Name': port, 'Type': 'PrimaryOut' if port == 'Out' else 'Out',
                          'IsExporting': True})
        # Port positions are part of Gaea's execution contract, not just UI
        # layout: primary input, primary output, then secondary ports.
        ports.sort(key=lambda p: 0 if p['Name'] == 'In' else 1 if p['Name'] == 'Out' else 2)
        if mask:
            n['RenderIntentOverride'] = 'Mask'
        nodes[str(number)] = n
        return n

    node(101, 'Canyon', '01 - River valley', 26000, 26000,
         {'Valley': 0.62, 'Depth': 0.85, 'Seed': 37753}, outputs=('Out', 'Depth'))
    node(102, 'Stratify', '02 - Rock strata', 26280, 26000,
         {'Spacing': 0.10, 'Shape': 0.55, 'Seed': 29349,
          'TiltAmount': 0.20, 'Direction': 90}, {'In': (101, 'Out')})
    erode = node(103, 'Erosion2', '03 - Valley erosion', 26560, 26000,
         {'Downcutting': 0.35, 'ErosionScale': 35.0, 'Seed': 5397,
          'SuspendedLoadDischargeAmount': 0.27, 'BedLoadDischargeAmount': 0.20,
          'CoarseSedimentsDischargeAmount': 0.22}, {'In': (102, 'Out')},
         outputs=('Out', 'Flow', 'Wear', 'Deposits'))
    erode['Modifiers']['$values'].append({
        '$type': 'QuadSpinner.Gaea.Nodes.Modifiers.Height, Gaea.Nodes',
        'Range': {'X': 0.0, 'Y': 0.35}, 'Falloff': 0.12,
        'Name': 'Height', 'HasUI': True, 'Intrinsic': True})
    rivers = node(104, 'Rivers', '04 - Rivers and tributaries', 26840, 26000,
         {'Water': 0.65, 'Width': 0.40, 'Depth': 0.15, 'Downcutting': 0.25,
          'RiverValleyWidth': 'plus2', 'Headwaters': 24, 'RenderSurface': True,
          'Seed': 10279}, {'In': (103, 'Out')},
         outputs=('Out', 'Rivers', 'Depth', 'Surface', 'Direction'))
    # Unconnected optional ports still occupy slots in Gaea's runtime port
    # array. Preserve the complete layout, including its order.
    rivers['Ports']['$values'].insert(2, {
        'Name': 'Headwaters', 'Type': 'In', 'IsExporting': True})

    node(105, 'File', 'Filled clearing - 22m core, 8m apron', 26840, 26300,
         {'FileName': 'test1_clearing_mask.png', 'RelativePath': True,
          'IsRGB': False, 'EnforceLinearGamma': True}, mask=True)
    node(107, 'Constant', 'Gate platform elevation', 26840, 26550,
         {'Height': 0.14})
    node(108, 'Combine', '05 - Terrain with gate clearing', 27120, 26000,
         {'PortCount': 2, 'Mode': 'Blend', 'Ratio': 1.0},
         {'In': (104, 'Out'), 'Input2': (107, 'Out'), 'Mask': (105, 'Out')})

    # Exclude the landing pad from the river mask as well as from the terrain.
    node(109, 'Combine', 'Water mask outside clearing', 27120, 26300,
         {'PortCount': 2, 'Mode': 'Subtract', 'Ratio': 1.0},
         {'In': (104, 'Rivers'), 'Input2': (105, 'Out')}, mask=True)
    tex = node(110, 'TextureBase', '06 - Rock and soil detail', 27400, 26000,
         {'Scale': 0.30, 'Seed': 25920}, {'In': (108, 'Out')})
    tex['Version'] = 2
    tex['Ports']['$values'].append({
        'Name': 'Guide', 'Type': 'In', 'IsExporting': True})
    node(111, 'SatMap', '07 - Valley ground colour', 27680, 26000,
         {'LibraryItem': 81, 'Bias': 0.26, 'Reverse': True}, {'In': (110, 'Out')})
    node(112, 'Tint', 'River colour preview', 27400, 26300,
         {'Start': {'R': 0.18, 'G': 0.36, 'B': 0.39},
          'End': {'R': 0.03, 'G': 0.11, 'B': 0.10}}, {'In': (104, 'Depth')})
    node(113, 'Combine', '08 - COLOURED PREVIEW', 27960, 26000,
         {'PortCount': 2, 'Mode': 'Blend', 'Ratio': 1.0},
         {'In': (111, 'Out'), 'Input2': (112, 'Out'), 'Mask': (109, 'Out')})
    nodes['113']['RenderIntentOverride'] = 'Color'

    exports = [
        (201, 'height', 108, 'Out', 'PNG16'),
        (202, 'ground_colour', 111, 'Out', 'PNG8'),
        (203, 'water_mask', 109, 'Out', 'PNG16'),
        (204, 'water_surface', 104, 'Surface', 'EXR'),
        (205, 'water_depth', 104, 'Depth', 'EXR'),
        (206, 'gate_clearing', 105, 'Out', 'PNG16'),
    ]
    for index, (number, name, source, port, fmt) in enumerate(exports):
        node(number, 'Export', name, 28280, 25750 + index * 175,
             {'Format': fmt, 'Location': 'BuildFolder', 'IfFileExists': 'Overwrite'},
             {'In': (source, port)})

    terrain.update(Width=512.0, Height=128.0, Ratio=0.25)
    tab = terrain['GraphTabs']['$values'][0]
    tab.update(Name='First landing - valley', ZoomFactor=0.48,
               ViewportLocation={'X': 25750.0, 'Y': 25550.0})
    asset['BuildDefinition'].update(Resolution=1024, BakeResolution=1024,
        Destination='<Builds>\\[Filename]\\[+++]', ColorSpace='sRGB')
    state = asset['State']
    state.update(PreviewResolution=1024, BakeResolution=1024,
                 SelectedNode=113, UnderlayNode=108)
    project['Metadata'].update(Name='test1 - First landing river valley',
        Description=('512 m square valley study, 128 m height scale. Canyon / strata / erosion / rivers / '
                     'masked gate clearing. Select 08 - COLOURED PREVIEW for the colour view; '
                     '05 - Terrain with gate clearing for geometry. Community build: 1024. '
                     'Gate clearing: solid 22 m core, approximately 8 m feathered apron. '
                     'Keep test1_clearing_mask.png beside this project. The apron has a smooth irregular boundary. '
                     'Gate platform elevation controls the flat interior height (0.14 = 17.92 m). '
                     'Water surface/depth require water_mask; they are not terrain collision. '
                     'Terrain is not yet imported into StargateGame. Node patterns based on bundled '
                     'QuadSpinner examples Canyon River with Sea, Rocky Riverbed and Cartography - 3D Mountain Map.'),
        DateLastSaved=datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%d %H:%M:%SZ'))

    # Gaea uses Newtonsoft reference-preserving JSON. Allocate globally unique
    # object IDs before emitting parent references, which must resolve backward.
    next_id = 0
    def assign(value):
        nonlocal next_id
        if isinstance(value, dict):
            next_id += 1
            values = list(value.items())
            value.clear()
            value['$id'] = str(next_id)
            for key, child in values:
                value[key] = child
                assign(child)
        elif isinstance(value, list):
            for child in value:
                assign(child)
    assign(project)
    for key, n in nodes.items():
        if key == '$id':
            continue
        for child in n['Ports']['$values'] + n['Modifiers']['$values']:
            child['Parent'] = {'$ref': n['$id']}
    expected_ports = {
        104: ['In', 'Out', 'Headwaters', 'Rivers', 'Depth', 'Surface', 'Direction'],
        108: ['In', 'Out', 'Input2', 'Mask'],
        109: ['In', 'Out', 'Input2', 'Mask'],
        110: ['In', 'Out', 'Guide'],
        113: ['In', 'Out', 'Input2', 'Mask'],
    }
    for number, expected in expected_ports.items():
        actual = [p['Name'] for p in nodes[str(number)]['Ports']['$values']]
        assert actual == expected, f'Incorrect port layout for node {number}: {actual}'
    return project


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--overwrite', action='store_true')
    args = parser.parse_args()
    result = build()
    if args.output.exists() and not args.overwrite:
        parser.error('Output exists; use --overwrite to replace it.')
    write_clearing_mask(args.output.with_name('test1_clearing_mask.png'))
    with args.output.open('w' if args.overwrite else 'x', encoding='utf-8') as output:
        json.dump(result, output, indent=2, ensure_ascii=False)
        output.write('\n')
    print(f'Created {args.output} ({len(result["Assets"]["$values"][0]["Terrain"]["Nodes"]) - 1} nodes)')
