import ctypes
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('overwrite_output', ROOT / 'Scripts/overwrite_output.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)

class OverwriteTests(unittest.TestCase):
    def test_fallback_and_collision_preserve_original(self):
        with tempfile.TemporaryDirectory() as folder:
            source, target = Path(folder)/'source.png', Path(folder)/'frame.png'
            source.write_bytes(b'new render'); target.write_bytes(b'original')
            with patch.object(helper.os, 'replace', side_effect=PermissionError('locked')):
                first, failed = helper.commit_output(source, target, True)
                second, _ = helper.commit_output(source, target, True)
                self.assertTrue(failed)
                self.assertNotEqual(first, second)
                self.assertIn('FailedOverwrite', first)
                self.assertEqual(Path(first).read_bytes(), b'new render')
                self.assertEqual(target.read_bytes(), b'original')
                with self.assertRaises(PermissionError): helper.commit_output(source, target, False)
                with self.assertRaises(PermissionError): helper.commit_output(source, Path(folder)/'missing.png', True)

    def test_real_blender_locked_destination_and_shared_receipt(self):
        with tempfile.TemporaryDirectory(prefix='BRH-overwrite-test-') as folder:
            folder = Path(folder)
            target = folder / 'frame_0001.png'
            target.write_bytes(b'original locked render')
            kernel = ctypes.WinDLL('kernel32', use_last_error=True)
            kernel.CreateFileW.restype = ctypes.c_void_p
            kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p]
            kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            handle = kernel.CreateFileW(str(target), 0x80000000, 1, None, 3, 0, None)
            self.assertNotEqual(handle, ctypes.c_void_p(-1).value)
            job_id = str(uuid.uuid4())
            args = ['Camera', '1', '1', '1', str(folder/'frame_'), 'KEEP', '16', '16', '100', '24', 'PNG', 'PLAYBLAST', '1', '0', '1', '0', 'SOLID', '1', job_id, str(folder/'watch'), '0', '']
            try:
                result = subprocess.run([r'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe', '--background', '--factory-startup', '--python', str(ROOT/'Scripts/render_scene.py'), '--', *args], env=dict(os.environ, BRH_FORCE_OVERWRITE='1'), capture_output=True, text=True, timeout=90)
            finally:
                kernel.CloseHandle(handle)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            receipt = json.loads((folder/'watch'/'_claims'/job_id/'1.done').read_text())
            self.assertIn('FailedOverwrite', receipt['output'])
            self.assertGreater(Path(receipt['output']).stat().st_size, 0)
            self.assertEqual(target.read_bytes(), b'original locked render')
            self.assertTrue((folder/'watch'/'_claims'/job_id/'complete.json').exists())
            self.assertIn('BRH_FAILED_OVERWRITE:', result.stdout)

if __name__ == '__main__': unittest.main()
