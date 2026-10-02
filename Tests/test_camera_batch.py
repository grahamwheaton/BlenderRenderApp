"""Real Blender batch integration in a unique temp folder (never production scenes)."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
BLENDER = os.environ.get('BLENDER_EXE', r'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe')
work = Path(tempfile.mkdtemp(prefix='BRH-camera-batch-test-'))
blend = work / 'fixture.blend'
watch = work / 'CloudRender'
watch.mkdir()
setup = f'''
import bpy, importlib.util
from types import SimpleNamespace
s=importlib.util.spec_from_file_location('render_queue_sender', {str(ROOT / 'BlenderAddon' / 'render_queue_sender.py')!r})
m=importlib.util.module_from_spec(s); s.loader.exec_module(m); m.register()
m.prefs=lambda: SimpleNamespace(save_before_sending=True,shared_queue_folder={str(watch)!r})
scene=bpy.context.scene
scene.render.engine='BLENDER_WORKBENCH'
scene.render.resolution_x=64; scene.render.resolution_y=64; scene.render.resolution_percentage=100
scene.render.image_settings.file_format='PNG'
scene.render.filepath={str(work / 'output' / 'shot_')!r}
scene.frame_set(7)
original=scene.camera
for obj in bpy.context.selected_objects: obj.select_set(False)
original.select_set(True)
for i in range(3):
    camera=original.copy(); camera.data=original.data.copy(); camera.name='Camera '+str(i)
    scene.collection.objects.link(camera); camera.select_set(True)
bpy.ops.wm.save_as_mainfile(filepath={str(blend)!r})
assert m.selection_output_path('//render/{{camera_name}}_####','Cam A','scene.blend')=='//render/Cam A_####'
jobs=m.write_camera_selection(bpy.context)
assert len(jobs)==4 and all(j['startFrame']==7 and j['endFrame']==7 for j in jobs)
assert len(set(j['outputPath'] for j in jobs))==4
assert scene.camera==original and bpy.data.filepath=={str(blend)!r}
m.unregister()
print('SENDER_BATCH_OK')
'''
result = subprocess.run([BLENDER, '--background', '--factory-startup', '--python-expr', setup], capture_output=True, text=True, timeout=90)
assert result.returncode == 0 and 'SENDER_BATCH_OK' in result.stdout, result.stdout + result.stderr
manifest = json.loads(next(watch.glob('*.camerabatch.json')).read_text())
packets = []
for j in manifest['jobs']:
    args = [j['cameraName'], '7', '7', '1', j['outputPath'], j['engine'], '64', '64', '100', '24', 'PNG', 'FINAL', '1', '0', '1', '0', 'SOLID', '1', j['jobId'], str(watch), '0', '']
    packets.append(dict(jobId=j['jobId'], args=args))
packet = work / 'packets.json'; packet.write_text(json.dumps(packets))
command = [BLENDER, '--background', '--factory-startup', str(blend), '--python', str(ROOT / 'Scripts' / 'camera_batch.py'), '--', str(packet)]
workers = [subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) for _ in range(2)]
logs = []
try:
    for worker in workers:
        out, err = worker.communicate(timeout=120)
        logs.append(out + err)
        assert worker.returncode == 0, out + err
finally:
    for worker in workers:
        if worker.poll() is None: worker.kill(); worker.wait()
assert sum(log.count('BRH_LOCAL_FRAME_DONE:7') for log in logs) == 4, '\n'.join(logs)
assert max(log.count('BRH_LOCAL_FRAME_DONE:7') for log in logs) >= 2, 'No worker reused the loaded scene'
for j in manifest['jobs']:
    claim = watch / '_claims' / j['jobId']
    assert (claim / 'complete.json').exists()
    assert not list(claim.glob('*.claim'))
    done = json.loads((claim / '7.done').read_text())
    assert Path(done['output']).stat().st_size > 0
assert len(list((work / 'output').glob('*.png'))) == 4
assert len(list(watch.glob('*.csv'))) == 4
assert len(list(work.glob('*.blend'))) == 1
print('PASS real Blender: sender selection, unchanged active camera, unique outputs, four cameras rendered exactly once by two workers; scene reused; per-camera audit reports. ' + str(work))
