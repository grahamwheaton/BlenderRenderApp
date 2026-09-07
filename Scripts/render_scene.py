import bpy
import csv
from fractions import Fraction
import getpass
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
camera_name, start_text, end_text, step_text, output_path, engine, width, height, scale, frame_rate, file_format, render_mode, overwrite, placeholders, ignore_compositor, transparent_background, viewport_shading, distributed_text, job_id, coordination_folder, compositor_output_text, compositor_output_node = args
camera = bpy.data.objects.get(camera_name)
if camera is None or camera.type != 'CAMERA':
    raise RuntimeError(f"Camera not found: {camera_name}")

scene = bpy.context.scene
scene.camera = camera
scene.frame_start = int(start_text)
scene.frame_end = int(end_text)
scene.frame_step = int(step_text)
scene.render.filepath = output_path
uses_compositor_output = compositor_output_text == '1'
compositor_node = None
if uses_compositor_output:
    tree = getattr(scene, 'compositing_node_group', None) or getattr(scene, 'node_tree', None)
    compositor_node = tree.nodes.get(compositor_output_node) if tree else None
    if compositor_node is None or compositor_node.bl_idname != 'CompositorNodeOutputFile' or compositor_node.mute:
        raise RuntimeError(f"Compositor File Output node is unavailable: {compositor_output_node}")
    directory, filename = os.path.split(output_path)
    if hasattr(compositor_node, 'directory'):
        compositor_node.directory = directory
        compositor_node.file_name = filename
    else:
        compositor_node.base_path = directory
        if compositor_node.file_slots:
            compositor_node.file_slots[0].path = filename
    compositor_node.format.file_format = file_format
    if hasattr(scene.render, 'save_output'):
        scene.render.save_output = False
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
print(f"BRH: Mode {render_mode} | camera {camera_name} | frames {scene.frame_start}-{scene.frame_end} step {scene.frame_step} | {scene.render.resolution_x}x{scene.render.resolution_y} at {scene.render.resolution_percentage}% | {scene.render.fps / scene.render.fps_base:g} fps | {scene.render.engine} | {scene.render.image_settings.file_format} | overwrite={scene.render.use_overwrite} | placeholders={scene.render.use_placeholder} | compositor={scene.render.use_compositing} | compositor_output={uses_compositor_output} | transparent={scene.render.film_transparent} | output {scene.render.filepath}")

def compositor_frame_path(frame):
    if compositor_node is None:
        return None
    if hasattr(compositor_node, 'directory'):
        directory = bpy.path.abspath(compositor_node.directory)
        filename = compositor_node.file_name
        items = compositor_node.file_output_items
        item_name = items[0].name if len(items) else ''
    else:
        directory = bpy.path.abspath(compositor_node.base_path)
        slots = compositor_node.file_slots
        filename = slots[0].path if len(slots) else ''
        item_name = ''
    def replace_hashes(match):
        return str(frame).zfill(len(match.group(0)))
    if '#' in filename:
        filename = re.sub(r'#+', replace_hashes, filename)
    else:
        filename += str(frame).zfill(4)
    extension = {
        'BMP': '.bmp', 'IRIS': '.rgb', 'PNG': '.png', 'JPEG': '.jpg',
        'JPEG2000': '.jp2', 'TARGA': '.tga', 'TARGA_RAW': '.tga',
        'CINEON': '.cin', 'DPX': '.dpx', 'OPEN_EXR_MULTILAYER': '.exr',
        'OPEN_EXR': '.exr', 'HDR': '.hdr', 'TIFF': '.tif', 'WEBP': '.webp'
    }.get(compositor_node.format.file_format, scene.render.file_extension)
    return os.path.join(directory, filename + item_name + extension)

def report_completed_frame(render_scene):
    rendered_path = compositor_frame_path(render_scene.frame_current) if uses_compositor_output else bpy.path.abspath(render_scene.render.frame_path(frame=render_scene.frame_current))
    print(f"BRH_FRAME_DONE:{render_scene.frame_current}|{rendered_path}", flush=True)

def valid_output(frame):
    path = compositor_frame_path(frame) if uses_compositor_output else bpy.path.abspath(scene.render.frame_path(frame=frame))
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
    worker_user = getpass.getuser()
    worker_domain = os.environ.get('USERDOMAIN', '').strip()
    worker_author = f'{worker_domain}\\{worker_user}' if worker_domain else worker_user

    def done_path(frame):
        return os.path.join(claim_dir, f'{frame}.done')

    def frame_complete(frame):
        if not valid_output(frame):
            return False
        return os.path.exists(done_path(frame)) or not replace_existing

    def frame_output_path(frame):
        return compositor_frame_path(frame) if uses_compositor_output else bpy.path.abspath(scene.render.frame_path(frame=frame))

    def write_render_report():
        output_directory = os.path.dirname(frame_output_path(frames[0]))
        report_name = f"render-report-{re.sub(r'[^A-Za-z0-9_.-]+', '_', camera_name)}-{safe_job_id[:8]}.csv"
        report_path = os.path.join(output_directory, report_name)
        temporary_report = report_path + '.' + uuid.uuid4().hex + '.tmp'
        try:
            os.makedirs(output_directory, exist_ok=True)
            with open(temporary_report, 'w', newline='', encoding='utf-8-sig') as report:
                writer = csv.writer(report)
                writer.writerow(['Frame', 'Rendered by', 'Machine', 'Completed', 'Output file'])
                for frame in frames:
                    data = {}
                    try:
                        with open(done_path(frame), 'r', encoding='utf-8') as as_done:
                            data = json.load(as_done)
                    except (OSError, json.JSONDecodeError):
                        pass
                    completed_at = data.get('completed_at')
                    completed_text = time.strftime('%Y-%m-%d %H:%M:%S', time.localtime(completed_at)) if isinstance(completed_at, (int, float)) else ''
                    writer.writerow([frame, data.get('author', 'Pre-existing / unknown'), data.get('machine', ''), completed_text, data.get('output', frame_output_path(frame))])
            os.replace(temporary_report, report_path)
            print(f'BRH: Render accountability report: {report_path}', flush=True)
        except Exception as error:
            try:
                os.unlink(temporary_report)
            except OSError:
                pass
            print(f'BRH: Could not write accountability report: {error}', flush=True)

    while True:
        for frame in frames:
            if frame not in reported and frame_complete(frame):
                reported.add(frame)
                path = compositor_frame_path(frame) if uses_compositor_output else bpy.path.abspath(scene.render.frame_path(frame=frame))
                print(f"BRH_FRAME_DONE:{frame}|{path}|{len(reported)}|{len(frames)}", flush=True)
        active_claims = [name for name in os.listdir(claim_dir) if name.endswith('.claim')]
        if len(reported) == len(frames) and not active_claims:
            write_render_report()
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
                    if uses_compositor_output:
                        bpy.ops.render.render()
                    else:
                        frame_output = bpy.path.abspath(scene.render.frame_path(frame=frame))
                        scene.render.filepath = frame_output
                        try:
                            bpy.ops.render.render(write_still=True)
                        finally:
                            scene.render.filepath = base_filepath
                if not valid_output(frame):
                    raise RuntimeError(f"Frame {frame} did not produce a non-empty output file.")
                temporary_done = done_path(frame) + '.' + uuid.uuid4().hex + '.tmp'
                path = frame_output_path(frame)
                with open(temporary_done, 'w', encoding='utf-8') as done_file:
                    json.dump({"completed_at": time.time(), "author": worker_author, "machine": socket.gethostname(), "pid": os.getpid(), "output": path}, done_file)
                os.replace(temporary_done, done_path(frame))
                reported.add(frame)
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
