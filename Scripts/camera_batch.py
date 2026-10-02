"""One loaded Blender scene, individually claimed still jobs. Never save the scene."""
import contextlib
import io
import json
import os
from pathlib import Path
import runpy
import sys
import time
import traceback

print("BRH_SCENE_READY", flush=True)
packets = json.loads(Path(sys.argv[sys.argv.index('--') + 1]).read_text(encoding='utf-8-sig'))
script = Path(__file__).with_name('render_scene.py')
original_stdout = sys.stdout
os.environ['BRH_CAMERA_BATCH'] = '1'


class JobLog(io.TextIOBase):
    def __init__(self, job_id):
        self.job_id = job_id
        self.pending = ''

    def write(self, text):
        self.pending += text
        while '\n' in self.pending:
            line, self.pending = self.pending.split('\n', 1)
            print(f'BRH_BATCH_LOG:{self.job_id}|{line}', file=original_stdout, flush=True)
        return len(text)

    def flush(self):
        if self.pending:
            print(f'BRH_BATCH_LOG:{self.job_id}|{self.pending}', file=original_stdout, flush=True)
            self.pending = ''


pending = list(packets)
while pending:
    progressed = False
    for packet in list(pending):
        job_id, args = packet['jobId'], packet['args']
        claims = Path(args[19]) / '_claims' / job_id
        if (claims / 'cancel.json').exists():
            status = 'Cancelled'
        elif (claims / 'complete.json').exists():
            status = 'Complete'
        else:
            frame_claim = claims / (str(int(args[1])) + '.claim')
            try:
                if frame_claim.exists() and time.time() - frame_claim.stat().st_mtime <= 120:
                    continue
            except OSError:
                continue
            print(f'BRH_BATCH_STATUS:{job_id}|Rendering', flush=True)
            stream = JobLog(job_id)
            try:
                sys.argv = [str(script), '--'] + args
                with contextlib.redirect_stdout(stream), contextlib.redirect_stderr(stream):
                    runpy.run_path(str(script), run_name='__main__')
                status = 'Cancelled' if (claims / 'cancel.json').exists() else 'Complete' if (claims / 'complete.json').exists() else 'Waiting'
            except Exception:
                with contextlib.redirect_stderr(stream):
                    traceback.print_exc()
                status = 'Failed'
            finally:
                stream.flush()
        print(f'BRH_BATCH_STATUS:{job_id}|{status}', flush=True)
        if status != 'Waiting':
            pending.remove(packet)
            progressed = True
    if not progressed and pending:
        if os.environ.get('BRH_PRELOADED') == '1':
            os._exit(75)
        time.sleep(2)
sys.stdout.flush()
os._exit(0)
