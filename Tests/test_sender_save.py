"""Exercise the shipped helper with simulated Blender save outcomes; no user files."""
import ast
from pathlib import Path
from types import SimpleNamespace
import unittest

SOURCE = Path(__file__).resolve().parents[1] / "BlenderAddon" / "render_queue_sender.py"


class SaveTests(unittest.TestCase):
    def helper(self, dirty, outcomes, enabled=True, stays_dirty=False, dirty_saves=0):
        data = SimpleNamespace(is_dirty=dirty)
        calls, sleeps = [], []
        def save():
            calls.append(True)
            result = outcomes.pop(0)
            if isinstance(result, Exception):
                raise result
            if result == {"FINISHED"} and not stays_dirty and len(calls) > dirty_saves:
                data.is_dirty = False
            return result
        env = dict(bpy=SimpleNamespace(data=data, context=SimpleNamespace(view_layer=SimpleNamespace(update=lambda: None)), ops=SimpleNamespace(wm=SimpleNamespace(save_mainfile=save))),
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

    def test_finished_but_dirty_has_specific_diagnostic(self):
        fn, calls, sleeps = self.helper(True, [{"FINISHED"}] * 3, stays_dirty=True)
        with self.assertRaisesRegex(RuntimeError, "FINISHED, but Blender still reports unsaved changes"):
            fn()
        self.assertEqual(len(calls), 3)
        self.assertEqual(sleeps, [1, 2])

    def test_finished_dirty_then_clean_retries(self):
        fn, calls, sleeps = self.helper(True, [{"FINISHED"}] * 2, dirty_saves=1)
        fn()
        self.assertEqual(len(calls), 2)
        self.assertEqual(sleeps, [1])

    def test_mixed_backup_and_dirty_failures_share_limit(self):
        fn, calls, sleeps = self.helper(True, [RuntimeError("Unable to make version backup"), {"FINISHED"}, {"FINISHED"}], dirty_saves=2)
        fn()
        self.assertEqual(len(calls), 3)
        self.assertEqual(sleeps, [1, 2])

    def test_cancelled_has_operator_diagnostic(self):
        fn, _, _ = self.helper(True, [{"CANCELLED"}])
        with self.assertRaisesRegex(RuntimeError, "Save operator did not finish.*CANCELLED"):
            fn()


if __name__ == "__main__": unittest.main()
