bl_info = {
    "name": "Blender Render Queue Sender",
    "author": "Graham Wheaton / OpenAI",
    "version": (5, 0, 0),
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


def publish_cloud_job(job):
    folder_text = clean_path(prefs().shared_queue_folder)
    if not folder_text:
        raise RuntimeError("Set the Shared NAS Queue Folder in this add-on's preferences first.")
    folder = Path(folder_text)
    folder.mkdir(parents=True, exist_ok=True)
    filename = f"{time.strftime('%Y%m%d_%H%M%S')}_{job['jobId']}.renderjob.json"
    temporary = folder / ("." + filename + ".tmp")
    temporary.write_text(json.dumps(job, indent=2, ensure_ascii=False), encoding="utf-8")
    os.replace(str(temporary), str(folder / filename))


class RENDERQUEUE_OT_cloud_render(Operator):
    bl_idname = "render.cloud_render"
    bl_label = "Cloud Render"
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
    bl_label = "Cloud Playblast"
    bl_description = "Capture the active camera through this 3D View and publish the completed viewport playblast"

    def execute(self, context):
        try:
            if context.area is None or context.area.type != "VIEW_3D":
                raise RuntimeError("Cloud Playblast must be run from the 3D View > View menu.")
            scene = context.scene
            if scene.render.image_settings.file_format == "FFMPEG":
                raise RuntimeError("V5 Cloud Playblast currently requires an image format such as JPEG or PNG, not FFmpeg.")

            shading = getattr(getattr(context.space_data, "shading", None), "type", "SOLID")
            # Validate/save the source before spending time capturing it.
            job = build_job(context, "PLAYBLAST", shading)
            region_3d = context.space_data.region_3d
            window_region = next((region for region in context.area.regions if region.type == "WINDOW"), None)
            if window_region is None:
                raise RuntimeError("The active 3D View has no drawable window region.")
            previous_perspective = region_3d.view_perspective
            previous_frame = scene.frame_current
            try:
                # render.opengl with view_context=True is Blender's actual viewport
                # renderer. Run it here because a background Blender process has no
                # 3D View/window context to capture.
                region_3d.view_perspective = "CAMERA"
                with context.temp_override(area=context.area, region=window_region, space_data=context.space_data):
                    result = bpy.ops.render.opengl(animation=True, view_context=True)
                    if "FINISHED" not in result:
                        raise RuntimeError("Blender cancelled the viewport capture.")
            finally:
                region_3d.view_perspective = previous_perspective
                scene.frame_set(previous_frame)

            last_frame = scene.frame_start + ((scene.frame_end - scene.frame_start) // max(1, scene.frame_step)) * max(1, scene.frame_step)
            preview_path = bpy.path.abspath(scene.render.frame_path(frame=last_frame))
            if not Path(preview_path).is_file() or Path(preview_path).stat().st_size == 0:
                raise RuntimeError("Viewport capture finished but the final output frame could not be found.")
            job.update({
                "version": 2,
                "distributed": False,
                "preRendered": True,
                "previewPath": preview_path,
            })
            publish_cloud_job(job)
            self.report({"INFO"}, f"Captured and published {job['cameraName']} viewport playblast")
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
