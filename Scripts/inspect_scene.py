import bpy
import json
import math
import os
import re
import sys

scene = bpy.context.scene
saved_active_camera = scene.camera.name if scene.camera else None
saved_engine = scene.render.engine
saved_x, saved_y = scene.render.resolution_x, scene.render.resolution_y
saved_percentage = scene.render.resolution_percentage
saved_format = scene.render.image_settings.file_format
saved_filepath = bpy.path.abspath(scene.render.filepath)
saved_frame_rate = scene.render.fps / scene.render.fps_base
saved_use_overwrite = scene.render.use_overwrite
saved_use_placeholder = scene.render.use_placeholder
saved_use_compositing = scene.render.use_compositing
saved_film_transparent = scene.render.film_transparent

# Register only Per-Camera Resolution inside the otherwise isolated Blender
# session. Its saved PropertyGroup values are exposed through RNA only after
# registration; the raw ID-property group can appear empty.
per_camera_resolution_loaded = False
for module_name in ("bl_ext.blender_org.per_camera_resolution", "per_camera_resolution"):
    try:
        module = __import__(module_name, fromlist=["register"])
        if not hasattr(bpy.types.Camera, "per_camera_resolution"):
            module.register()
        per_camera_resolution_loaded = hasattr(bpy.types.Camera, "per_camera_resolution")
        if per_camera_resolution_loaded:
            break
    except Exception as error:
        print(f"BRH_PER_CAMERA_INFO:{module_name}:{error}")

def get_camera_resolution(camera):
    """Read registered RNA values, with raw data as a compatibility fallback."""
    if per_camera_resolution_loaded and hasattr(camera.data, "per_camera_resolution"):
        props = camera.data.per_camera_resolution
        if props.use_custom_resolution:
            return {
                "uses_per_camera_resolution": True,
                "resolution_x": int(props.resolution_x),
                "resolution_y": int(props.resolution_y),
                "resolution_percentage": int(props.resolution_percentage),
            }
    raw = camera.data.get("per_camera_resolution")
    if raw and bool(raw.get("use_custom_resolution", False)):
        return {
            "uses_per_camera_resolution": True,
            "resolution_x": int(raw.get("resolution_x", saved_x)),
            "resolution_y": int(raw.get("resolution_y", saved_y)),
            "resolution_percentage": int(raw.get("resolution_percentage", saved_percentage)),
        }
    return {
        "uses_per_camera_resolution": False,
        "resolution_x": saved_x,
        "resolution_y": saved_y,
        "resolution_percentage": saved_percentage,
    }

camera_settings = {
    camera.name: get_camera_resolution(camera)
    for camera in bpy.data.objects if camera.type == 'CAMERA'
}

def get_camera_keyframe_range(camera):
    """Return the full frame range of animation attached to the camera object."""
    animation = camera.animation_data
    if animation is None:
        return None
    ranges = []
    if animation.action is not None:
        start, end = animation.action.frame_range
        ranges.append((start, end))
    for track in animation.nla_tracks:
        for strip in track.strips:
            ranges.append((strip.frame_start, strip.frame_end))
    if not ranges:
        return None
    return {
        "start": math.floor(min(item[0] for item in ranges)),
        "end": math.ceil(max(item[1] for item in ranges)),
    }

camera_keyframes = {
    camera.name: get_camera_keyframe_range(camera)
    for camera in bpy.data.objects if camera.type == 'CAMERA'
}
thumbnail_dir = None
if "--" in sys.argv:
    extra_args = sys.argv[sys.argv.index("--") + 1:]
    thumbnail_dir = extra_args[0] if extra_args else None

thumbnails = {}
if thumbnail_dir:
    os.makedirs(thumbnail_dir, exist_ok=True)
    original_camera = scene.camera
    original_engine = scene.render.engine
    original_x, original_y = scene.render.resolution_x, scene.render.resolution_y
    original_percentage = scene.render.resolution_percentage
    original_format = scene.render.image_settings.file_format
    original_filepath = scene.render.filepath
    original_use_nodes = scene.use_nodes
    original_use_compositing = scene.render.use_compositing
    try:
        scene.render.engine = 'BLENDER_WORKBENCH'
        scene.render.resolution_x = 320
        scene.render.resolution_y = max(1, round(320 * original_y / max(1, original_x)))
        scene.render.resolution_percentage = 100
        scene.render.image_settings.file_format = 'PNG'
        scene.render.film_transparent = False
        scene.render.use_compositing = False
        scene.use_nodes = False
        for camera in sorted((obj for obj in bpy.data.objects if obj.type == 'CAMERA'), key=lambda obj: obj.name.casefold()):
            try:
                camera_resolution = camera_settings[camera.name]
                camera_x = camera_resolution["resolution_x"]
                camera_y = camera_resolution["resolution_y"]
                if camera_x >= camera_y:
                    scene.render.resolution_x = 320
                    scene.render.resolution_y = max(1, round(320 * camera_y / max(1, camera_x)))
                else:
                    scene.render.resolution_y = 320
                    scene.render.resolution_x = max(1, round(320 * camera_x / max(1, camera_y)))
                safe_name = re.sub(r'[^A-Za-z0-9_.-]+', '_', camera.name)
                preview_path = os.path.join(thumbnail_dir, safe_name + '.png')
                scene.camera = camera
                scene.render.filepath = preview_path
                bpy.ops.render.render(write_still=True)
                thumbnails[camera.name] = preview_path
            except Exception as error:
                print(f"BRH_PREVIEW_WARNING:{camera.name}:{error}")
    finally:
        try:
            scene.camera = original_camera
            scene.render.engine = original_engine
            scene.render.resolution_x, scene.render.resolution_y = original_x, original_y
            scene.render.resolution_percentage = original_percentage
            scene.render.image_settings.file_format = original_format
            scene.render.filepath = original_filepath
            scene.render.use_compositing = original_use_compositing
            scene.use_nodes = original_use_nodes
        except Exception as error:
            print(f"BRH_PREVIEW_RESTORE_WARNING:{error}")

data = {
    "cameras": sorted([obj.name for obj in bpy.data.objects if obj.type == 'CAMERA'], key=str.casefold),
    "active_camera": saved_active_camera,
    "frame_start": scene.frame_start,
    "frame_end": scene.frame_end,
    "frame_step": scene.frame_step,
    "output_path": saved_filepath,
    "render_engine": saved_engine,
    "resolution_x": saved_x,
    "resolution_y": saved_y,
    "resolution_percentage": saved_percentage,
    "file_format": saved_format,
    "frame_rate": saved_frame_rate,
    "use_overwrite": saved_use_overwrite,
    "use_placeholder": saved_use_placeholder,
    "use_compositing": saved_use_compositing,
    "film_transparent": saved_film_transparent,
    "thumbnails": thumbnails,
    "camera_settings": camera_settings,
    "camera_keyframes": camera_keyframes,
}
print("BRH_JSON:" + json.dumps(data, ensure_ascii=False))
