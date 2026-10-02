"""Run with Blender --background --factory-startup --python this_file."""
import ast
import bpy
import os
import re
import tempfile
from pathlib import Path

source = Path(__file__).resolve().parents[1] / 'Scripts' / 'render_scene.py'
tree = ast.parse(source.read_text(encoding='utf-8-sig'))
function = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'compositor_frame_path')
scene = bpy.context.scene
scene.render.engine = 'CYCLES'
scene.cycles.samples = 1
scene.render.resolution_x = scene.render.resolution_y = 16
scene.render.resolution_percentage = 100
scene.render.save_output = False
scene.render.use_compositing = True
group = bpy.data.node_groups.new('Output test', 'CompositorNodeTree')
scene.compositing_node_group = group
compositor_node = group.nodes.new('CompositorNodeOutputFile')
item = compositor_node.file_output_items.new('RGBA', 'Image')
rgb = group.nodes.new('CompositorNodeRLayers')
group.links.new(rgb.outputs[0], compositor_node.inputs[0])
exec(compile(ast.Module(body=[function], type_ignores=[]), str(source), 'exec'))
with tempfile.TemporaryDirectory(prefix='BRH-compositor-') as folder:
    compositor_node.directory = folder
    for name, extension, override in [('Still', True, False), ('Camera####', True, False), ('NoExtension', False, False), ('Override', True, True)]:
        compositor_node.file_name = name
        compositor_node.use_file_extension = extension
        compositor_node.format.media_type = 'IMAGE'
        compositor_node.format.file_format = 'PNG'
        item.override_node_format = override
        if override:
            item.format.media_type = 'IMAGE'
            item.format.file_format = 'JPEG'
        scene.frame_set(2)
        predicted = compositor_frame_path(2)
        bpy.ops.render.render()
        assert os.path.isfile(predicted) and os.path.getsize(predicted) > 0, (predicted, os.listdir(folder))
        print('PASS compositor output:', predicted)
print('COMPOSITOR PATH TESTS PASSED')
