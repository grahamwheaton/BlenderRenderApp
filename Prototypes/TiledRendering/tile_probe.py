"""Run with Blender --background --factory-startup --disable-autoexec scene.blend --python tile_probe.py -- OUTPUT.
Low resolution experiment only; never saves the source blend.
"""
import bpy
import json
import sys
import argparse
import time
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('output')
parser.add_argument('--size', type=int, default=512)
parser.add_argument('--samples', type=int, default=32)
parser.add_argument('--overlaps', type=int, nargs='+', default=[0,32,64,128])
options = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
size = options.size
assert size > 0 and size % 2 == 0
root = Path(options.output).resolve()
root.mkdir(parents=True, exist_ok=True)
s = bpy.context.scene
s.render.resolution_x = s.render.resolution_y = size
s.render.resolution_percentage = 100
s.render.engine = 'CYCLES'
s.cycles.device = 'CPU'
s.cycles.samples = options.samples
s.cycles.use_adaptive_sampling = False
s.cycles.seed = 17
s.render.use_compositing = True
s.render.use_sequencer = False
s.render.use_border = False
s.render.use_crop_to_border = False
tree = getattr(s, 'compositing_node_group', None) or s.node_tree
outputs = [n for n in tree.nodes if n.bl_idname == 'CompositorNodeOutputFile' and not n.mute]
if len(outputs) != 1:
    raise RuntimeError('Probe requires exactly one active File Output node')
out = outputs[0]
out.format.file_format = 'OPEN_EXR_MULTILAYER'
out.format.color_depth = '32'
out.format.exr_codec = 'ZIP'
def render(name, bounds=None):
    directory = root / name
    directory.mkdir(parents=True,exist_ok=True)
    if hasattr(out, 'directory'):
        out.directory = str(directory)
        out.file_name = 'result'
    else:
        out.base_path = str(directory)
    s.render.filepath = str(directory / 'unused')
    if hasattr(s.render, 'save_output'):
        s.render.save_output = False
    s.render.use_border = bounds is not None
    s.render.use_crop_to_border = bounds is not None
    if bounds:
        x0,y0,x1,y1 = bounds
        s.render.border_min_x, s.render.border_min_y = x0/size, y0/size
        s.render.border_max_x, s.render.border_max_y = x1/size, y1/size
    bpy.ops.render.render()
    files = list(directory.glob('*.exr'))
    if len(files) != 1:
        raise RuntimeError(f'Expected one EXR in {directory}, got {files}')
    return str(files[0])

reference = render('reference')
for overlap in options.overlaps:
    started = time.monotonic()
    manifest = {'size':size,'overlap':overlap,'samples':options.samples,'reference':reference,'tiles':[]}
    for y in range(2):
        for x in range(2):
            core = [x*size//2,y*size//2,(x+1)*size//2,(y+1)*size//2]
            bounds = [max(0,core[0]-overlap),max(0,core[1]-overlap),min(size,core[2]+overlap),min(size,core[3]+overlap)]
            manifest['tiles'].append({'core':core,'bounds':bounds,'file':render(f'overlap_{overlap}/tile_{x}_{y}',bounds)})
    manifest['render_seconds'] = time.monotonic()-started
    (root/f'overlap_{overlap}'/'manifest.json').write_text(json.dumps(manifest,indent=2),encoding='utf-8')
print('PROBE_RENDER_COMPLETE', flush=True)
