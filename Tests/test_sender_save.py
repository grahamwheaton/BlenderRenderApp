"""Exercise the shipped helper with simulated Blender save outcomes; no user files."""
import ast
from pathlib import Path
from types import SimpleNamespace
import unittest

SOURCE = Path(__file__).resolve().parents[1] / "BlenderAddon" / "render_queue_sender.py"


class SaveTests(unittest.TestCase):
    def helper(self, dirty, outcomes, enabled=True):
        data = SimpleNamespace(is_dirty=dirty)
        calls, sleeps = [], []
        def save():
            calls.append(True)
            result = outcomes.pop(0)
            if isinstance(result, Exception):
                raise result
            if result == {"FINISHED"}:
                data.is_dirty = False
            return result
        env = dict(bpy=SimpleNamespace(data=data, ops=SimpleNamespace(wm=SimpleNamespace(save_mainfile=save))),
                   prefs=lambda: SimpleNamespace(save_before_sending=enabled), time=SimpleNamespace(sleep=sleeps.append))
        tree = ast.parse(SOURCE.read_text(encoding="utf-8"))
        fn = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == "save_shared_scene")
        exec(compile(ast.Module(body=[fn], type_ignores=[]), str(SOURCE), "exec"), env)
        return env["save_shared_scene"], calls, sleeps

    def test_clean_file_never_saved(self):
        fn, calls, _ = self.helper(False, [])
        fn(); self.assertEqual(calls, [])

    def test_backup_retry_then_success(self):
        fn, calls, sleeps = self.helper(True, [RuntimeError("Version backup failed (file saved with @)"), {"FINISHED"}])
        fn(); self.assertEqual(len(calls), 2); self.assertEqual(sleeps, [1])

    def test_permanent_failure_blocks_submission(self):
        fn, calls, sleeps = self.helper(True, [RuntimeError("Access is denied")] * 3)
        with self.assertRaisesRegex(RuntimeError, "Job NOT sent"):
            fn()
        self.assertEqual(len(calls), 3); self.assertEqual(sleeps, [1, 2])

    def test_disabled_and_cancelled_save(self):
        fn, calls, _ = self.helper(True, [], False)
        with self.assertRaises(RuntimeError): fn()
        self.assertEqual(calls, [])
        fn, calls, _ = self.helper(True, [{"CANCELLED"}])
        with self.assertRaisesRegex(RuntimeError, "Job NOT sent"): fn()
        self.assertEqual(len(calls), 1)


if __name__ == "__main__": unittest.main()
