"""Open a scene without claiming work; wait for a launcher-owned release file."""
import bpy
import json
import os
import runpy
import sys
import time
from pathlib import Path

control = Path(sys.argv[sys.argv.index('--') + 1])
deadline = time.monotonic() + 600
print('BRH_PRELOAD_READY', flush=True)

def release():
    if time.monotonic() > deadline:
        os._exit(75)
    if not control.exists():
        return 0.25
    packet = json.loads(control.read_text(encoding='utf-8-sig'))
    control.unlink()
    os.environ['BRH_PRELOADED'] = '1'
    os.environ['BRH_FORCE_OVERWRITE'] = '1' if packet.get('forceOverwrite', False) else '0'
    sys.argv = [packet['script'], '--'] + packet['args']
    runpy.run_path(packet['script'], run_name='__main__')
    return None

if bpy.app.background:
    while release() is not None:
        time.sleep(0.25)
else:
    bpy.app.timers.register(release, first_interval=0.25)
