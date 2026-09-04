bl_info = {
    "name": "Blender Render Queue Sender",
    "author": "Graham Wheaton / OpenAI",
    "version": (3, 0, 0),
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
import tempfile
import time
import uuid
from pathlib import Path
from bpy.props import BoolProperty, IntProperty, StringProperty
from bpy.types import AddonPreferences, Operator

LOCAL_HOST = "127.0.0.1"
DEFAULT_LOCAL_PORT = 43129


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


def build_job(context):
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
        "renderMode": "FINAL",
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
        default="",
    )
    local_port: IntProperty(
        name="Local Watch Port",
        description="Must match the Blender Render desktop app",
        default=DEFAULT_LOCAL_PORT,
        min=1024,
        max=65535,
    )
    save_before_sending: BoolProperty(
        name="Save Before Sending",
        description="Save the current .blend so the renderer receives the latest changes",
        default=True,
    )

    def draw(self, context):
        layout = self.layout
        layout.prop(self, "shared_queue_folder")
        layout.prop(self, "local_port")
        layout.prop(self, "save_before_sending")
        layout.label(text="Use the same NAS queue folder in the V3 desktop app.")


class RENDERQUEUE_OT_send_local(Operator):
    bl_idname = "render.send_to_queue_local"
    bl_label = "Send to Render Queue Local"
    bl_description = "Send the active camera to Watch mode on this computer"

    def execute(self, context):
        try:
            job = build_job(context)
            payload = json.dumps(job, ensure_ascii=False).encode("utf-8")
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                client.settimeout(2.0)
                client.sendto(payload, (LOCAL_HOST, prefs().local_port))
                acknowledgement, _address = client.recvfrom(128)
                if acknowledgement != b"BRQ_ACK":
                    raise RuntimeError("The local render app returned an invalid response.")
            self.report({"INFO"}, f"Sent {job['cameraName']} to the local render queue")
            return {"FINISHED"}
        except socket.timeout:
            self.report({"ERROR"}, "No local Blender Render app responded. Open V3 and enable Watch mode.")
            return {"CANCELLED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class RENDERQUEUE_OT_send_cloud(Operator):
    bl_idname = "render.send_to_queue_cloud"
    bl_label = "Send to Render Queue Cloud"
    bl_description = "Send the active camera to every renderer watching the shared NAS queue"

    def execute(self, context):
        try:
            folder_text = clean_path(prefs().shared_queue_folder)
            if not folder_text:
                raise RuntimeError("Set the Shared NAS Queue Folder in this add-on's preferences first.")
            folder = Path(folder_text)
            folder.mkdir(parents=True, exist_ok=True)
            job = build_job(context)
            filename = f"{time.strftime('%Y%m%d_%H%M%S')}_{job['jobId']}.renderjob.json"
            temporary = Path(tempfile.gettempdir()) / (filename + ".tmp")
            temporary.write_text(json.dumps(job, indent=2, ensure_ascii=False), encoding="utf-8")
            os.replace(str(temporary), str(folder / filename))
            self.report({"INFO"}, f"Sent {job['cameraName']} to the NAS render queue")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


def draw_render_menu(self, context):
    self.layout.separator()
    self.layout.operator(RENDERQUEUE_OT_send_local.bl_idname, icon="RENDER_ANIMATION")
    self.layout.operator(RENDERQUEUE_OT_send_cloud.bl_idname, icon="NETWORK_DRIVE")


classes = (
    RENDERQUEUE_Preferences,
    RENDERQUEUE_OT_send_local,
    RENDERQUEUE_OT_send_cloud,
)


def register():
    for cls in classes:
        bpy.utils.register_class(cls)
    bpy.types.TOPBAR_MT_render.append(draw_render_menu)


def unregister():
    bpy.types.TOPBAR_MT_render.remove(draw_render_menu)
    for cls in reversed(classes):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
