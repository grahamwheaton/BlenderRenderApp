"""Run inside Blender background factory startup; only temporary scenes/queues."""
import bpy
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('render_queue_sender', root / 'BlenderAddon/render_queue_sender.py')
sender = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sender)
sender.register()
with tempfile.TemporaryDirectory(prefix='BRH-playblast-selection-') as temp:
    work = Path(temp)
    sender.prefs = lambda: SimpleNamespace(save_before_sending=True, shared_queue_folder=str(work / 'watch'))
    scene = bpy.context.scene
    scene.render.image_settings.file_format = 'PNG'
    scene.render.filepath = str(work / 'output' / 'frame_')
    scene.frame_start, scene.frame_end, scene.frame_step = 2, 20, 2
    original = scene.camera
    for obj in bpy.context.selected_objects:
        obj.select_set(False)
    original.select_set(True)
    other = original.copy()
    other.data = original.data.copy()
    other.name = 'Second camera'
    scene.collection.objects.link(other)
    other.select_set(True)
    bpy.data.objects['Cube'].select_set(True)
    bpy.ops.wm.save_as_mainfile(filepath=str(work / 'fixture.blend'))
    selected = list(bpy.context.selected_objects)
    context = SimpleNamespace(scene=scene, selected_objects=selected, area=SimpleNamespace(type='VIEW_3D'), space_data=SimpleNamespace(shading=SimpleNamespace(type='MATERIAL')))
    sender.overlay_settings = lambda space: {'show_overlays': False, 'show_floor': False}
    saves = []
    sender.save_shared_scene = lambda: saves.append(True)
    jobs = sender.write_playblast_selection(context)
    assert len(jobs) == 2 and len(saves) == 1
    assert len({j['outputPath'] for j in jobs}) == 2
    assert len({j['jobId'] for j in jobs}) == 2
    assert all(j['requiresViewport'] and j['distributed'] and j['renderMode'] == 'PLAYBLAST' and 'batchId' not in j for j in jobs)
    assert all((j['startFrame'], j['endFrame'], j['frameStep']) == (2, 20, 2) for j in jobs)
    assert all(j['viewportShading'] == 'MATERIAL' and j['viewportOverlaySettings']['show_floor'] is False for j in jobs)
    assert scene.camera == original and list(bpy.context.selected_objects) == selected
    assert len(list((work / 'watch').glob('*.renderjob.json'))) == 2
    assert len(list(work.glob('*.blend'))) == 1
    for invalid in ([], [bpy.data.objects['Cube']]):
        context.selected_objects = invalid
        try:
            sender.write_playblast_selection(context)
            raise AssertionError('Accepted empty camera selection')
        except RuntimeError:
            pass
    assert len(list((work / 'watch').glob('*.renderjob.json'))) == 2
    context.selected_objects = selected
    scene.frame_set(7)
    stills = sender.write_playblast_selection(context, current_frame_only=True)
    assert len(stills) == 2 and len(saves) == 2
    assert all(j['startFrame'] == j['endFrame'] == 7 and j['renderMode'] == 'PLAYBLAST' for j in stills)
    assert all(j['viewportShading'] == 'MATERIAL' and j['viewportOverlaySettings']['show_floor'] is False for j in stills)
    assert scene.camera == original and scene.frame_start == 2 and scene.frame_end == 20
    assert len(list((work / 'watch').glob('*.renderjob.json'))) == 4
sender.unregister()
print('PASS selected-camera playblast: registration, one save, separate jobs, viewport settings, range, unique outputs, unchanged selection and active camera')
