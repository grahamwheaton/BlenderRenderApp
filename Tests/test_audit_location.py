"""Run production report writers with temporary claims; no Blender/NAS writes."""
import ast
import csv
import json
import os
import re
import tempfile
import time
import unittest
import uuid
from pathlib import Path


class AuditTests(unittest.TestCase):
    def test_report_destinations(self):
        scripts = Path(__file__).resolve().parents[1] / 'Scripts'
        for script in ('render_scene.py', 'viewport_playblast.py', 'tile_worker.py'):
            with self.subTest(script=script), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                watch = root / 'CloudRender'; watch.mkdir()
                output = root / 'output'; output.mkdir()
                claims = watch / '_claims'; claims.mkdir()
                frame = output / 'frame.png'
                receipt = dict(output=str(frame), machine='TEST-PC', author='Artist', completed_at=1, core=[0, 0, 10, 10])
                for n in (0, 1): (claims / f'{n}.done').write_text(json.dumps(receipt))
                name = 'write_tile_report' if script == 'tile_worker.py' else 'write_render_report'
                fn = next(n for n in ast.walk(ast.parse((scripts / script).read_text())) if isinstance(n, ast.FunctionDef) and n.name == name)
                env = dict(csv=csv, json=json, os=os, re=re, time=time, uuid=uuid, Path=Path,
                           coordination_folder=str(watch), camera_name='Camera', job_id='12345678-test', safe_job_id='12345678-test',
                           safe_name=lambda s: s, frames=[1], done_path=lambda n: claims / f'{n}.done',
                           frame_output=lambda n: frame, frame_output_path=lambda n: str(frame),
                           j={'coordinationFolder': str(watch)}, folder=claims)
                exec(compile(ast.Module(body=[fn], type_ignores=[]), script, 'exec'), env)
                env[name](*([output / 'stitched.exr', [1]] if script == 'tile_worker.py' else []))
                reports = list(watch.glob('*.csv'))
                self.assertEqual(len(reports), 1)
                self.assertEqual(list(output.iterdir()), [])
                self.assertFalse(list(watch.glob('*.tmp')))
                with reports[0].open(encoding='utf-8-sig', newline='') as stream:
                    rows = list(csv.reader(stream))
                self.assertEqual(len(rows), 2)
                self.assertIn('TEST-PC', rows[1])


if __name__ == '__main__': unittest.main()
