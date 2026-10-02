"""Real Blender smoke: ready without work, then release the same process."""
import json
import os
import time
from pathlib import Path
import subprocess
import tempfile
import threading

root = Path(__file__).resolve().parents[1]
blender = r'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe'
with tempfile.TemporaryDirectory(prefix='BRH-preload-test-') as folder:
    folder = Path(folder)
    control, output = folder / 'release.json', folder / 'executed.txt'
    # Local test fixture only, never a production scene or output.
    worker = folder / 'worker.py'
    worker.write_text('import os,sys\nfrom pathlib import Path\nPath(sys.argv[-1]).write_text(str(os.getpid()))\nos._exit(0)\n')
    process = subprocess.Popen([blender, '--background', '--factory-startup', '--python', str(root / 'Scripts/preload.py'), '--', str(control)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    ready = threading.Event()
    lines = []
    def read():
        for line in process.stdout:
            lines.append(line)
            if 'BRH_PRELOAD_READY' in line:
                ready.set()
    thread = threading.Thread(target=read, daemon=True)
    thread.start()
    try:
        assert ready.wait(30), ''.join(lines)
        assert not output.exists(), 'Work ran before release'
        temp = control.with_suffix('.tmp')
        temp.write_text(json.dumps({'script': str(worker), 'args': [str(output)]}))
        temp.rename(control)
        assert process.wait(timeout=30) == 0
        assert output.read_text() == str(process.pid), 'Did not reuse the opened process'
        print('PASS preload: ready without work, release reuses same Blender PID')
    finally:
        if process.poll() is None:
            process.kill()
        process.wait()
        thread.join(timeout=5)

with tempfile.TemporaryDirectory(prefix='BRH-preload-claim-test-') as folder:
    folder = Path(folder)
    claims = folder / '_claims' / 'test-job'
    claims.mkdir(parents=True)
    claim = claims / '1.claim'
    claim.write_text(json.dumps({'machine': 'OTHER-PC', 'claimed_at': time.time()}))
    args = ['Camera', '1', '1', '1', str(folder / 'frame_'), 'KEEP', '16', '16', '100', '24', 'PNG', 'FINAL', '0', '0', '0', '0', 'SOLID', '1', 'test-job', str(folder), '0', '']
    env = dict(os.environ, BRH_PRELOADED='1')
    result = subprocess.run([blender, '--background', '--factory-startup', '--python', str(root / 'Scripts/render_scene.py'), '--', *args], env=env, capture_output=True, text=True, timeout=30)
    assert result.returncode == 75, result.stdout + result.stderr
    assert claim.exists() and not list(claims.glob('*.done')), 'Touched another worker claim'
    print('PASS lost claim: exits without rendering or modifying peer claim')
