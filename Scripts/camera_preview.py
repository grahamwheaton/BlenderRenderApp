"""Generate one local Workbench thumbnail; never save the source blend."""
import bpy
import json
import sys

job = json.loads(sys.argv[sys.argv.index('--') + 1])
scene = bpy.context.scene
camera = scene.objects.get(job['camera'])
if camera is None or camera.type != 'CAMERA':
    raise RuntimeError('Camera not found in scene: ' + job['camera'])
scene.frame_set(int(job['frame']))
scene.camera = camera
scene.render.engine = 'BLENDER_WORKBENCH'
width, height = max(1, int(job['width'])), max(1, int(job['height']))
ratio = 480 / max(width, height)
scene.render.resolution_x = max(1, round(width * ratio))
scene.render.resolution_y = max(1, round(height * ratio))
scene.render.resolution_percentage = 100
scene.render.use_compositing = False
scene.render.use_sequencer = False
scene.render.use_border = False
scene.render.film_transparent = False
scene.render.use_placeholder = False
scene.render.use_overwrite = True
if hasattr(scene.render, 'save_output'):
    scene.render.save_output = True
settings = scene.render.image_settings
if hasattr(settings, 'media_type'):
    settings.media_type = 'IMAGE'
settings.file_format = 'PNG'
scene.render.filepath = job['output']
bpy.ops.render.render(write_still=True)
print('BRH_PREVIEW_READY', flush=True)
