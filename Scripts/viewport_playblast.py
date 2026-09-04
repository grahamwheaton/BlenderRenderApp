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
    raise RuntimeError("Shared viewport playblasts require an image-sequence format, not FFmpeg.")

scene.camera = camera
scene.frame_start, scene.frame_end, scene.frame_step = int(start_text), int(end_text), max(1, int(step_text))
scene.render.filepath = output_path
scene.render.resolution_x, scene.render.resolution_y = int(width), int(height)
scene.render.resolution_percentage = int(scale)
fps_fraction = Fraction(frame_rate).limit_denominator(1001)
scene.render.fps, scene.render.fps_base = fps_fraction.numerator, fps_fraction.denominator
scene.render.image_settings.file_format = file_format
scene.render.film_transparent = False

def safe_name(value):
    return "".join(c if c.isalnum() or c in "-_." else "_" for c in value) or "invalid-job"

claim_folder = Path(coordination_folder) / "_claims" / safe_name(job_id)
claim_folder.mkdir(parents=True, exist_ok=True)
complete_path = claim_folder / "complete.json"
frames = list(range(scene.frame_start, scene.frame_end + 1, scene.frame_step))
base_filepath = scene.render.filepath
replace_existing = overwrite == "1"

def done_path(frame):
    return claim_folder / f"{frame}.done"

def frame_output(frame):
    previous = scene.render.filepath
    scene.render.filepath = base_filepath
    try:
        return Path(bpy.path.abspath(scene.render.frame_path(frame=frame)))
    finally:
        scene.render.filepath = previous

def valid_output(frame):
    try:
        path = frame_output(frame)
        return path.is_file() and path.stat().st_size > 0
    except OSError:
        return False

def frame_complete(frame):
    return valid_output(frame) and (done_path(frame).exists() or not replace_existing)

def claim_frame(frame):
    claim_path = claim_folder / f"{frame}.claim"
    try:
        descriptor = os.open(claim_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        with os.fdopen(descriptor, "w", encoding="utf-8") as claim:
            json.dump({"machine": socket.gethostname(), "pid": os.getpid(), "claimed_at": time.time()}, claim)
        return claim_path
    except FileExistsError:
        try:
            if time.time() - claim_path.stat().st_mtime <= 120:
                return None
            stale = claim_path.with_name(claim_path.name + ".stale." + uuid.uuid4().hex)
            os.replace(claim_path, stale)
            stale.unlink(missing_ok=True)
            return claim_frame(frame)
        except OSError:
            return None

def heartbeat(claim_path, stop_event):
    while not stop_event.wait(10):
        try:
            os.utime(claim_path, None)
        except OSError:
            return

def find_viewport():
    for window in bpy.context.window_manager.windows:
        for area in window.screen.areas:
            if area.type == "VIEW_3D":
                region = next((item for item in area.regions if item.type == "WINDOW"), None)
                if region is not None:
                    return window, area, region, area.spaces.active
    raise RuntimeError("The Blender worker could not create a 3D View for viewport capture.")

def copy_frame(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    last_error = None
    for attempt in range(3):
        temporary = destination.with_name(destination.name + "." + uuid.uuid4().hex + ".tmp")
        try:
            shutil.copyfile(source, temporary)
            os.replace(temporary, destination)
            if destination.stat().st_size <= 0:
                raise RuntimeError("copied file is empty")
            return
        except Exception as error:
            last_error = error
            temporary.unlink(missing_ok=True)
            if attempt < 2:
                time.sleep(1 + attempt)
    raise RuntimeError(f"Could not save {destination}: {last_error}")

def mark_done(frame):
    temporary = done_path(frame).with_name(f"{frame}.done.{uuid.uuid4().hex}.tmp")
    temporary.write_text(json.dumps({"completed_at": time.time(), "machine": socket.gethostname(), "pid": os.getpid()}), encoding="utf-8")
    os.replace(temporary, done_path(frame))

def mark_complete():
    temporary = complete_path.with_name("complete." + uuid.uuid4().hex + ".tmp")
    temporary.write_text(json.dumps({"completed_at": time.time(), "machine": socket.gethostname(), "frames": len(frames), "viewport": True}), encoding="utf-8")
    try:
        os.replace(temporary, complete_path)
    except OSError:
        temporary.unlink(missing_ok=True)
        if not complete_path.exists():
            raise

def run_distributed_capture():
    window, area, region, space = find_viewport()
    space.shading.type = viewport_shading if viewport_shading in {"WIREFRAME", "SOLID", "MATERIAL", "RENDERED"} else "SOLID"
    space.region_3d.view_perspective = "CAMERA"
    reported = set()
    with tempfile.TemporaryDirectory(prefix="blender_render_viewport_") as capture_folder:
        while True:
            for frame in frames:
                if frame not in reported and frame_complete(frame):
                    reported.add(frame)
                    print(f"BRH_FRAME_DONE:{frame}|{frame_output(frame)}|{len(reported)}|{len(frames)}", flush=True)
            active_claims = list(claim_folder.glob("*.claim"))
            if len(reported) == len(frames) and not active_claims:
                mark_complete()
                return
            claimed_any = False
            for frame in frames:
                if frame_complete(frame):
                    continue
                claim_path = claim_frame(frame)
                if claim_path is None:
                    continue
                claimed_any = True
                stop_heartbeat = threading.Event()
                heartbeat_thread = threading.Thread(target=heartbeat, args=(claim_path, stop_heartbeat), daemon=True)
                heartbeat_thread.start()
                try:
                    if replace_existing or not valid_output(frame):
                        scene.frame_set(frame)
                        staged_base = Path(capture_folder) / f"frame_{frame}"
                        scene.render.filepath = str(staged_base)
                        with bpy.context.temp_override(window=window, screen=window.screen, area=area, region=region, space_data=space):
                            result = bpy.ops.render.opengl(write_still=True, view_context=True)
                        if "FINISHED" not in result:
                            raise RuntimeError(f"Blender cancelled viewport frame {frame}.")
                        staged = Path(str(staged_base) + scene.render.file_extension)
                        if not staged.is_file() or staged.stat().st_size <= 0:
                            raise RuntimeError(f"Viewport capture did not create frame {frame}.")
                        copy_frame(staged, frame_output(frame))
                        scene.render.filepath = base_filepath
                    if not valid_output(frame):
                        raise RuntimeError(f"Frame {frame} did not produce a non-empty output file.")
                    mark_done(frame)
                    reported.add(frame)
                    print(f"BRH_FRAME_DONE:{frame}|{frame_output(frame)}|{len(reported)}|{len(frames)}", flush=True)
                finally:
                    scene.render.filepath = base_filepath
                    stop_heartbeat.set()
                    heartbeat_thread.join(timeout=1)
                    claim_path.unlink(missing_ok=True)
            if not claimed_any:
                time.sleep(2)

try:
    run_distributed_capture()
except Exception:
    traceback.print_exc()
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(1)
sys.stdout.flush()
os._exit(0)
