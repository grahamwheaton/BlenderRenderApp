bl_info = {
    "name": "Blender Render Queue Sender",
    "author": "Graham Wheaton / OpenAI",
    "version": (5, 7, 0),
    "blender": (4, 0, 0),
    "location": "Render menu",
    "description": "Send the active camera to Blender Render Watch mode locally or through a shared NAS queue",
    "category": "Render",
}

import bpy
import getpass
import json
import os
import socket
import time
import uuid
from pathlib import Path
from bpy.props import BoolProperty, StringProperty
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


def build_job(context, render_mode, viewport_shading="SOLID", viewport_overlays=None, current_frame_only=False):
    scene = context.scene
    camera = scene.camera
    if camera is None or camera.type != "CAMERA":
        raise RuntimeError("The scene does not have an active camera.")
    if not bpy.data.filepath:
        raise RuntimeError("Save the Blender file before sending it to the render queue.")
    if prefs().save_before_sending:
        bpy.ops.wm.save_mainfile()
    elif bpy.data.is_dirty:
        raise RuntimeError("The Blender file has unsaved changes. Save it before sending, or enable Save Before Sending.")

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
        layout.label(text="Blender Render Queue Sender - Version 5.7.0", icon="INFO")
        layout.prop(self, "shared_queue_folder")
        layout.prop(self, "save_before_sending")
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
    filename = f"{time.strftime('%Y%m%d_%H%M%S')}_{job['jobId']}.renderjob.json"
    temporary = folder / ("." + filename + ".tmp")
    temporary.write_text(json.dumps(job, indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(str(temporary), str(folder / filename))
    return job


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
    self.layout.operator(RENDERQUEUE_OT_cloud_render_animation.bl_idname, icon="RENDER_ANIMATION")


def draw_view_menu(self, context):
    self.layout.separator()
    self.layout.operator(RENDERQUEUE_OT_cloud_playblast.bl_idname, icon="RENDER_ANIMATION")


classes = (
    RENDERQUEUE_Preferences,
    RENDERQUEUE_OT_cloud_render_image,
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
