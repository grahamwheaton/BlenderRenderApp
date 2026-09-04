bl_info = {
    "name": "Blender Render Queue Sender",
    "author": "Graham Wheaton / OpenAI",
    "version": (5, 2, 0),
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


def build_job(context, render_mode, viewport_shading="SOLID"):
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
    user, machine = sender_stamp()
    return {
        "version": 1,
        "jobId": str(uuid.uuid4()),
        "blendFile": blend_file,
        "cameraName": camera.name,
        "startFrame": int(scene.frame_start),
        "endFrame": int(scene.frame_end),
        "frameStep": int(scene.frame_step),
        "outputPath": bpy.path.abspath(scene.render.filepath),
        "engine": scene.render.engine,
        "width": width,
        "height": height,
        "scale": scale,
        "frameRate": scene.render.fps / scene.render.fps_base,
        "format": scene.render.image_settings.file_format,
        "renderMode": render_mode,
        "viewportShading": viewport_shading,
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
        layout.label(text="Blender Render Queue Sender - Version 5.2.0", icon="INFO")
        layout.prop(self, "shared_queue_folder")
        layout.prop(self, "save_before_sending")
        layout.label(text="Use the same NAS queue folder in the V3 desktop app.")


def write_cloud_job(context, render_mode, viewport_shading="SOLID", extra=None):
    folder_text = clean_path(prefs().shared_queue_folder)
    if not folder_text:
        raise RuntimeError("Set the Shared NAS Queue Folder in this add-on's preferences first.")
    folder = Path(folder_text)
    folder.mkdir(parents=True, exist_ok=True)
    job = build_job(context, render_mode, viewport_shading)
    if extra:
        job.update(extra)
    filename = f"{time.strftime('%Y%m%d_%H%M%S')}_{job['jobId']}.renderjob.json"
    temporary = folder / ("." + filename + ".tmp")
    temporary.write_text(json.dumps(job, indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(str(temporary), str(folder / filename))
    return job


class RENDERQUEUE_OT_cloud_render(Operator):
    bl_idname = "render.cloud_render"
    bl_label = "Cloud Render (V5.2)"
    bl_description = "Send the active camera to every renderer watching the shared NAS queue"

    def execute(self, context):
        try:
            job = write_cloud_job(context, "FINAL")
            self.report({"INFO"}, f"Sent {job['cameraName']} as a Cloud Render")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class RENDERQUEUE_OT_cloud_playblast(Operator):
    bl_idname = "view3d.cloud_playblast"
    bl_label = "Cloud Playblast (V5.2)"
    bl_description = "Send a true viewport playblast job to a listening Blender Render app"

    def execute(self, context):
        try:
            if context.area is None or context.area.type != "VIEW_3D":
                raise RuntimeError("Cloud Playblast must be run from the 3D View > View menu.")
            if context.scene.render.image_settings.file_format == "FFMPEG":
                raise RuntimeError("V5 Cloud Playblast currently requires an image format such as JPEG or PNG, not FFmpeg.")
            shading = getattr(getattr(context.space_data, "shading", None), "type", "SOLID")
            job = write_cloud_job(context, "PLAYBLAST", shading, {
                "version": 3,
                "distributed": True,
                "requiresViewport": True,
            })
            self.report({"INFO"}, f"Sent {job['cameraName']} viewport playblast to the render queue")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


def draw_render_menu(self, context):
    self.layout.separator()
    self.layout.operator(RENDERQUEUE_OT_cloud_render.bl_idname, icon="NETWORK_DRIVE")


def draw_view_menu(self, context):
    self.layout.separator()
    self.layout.operator(RENDERQUEUE_OT_cloud_playblast.bl_idname, icon="RENDER_ANIMATION")


classes = (
    RENDERQUEUE_Preferences,
    RENDERQUEUE_OT_cloud_render,
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
