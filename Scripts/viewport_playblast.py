import bpy
from fractions import Fraction
import json
import os
from pathlib import Path
import shutil
import socket
import sys
import tempfile
import threading
import time
import traceback
import uuid


args = sys.argv[sys.argv.index("--") + 1:]
camera_name, start_text, end_text, step_text, output_path, width, height, scale, frame_rate, file_format, viewport_shading, overwrite, job_id, coordination_folder = args
scene = bpy.context.scene
camera = bpy.data.objects.get(camera_name)
if camera is None or camera.type != "CAMERA":
    raise RuntimeError(f"Camera not found: {camera_name}")
if file_format == "FFMPEG":
    raise RuntimeError("True viewport playblast requires an image-sequence format, not FFmpeg.")

scene.camera = camera
scene.frame_start = int(start_text)
scene.frame_end = int(end_text)
scene.frame_step = max(1, int(step_text))
scene.render.filepath = output_path
scene.render.resolution_x = int(width)
scene.render.resolution_y = int(height)
scene.render.resolution_percentage = int(scale)
fps_fraction = Fraction(frame_rate).limit_denominator(1001)
scene.render.fps = fps_fraction.numerator
scene.render.fps_base = fps_fraction.denominator
scene.render.image_settings.file_format = file_format
scene.render.film_transparent = False


def safe_name(value):
    return "".join(character if character.isalnum() or character in "-_." else "_" for character in value) or "invalid-job"


claim_folder = Path(coordination_folder) / "_claims" / safe_name(job_id)
claim_folder.mkdir(parents=True, exist_ok=True)
claim_path = claim_folder / "viewport.claim"
complete_path = claim_folder / "complete.json"


def acquire_claim():
    while True:
        if complete_path.exists():
            return False
        try:
            descriptor = os.open(claim_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            with os.fdopen(descriptor, "w", encoding="utf-8") as claim:
                json.dump({"machine": socket.gethostname(), "pid": os.getpid(), "claimed_at": time.time()}, claim)
            return True
        except FileExistsError:
            try:
                if time.time() - claim_path.stat().st_mtime > 120:
                    stale = claim_path.with_name(claim_path.name + ".stale." + uuid.uuid4().hex)
                    os.replace(claim_path, stale)
                    stale.unlink(missing_ok=True)
                    continue
            except OSError:
                pass
            time.sleep(2)


def heartbeat(stop_event):
    while not stop_event.wait(10):
        try:
            os.utime(claim_path, None)
        except OSError:
            return


def find_viewport():
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type != "VIEW_3D":
                continue
            region = next((item for item in area.regions if item.type == "WINDOW"), None)
            if region is not None:
                return window, area, region, area.spaces.active
    raise RuntimeError("The Blender worker could not create a 3D View for viewport capture.")


def copy_frame(source, destination, allow_overwrite):
    target = Path(destination)
    target.parent.mkdir(parents=True, exist_ok=True)
    if target.is_file() and target.stat().st_size > 0 and not allow_overwrite:
        return
    last_error = None
    for attempt in range(3):
        temporary = target.with_name(target.name + "." + uuid.uuid4().hex + ".tmp")
        try:
            shutil.copyfile(source, temporary)
            os.replace(temporary, target)
            if target.stat().st_size <= 0:
                raise RuntimeError("copied file is empty")
            return
        except Exception as error:
            last_error = error
            temporary.unlink(missing_ok=True)
            if attempt < 2:
                time.sleep(1 + attempt)
    raise RuntimeError(f"Could not save {destination}: {last_error}")


def run_capture():
    if not acquire_claim():
        print("BRH: Viewport playblast already completed by another machine", flush=True)
        return
    stop_heartbeat = threading.Event()
    heartbeat_thread = threading.Thread(target=heartbeat, args=(stop_heartbeat,), daemon=True)
    heartbeat_thread.start()
    original_path = scene.render.filepath
    frames = list(range(scene.frame_start, scene.frame_end + 1, scene.frame_step))
    destinations = {frame: bpy.path.abspath(scene.render.frame_path(frame=frame)) for frame in frames}
    try:
        window, area, region, space = find_viewport()
        space.shading.type = viewport_shading if viewport_shading in {"WIREFRAME", "SOLID", "MATERIAL", "RENDERED"} else "SOLID"
        space.region_3d.view_perspective = "CAMERA"
        with tempfile.TemporaryDirectory(prefix="blender_render_viewport_") as capture_folder:
            scene.render.filepath = str(Path(capture_folder) / "frame_")

            def report_frame(render_scene):
                frame = render_scene.frame_current
                staged = bpy.path.abspath(render_scene.render.frame_path(frame=frame))
                print(f"BRH_FRAME_DONE:{frame}|{staged}", flush=True)

            bpy.app.handlers.render_write.append(report_frame)
            try:
                with bpy.context.temp_override(window=window, screen=window.screen, area=area, region=region, space_data=space):
                    result = bpy.ops.render.opengl(animation=True, view_context=True)
                if "FINISHED" not in result:
                    raise RuntimeError("Blender cancelled the viewport capture.")
            finally:
                if report_frame in bpy.app.handlers.render_write:
                    bpy.app.handlers.render_write.remove(report_frame)

            for index, frame in enumerate(frames, 1):
                source = bpy.path.abspath(scene.render.frame_path(frame=frame))
                if not Path(source).is_file() or Path(source).stat().st_size <= 0:
                    raise RuntimeError(f"Viewport capture did not create frame {frame}.")
                copy_frame(source, destinations[frame], overwrite == "1")
                print(f"BRH_FRAME_DONE:{frame}|{destinations[frame]}|{index}|{len(frames)}", flush=True)

        temporary_complete = complete_path.with_name("complete." + uuid.uuid4().hex + ".tmp")
        temporary_complete.write_text(json.dumps({"completed_at": time.time(), "machine": socket.gethostname(), "frames": len(frames), "viewport": True}), encoding="utf-8")
        os.replace(temporary_complete, complete_path)
    finally:
        scene.render.filepath = original_path
        stop_heartbeat.set()
        heartbeat_thread.join(timeout=1)
        claim_path.unlink(missing_ok=True)


try:
    run_capture()
except Exception:
    traceback.print_exc()
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(1)
sys.stdout.flush()
os._exit(0)
