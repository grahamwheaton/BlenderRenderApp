import bpy
from fractions import Fraction
import json
import os
import re
import socket
import sys
import threading
import time
import traceback
import uuid

args = sys.argv[sys.argv.index("--") + 1:]
camera_name, start_text, end_text, step_text, output_path, engine, width, height, scale, frame_rate, file_format, render_mode, overwrite, placeholders, ignore_compositor, transparent_background, viewport_shading, distributed_text, job_id, coordination_folder = args
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
distributed = distributed_text == '1'
scene.render.use_overwrite = True if distributed else overwrite == '1'
scene.render.use_placeholder = False if distributed else placeholders == '1'
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

def valid_output(frame):
    path = bpy.path.abspath(scene.render.frame_path(frame=frame))
    try:
        return os.path.isfile(path) and os.path.getsize(path) > 0
    except OSError:
        return False

def claim_frame(claim_path):
    try:
        descriptor = os.open(claim_path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        with os.fdopen(descriptor, 'w', encoding='utf-8') as claim:
            json.dump({"machine": socket.gethostname(), "pid": os.getpid(), "claimed_at": time.time()}, claim)
        return True
    except FileExistsError:
        try:
            if time.time() - os.path.getmtime(claim_path) <= 120:
                return False
            stale_path = claim_path + ".stale." + uuid.uuid4().hex
            os.replace(claim_path, stale_path)
            os.unlink(stale_path)
            return claim_frame(claim_path)
        except (FileNotFoundError, PermissionError, OSError):
            return False

def heartbeat_claim(claim_path, stop_event):
    while not stop_event.wait(10):
        try:
            os.utime(claim_path, None)
        except OSError:
            return

def render_distributed():
    if scene.render.image_settings.file_format == 'FFMPEG':
        raise RuntimeError("NAS frame sharing requires an image-sequence format, not FFmpeg.")
    safe_job_id = re.sub(r'[^A-Za-z0-9_.-]+', '_', job_id) or 'invalid-job'
    claim_dir = os.path.join(coordination_folder, '_claims', safe_job_id)
    os.makedirs(claim_dir, exist_ok=True)
    complete_path = os.path.join(claim_dir, 'complete.json')
    frames = list(range(scene.frame_start, scene.frame_end + 1, max(1, scene.frame_step)))
    reported = set()
    base_filepath = scene.render.filepath
    replace_existing = overwrite == '1'

    def done_path(frame):
        return os.path.join(claim_dir, f'{frame}.done')

    def frame_complete(frame):
        if not valid_output(frame):
            return False
        return os.path.exists(done_path(frame)) or not replace_existing

    while True:
        for frame in frames:
            if frame not in reported and frame_complete(frame):
                reported.add(frame)
                path = bpy.path.abspath(scene.render.frame_path(frame=frame))
                print(f"BRH_FRAME_DONE:{frame}|{path}|{len(reported)}|{len(frames)}", flush=True)
        active_claims = [name for name in os.listdir(claim_dir) if name.endswith('.claim')]
        if len(reported) == len(frames) and not active_claims:
            temporary_complete = complete_path + '.' + uuid.uuid4().hex + '.tmp'
            with open(temporary_complete, 'w', encoding='utf-8') as complete_file:
                json.dump({"completed_at": time.time(), "machine": socket.gethostname(), "frames": len(frames)}, complete_file)
            try:
                os.replace(temporary_complete, complete_path)
            except OSError:
                if os.path.exists(complete_path):
                    try:
                        os.unlink(temporary_complete)
                    except OSError:
                        pass
                else:
                    raise
            return

        claimed_any = False
        for frame in frames:
            if frame_complete(frame):
                continue
            claim_path = os.path.join(claim_dir, f'{frame}.claim')
            if not claim_frame(claim_path):
                continue
            claimed_any = True
            stop_heartbeat = threading.Event()
            heartbeat = threading.Thread(target=heartbeat_claim, args=(claim_path, stop_heartbeat), daemon=True)
            heartbeat.start()
            try:
                if replace_existing or not valid_output(frame):
                    scene.frame_set(frame)
                    frame_output = bpy.path.abspath(scene.render.frame_path(frame=frame))
                    scene.render.filepath = frame_output
                    try:
                        bpy.ops.render.render(write_still=True)
                    finally:
                        scene.render.filepath = base_filepath
                if not valid_output(frame):
                    raise RuntimeError(f"Frame {frame} did not produce a non-empty output file.")
                temporary_done = done_path(frame) + '.' + uuid.uuid4().hex + '.tmp'
                with open(temporary_done, 'w', encoding='utf-8') as done_file:
                    json.dump({"completed_at": time.time(), "machine": socket.gethostname()}, done_file)
                os.replace(temporary_done, done_path(frame))
                reported.add(frame)
                path = bpy.path.abspath(scene.render.frame_path(frame=frame))
                print(f"BRH_FRAME_DONE:{frame}|{path}|{len(reported)}|{len(frames)}", flush=True)
            finally:
                stop_heartbeat.set()
                heartbeat.join(timeout=1)
                try:
                    os.unlink(claim_path)
                except OSError:
                    pass
        if not claimed_any:
            time.sleep(2)

try:
    if distributed:
        render_distributed()
    else:
        bpy.app.handlers.render_write.append(report_completed_frame)
        bpy.ops.render.render(animation=True)
except Exception:
    traceback.print_exc()
    sys.stdout.flush()
    sys.stderr.flush()
    os._exit(1)
sys.stdout.flush()
# Some third-party add-ons can hang or fail while Blender unregisters them in
# background mode. The render has completed and its files are closed at this point.
os._exit(0)
