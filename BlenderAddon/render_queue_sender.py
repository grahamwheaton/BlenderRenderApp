bl_info = {
    "name": "Blender Render Queue Sender",
    "author": "Graham Wheaton / OpenAI",
    "version": (5, 9, 0),
    "blender": (4, 0, 0),
    "location": "Render menu",
    "description": "Send the active camera to Blender Render Watch mode locally or through a shared NAS queue",
    "category": "Render",
}

import bpy
import getpass
import json
import os
import re
import socket
import time
import uuid
from pathlib import Path
from bpy.props import BoolProperty, StringProperty, IntProperty
from bpy.types import AddonPreferences, Operator

def prefs():
    return bpy.context.preferences.addons[__name__].preferences


def clean_path(path):
    return bpy.path.abspath(path).strip().strip('"') if path else ""


def sender_stamp():
    try:
        user = getpass.getuser()
    except Exception:
        user = "Unknown"
    try:
        machine = socket.gethostname()
    except Exception:
        machine = "Unknown"
    return user, machine


def camera_resolution(scene, camera):
    width = scene.render.resolution_x
    height = scene.render.resolution_y
    scale = scene.render.resolution_percentage
    if hasattr(camera.data, "per_camera_resolution"):
        per_camera = camera.data.per_camera_resolution
        if getattr(per_camera, "use_custom_resolution", False):
            width = per_camera.resolution_x
            height = per_camera.resolution_y
            scale = per_camera.resolution_percentage
    return int(width), int(height), int(scale)


def overlay_settings(space_data):
    overlay = getattr(space_data, "overlay", None)
    if overlay is None:
        return {}
    settings = {}
    for prop in overlay.bl_rna.properties:
        if prop.identifier == "rna_type" or prop.is_readonly:
            continue
        try:
            value = getattr(overlay, prop.identifier)
            if isinstance(value, (bool, int, float, str)):
                settings[prop.identifier] = value
        except Exception:
            pass
    return settings


def render_output(scene):
    if getattr(scene.render, "save_output", True):
        return bpy.path.abspath(scene.render.filepath), False, "", scene.render.image_settings.file_format
    tree = getattr(scene, "compositing_node_group", None) or getattr(scene, "node_tree", None)
    if tree is None:
        raise RuntimeError("Output is disabled and the compositor has no File Output node tree.")
    nodes = [node for node in tree.nodes if node.bl_idname == "CompositorNodeOutputFile" and not node.mute]
    linked = [node for node in nodes if any(socket.is_linked for socket in node.inputs)]
    node = (linked or nodes or [None])[0]
    if node is None:
        raise RuntimeError("Output is disabled and no enabled compositor File Output node was found.")
    directory = getattr(node, "directory", None)
    filename = getattr(node, "file_name", None)
    if directory is None:
        directory = getattr(node, "base_path", "")
        slots = getattr(node, "file_slots", None)
        filename = slots[0].path if slots and len(slots) else ""
    combined = os.path.join(directory or "", filename or "")
    if not combined:
        raise RuntimeError(f"Compositor File Output node '{node.name}' has no output path.")
    node_format = getattr(getattr(node, "format", None), "file_format", scene.render.image_settings.file_format)
    return bpy.path.abspath(combined), True, node.name, node_format


def save_shared_scene():
    """Keep one shared file; never publish a job after an unsuccessful save."""
    if not bpy.data.is_dirty:
        return
    if not prefs().save_before_sending:
        raise RuntimeError("The Blender file has unsaved changes. Save it before sending, or enable Save Before Sending.")
    for attempt in range(3):
        try:
            result = bpy.ops.wm.save_mainfile()
            if "FINISHED" not in result or bpy.data.is_dirty:
                raise RuntimeError("Blender did not confirm the scene was saved.")
            return
        except RuntimeError as error:
            transient = any(term in str(error).lower() for term in (
                "backup", "cannot change old file", "access is denied", "permission", "being used by another process"))
            if not transient or attempt == 2:
                raise RuntimeError(
                    "Job NOT sent: Blender could not save the shared scene. "
                    "Check NAS permissions/file locks, or use Save As to make a manual copy, then send again. "
                    f"Original error: {error}"
                ) from error
            time.sleep(attempt + 1)


def build_job(context, render_mode, viewport_shading="SOLID", viewport_overlays=None, current_frame_only=False, camera_override=None, save=True):
    scene = context.scene
    camera = camera_override or scene.camera
    if camera is None or camera.type != "CAMERA":
        raise RuntimeError("The scene does not have an active camera.")
    if not bpy.data.filepath:
        raise RuntimeError("Save the Blender file before sending it to the render queue.")
    if save:
        save_shared_scene()

    blend_file = str(Path(bpy.data.filepath).resolve())
    if not Path(blend_file).is_file():
        raise RuntimeError(f"The Blender file is not accessible: {blend_file}")
    width, height, scale = camera_resolution(scene, camera)
    output_path, uses_compositor_output, compositor_output_node, output_format = render_output(scene)
    user, machine = sender_stamp()
    start_frame = int(scene.frame_current) if current_frame_only else int(scene.frame_start)
    end_frame = int(scene.frame_current) if current_frame_only else int(scene.frame_end)
    return {
        "version": 1,
        "jobId": str(uuid.uuid4()),
        "blendFile": blend_file,
        "cameraName": camera.name,
        "startFrame": start_frame,
        "endFrame": end_frame,
        "frameStep": int(scene.frame_step),
        "outputPath": output_path,
        "usesCompositorOutput": uses_compositor_output,
        "compositorOutputNode": compositor_output_node,
        "engine": scene.render.engine,
        "width": width,
        "height": height,
        "scale": scale,
        "frameRate": scene.render.fps / scene.render.fps_base,
        "format": output_format,
        "renderMode": render_mode,
        "viewportShading": viewport_shading,
        "showOverlays": bool((viewport_overlays or {}).get("show_overlays", False)),
        "viewportOverlaySettings": viewport_overlays or {},
        "distributed": True,
        "overwrite": bool(scene.render.use_overwrite),
        "placeholders": bool(scene.render.use_placeholder),
        "ignoreCompositor": not bool(scene.render.use_compositing),
        "transparentBackground": bool(scene.render.film_transparent),
        "senderUser": user,
        "senderMachine": machine,
        "sentAt": time.strftime("%Y-%m-%dT%H:%M:%S"),
    }


class RENDERQUEUE_Preferences(AddonPreferences):
    bl_idname = __name__
    tile_size: IntProperty(name="Tile size (pixels)", default=2048, min=128, max=8192)
    tile_overlap: IntProperty(name="Tile overlap (pixels)", default=128, min=0, max=512)

    shared_queue_folder: StringProperty(
        name="Shared NAS Queue Folder",
        description="Folder watched by Blender Render apps on the NAS",
        subtype="DIR_PATH",
        default=r"W:\Working Graphics\_3D RESOURCE\CloudRender",
    )
    save_before_sending: BoolProperty(
        name="Save Before Sending",
        description="Save the current .blend so the renderer receives the latest changes",
        default=True,
    )

    def draw(self, context):
        layout = self.layout
        layout.label(text="Blender Render Queue Sender - Version 5.9.0", icon="INFO")
        layout.prop(self, "shared_queue_folder")
        layout.prop(self, "save_before_sending")
        layout.label(text="Cloud Image (tiled) settings — requires app V5.8")
        layout.prop(self, "tile_size")
        layout.prop(self, "tile_overlap")
        layout.label(text="Cycles stills; one multilayer EXR compositor output.")
        layout.label(text="Use the same NAS queue folder in the V3 desktop app.")


def write_cloud_job(context, render_mode, viewport_shading="SOLID", extra=None, viewport_overlays=None, current_frame_only=False):
    folder_text = clean_path(prefs().shared_queue_folder)
    if not folder_text:
        raise RuntimeError("Set the Shared NAS Queue Folder in this add-on's preferences first.")
    folder = Path(folder_text)
    folder.mkdir(parents=True, exist_ok=True)
    job = build_job(context, render_mode, viewport_shading, viewport_overlays, current_frame_only)
    if extra:
        job.update(extra)
    extension = 'tilejob.json' if job.get('tileSize', 0) else 'renderjob.json'
    filename = f"{time.strftime('%Y%m%d_%H%M%S')}_{job['jobId']}.{extension}"
    temporary = folder / ("." + filename + ".tmp")
    temporary.write_text(json.dumps(job, indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(str(temporary), str(folder / filename))
    return job


def selection_output_path(path, camera_name, blend_file):
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', camera_name).rstrip(' .') or 'Camera'
    path = path.replace('{blend_name}', Path(blend_file).stem)
    if '{camera_name}' in path:
        return path.replace('{camera_name}', name)
    directory, filename = os.path.split(path)
    # Keep frame padding/extension at the end; prefix only the filename.
    return os.path.join(directory, name + '_' + filename)


def write_camera_selection(context):
    cameras = sorted((obj for obj in context.selected_objects if obj.type == 'CAMERA' and obj.name in context.scene.objects), key=lambda obj: obj.name)
    if not cameras:
        raise RuntimeError('Select one or more camera objects in the current scene first.')
    if not bpy.data.filepath:
        raise RuntimeError('Save the Blender file before sending it to the render queue.')
    folder_text = clean_path(prefs().shared_queue_folder)
    if not folder_text:
        raise RuntimeError('Set the Shared NAS Queue Folder first.')
    save_shared_scene()
    batch_id = str(uuid.uuid4())
    jobs = []
    paths = set()
    for camera in cameras:
        job = build_job(context, 'FINAL', current_frame_only=True, camera_override=camera, save=False)
        tree = getattr(context.scene, 'compositing_node_group', None) or getattr(context.scene, 'node_tree', None)
        if tree and not job['ignoreCompositor']:
            for node in tree.nodes:
                if node.bl_idname != 'CompositorNodeOutputFile' or node.mute or node.name == job['compositorOutputNode']:
                    continue
                raw = str(getattr(node, 'directory', getattr(node, 'base_path', ''))) + str(getattr(node, 'file_name', ''))
                slots = getattr(node, 'file_slots', [])
                if '{camera_name}' not in raw and not (slots and all('{camera_name}' in slot.path for slot in slots)):
                    raise RuntimeError(f"Additional compositor output '{node.name}' needs {{camera_name}} in its path to avoid camera images overwriting each other.")
        if job['format'] == 'FFMPEG':
            raise RuntimeError('Camera selection requires an image output format, not FFmpeg.')
        job['outputPath'] = selection_output_path(job['outputPath'], camera.name, job['blendFile'])
        canonical = os.path.normcase(os.path.normpath(job['outputPath'])).casefold()
        if canonical in paths:
            raise RuntimeError('Selected camera names produce identical output paths. Rename these cameras before sending.')
        paths.add(canonical)
        job['batchId'] = batch_id
        jobs.append(job)
    folder = Path(folder_text)
    folder.mkdir(parents=True, exist_ok=True)
    filename = f'{time.strftime("%Y%m%d_%H%M%S")}_{batch_id}.camerabatch.json'
    temporary = folder / ('.' + filename + '.tmp')
    temporary.write_text(json.dumps({'version': 1, 'batchId': batch_id, 'jobs': jobs}, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, folder / filename)
    return jobs


class RENDERQUEUE_OT_cloud_render_image_selection(Operator):
    bl_idname = 'render.cloud_render_image_selection'
    bl_label = 'Cloud Render Image Selection'
    bl_description = 'Queue the current frame from every selected camera; workers load the shared scene once per batch (launcher 6.6+)'

    def execute(self, context):
        try:
            jobs = write_camera_selection(context)
            self.report({'INFO'}, f'Sent {len(jobs)} selected cameras as a Cloud Image batch')
            return {'FINISHED'}
        except Exception as error:
            self.report({'ERROR'}, str(error))
            return {'CANCELLED'}


class RENDERQUEUE_OT_cloud_render_animation(Operator):
    bl_idname = "render.cloud_render_animation"
    bl_label = "Cloud Render Animation"
    bl_description = "Send the complete scene frame range to every renderer watching the shared NAS queue"

    def execute(self, context):
        try:
            job = write_cloud_job(context, "FINAL")
            self.report({"INFO"}, f"Sent {job['cameraName']} frames {job['startFrame']}-{job['endFrame']} as a Cloud Render Animation")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class RENDERQUEUE_OT_cloud_render_image(Operator):
    bl_idname = "render.cloud_render_image"
    bl_label = "Cloud Render Image"
    bl_description = "Send only the current frame to every renderer watching the shared NAS queue"

    def execute(self, context):
        try:
            job = write_cloud_job(context, "FINAL", current_frame_only=True)
            self.report({"INFO"}, f"Sent {job['cameraName']} frame {job['startFrame']} as a Cloud Render Image")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class RENDERQUEUE_OT_cloud_render_tiled(Operator):
    bl_idname = "render.cloud_render_tiled"
    bl_label = "Cloud Image (tiled)"
    bl_description = "Distribute the current Cycles frame as overlapping tiles and assemble a multilayer EXR (experimental)"

    def execute(self, context):
        try:
            if context.scene.render.engine != 'CYCLES':
                raise RuntimeError('Experimental tiles require Cycles.')
            if prefs().tile_overlap > prefs().tile_size:
                raise RuntimeError('Tile overlap cannot exceed tile size.')
            extra = {'tileSize': prefs().tile_size, 'tileOverlap': prefs().tile_overlap, 'version': 4}
            job = write_cloud_job(context, "FINAL", extra=extra, current_frame_only=True)
            self.report({'INFO'}, f"Sent {job['cameraName']} frame {job['startFrame']} as tiles")
            return {'FINISHED'}
        except Exception as error:
            self.report({'ERROR'}, str(error))
            return {'CANCELLED'}


class RENDERQUEUE_OT_cloud_playblast(Operator):
    bl_idname = "view3d.cloud_playblast"
    bl_label = "Cloud Render Playblast"
    bl_description = "Send a true viewport playblast job to a listening Blender Render app"

    def execute(self, context):
        try:
            if context.area is None or context.area.type != "VIEW_3D":
                raise RuntimeError("Cloud Playblast must be run from the 3D View > View menu.")
            if context.scene.render.image_settings.file_format == "FFMPEG":
                raise RuntimeError("V5 Cloud Playblast currently requires an image format such as JPEG or PNG, not FFmpeg.")
            shading = getattr(getattr(context.space_data, "shading", None), "type", "SOLID")
            overlays = overlay_settings(context.space_data)
            job = write_cloud_job(context, "PLAYBLAST", shading, {
                "version": 3,
                "distributed": True,
                "requiresViewport": True,
            }, overlays)
            self.report({"INFO"}, f"Sent {job['cameraName']} viewport playblast to the render queue")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


def draw_render_menu(self, context):
    self.layout.separator()
    self.layout.operator(RENDERQUEUE_OT_cloud_render_image.bl_idname, icon="RENDER_STILL")
    self.layout.operator(RENDERQUEUE_OT_cloud_render_image_selection.bl_idname, icon="OUTLINER_OB_CAMERA")
    self.layout.operator(RENDERQUEUE_OT_cloud_render_tiled.bl_idname, icon="IMAGE_DATA")
    self.layout.operator(RENDERQUEUE_OT_cloud_render_animation.bl_idname, icon="RENDER_ANIMATION")


def draw_view_menu(self, context):
    self.layout.separator()
    self.layout.operator(RENDERQUEUE_OT_cloud_playblast.bl_idname, icon="RENDER_ANIMATION")


classes = (
    RENDERQUEUE_Preferences,
    RENDERQUEUE_OT_cloud_render_image,
    RENDERQUEUE_OT_cloud_render_image_selection,
    RENDERQUEUE_OT_cloud_render_tiled,
    RENDERQUEUE_OT_cloud_render_animation,
    RENDERQUEUE_OT_cloud_playblast,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.TOPBAR_MT_render.append(draw_render_menu)
    bpy.types.VIEW3D_MT_view.append(draw_view_menu)


def unregister():
    bpy.types.VIEW3D_MT_view.remove(draw_view_menu)
    bpy.types.TOPBAR_MT_render.remove(draw_render_menu)
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
