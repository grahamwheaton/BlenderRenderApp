import bpy
from fractions import Fraction
import os
import sys

args = sys.argv[sys.argv.index("--") + 1:]
camera_name, start_text, end_text, step_text, output_path, engine, width, height, scale, frame_rate, file_format, render_mode, overwrite, placeholders, ignore_compositor, transparent_background, viewport_shading = args
camera = bpy.data.objects.get(camera_name)
if camera is None or camera.type != 'CAMERA':
    raise RuntimeError(f"Camera not found: {camera_name}")

scene = bpy.context.scene
scene.camera = camera
scene.frame_start = int(start_text)
scene.frame_end = int(end_text)
scene.frame_step = int(step_text)
scene.render.filepath = output_path
if render_mode == 'PLAYBLAST' and viewport_shading == 'RENDERED':
    if engine != 'KEEP':
        scene.render.engine = engine
    scene.render.use_compositing = False
elif render_mode == 'PLAYBLAST' and viewport_shading == 'MATERIAL':
    scene.render.engine = 'BLENDER_EEVEE_NEXT'
    scene.render.use_compositing = False
elif render_mode == 'PLAYBLAST':
    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.render.use_compositing = False
    if viewport_shading == 'WIREFRAME':
        scene.display.shading.light = 'FLAT'
        scene.display.shading.show_shadows = False
        scene.display.shading.show_cavity = False
        scene.display.shading.show_outline = True
elif engine != 'KEEP':
    scene.render.engine = engine
scene.render.resolution_x = int(width)
scene.render.resolution_y = int(height)
scene.render.resolution_percentage = int(scale)
fps_fraction = Fraction(frame_rate).limit_denominator(1001)
scene.render.fps = fps_fraction.numerator
scene.render.fps_base = fps_fraction.denominator
scene.render.use_overwrite = overwrite == '1'
scene.render.use_placeholder = placeholders == '1'
scene.render.use_compositing = False if render_mode == 'PLAYBLAST' else ignore_compositor != '1'
scene.render.film_transparent = transparent_background == '1'
# Per-Camera Resolution applies its camera values again from a render handler.
# Keep its runtime-only values aligned with this queued job so it cannot undo
# overrides selected in the launcher. The .blend is never saved.
if hasattr(camera.data, 'per_camera_resolution'):
    camera_resolution = camera.data.per_camera_resolution
    if camera_resolution.use_custom_resolution:
        camera_resolution.resolution_x = int(width)
        camera_resolution.resolution_y = int(height)
        camera_resolution.resolution_percentage = int(scale)
scene.render.image_settings.file_format = file_format
print(f"BRH: Mode {render_mode} | camera {camera_name} | frames {scene.frame_start}-{scene.frame_end} step {scene.frame_step} | {scene.render.resolution_x}x{scene.render.resolution_y} at {scene.render.resolution_percentage}% | {scene.render.fps / scene.render.fps_base:g} fps | {scene.render.engine} | {scene.render.image_settings.file_format} | overwrite={scene.render.use_overwrite} | placeholders={scene.render.use_placeholder} | compositor={scene.render.use_compositing} | transparent={scene.render.film_transparent} | output {scene.render.filepath}")
def report_completed_frame(render_scene):
    rendered_path = bpy.path.abspath(render_scene.render.frame_path(frame=render_scene.frame_current))
    print(f"BRH_FRAME_DONE:{render_scene.frame_current}|{rendered_path}", flush=True)

bpy.app.handlers.render_write.append(report_completed_frame)
bpy.ops.render.render(animation=True)
sys.stdout.flush()
# Some third-party add-ons can hang or fail while Blender unregisters them in
# background mode. The render has completed and its files are closed at this point.
os._exit(0)
