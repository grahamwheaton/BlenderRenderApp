"""Exercise two independent Blender workers against a shared disposable job."""
import json
from pathlib import Path
import subprocess
import uuid
import argparse
import time

parser=argparse.ArgumentParser()
parser.add_argument('--large',action='store_true')
parser.add_argument('--cancel',action='store_true')
options=parser.parse_args()

root=Path(__file__).parent/'tile-worker-tests'/uuid.uuid4().hex
root.mkdir(parents=True)
repo=Path(r'C:\Users\Graham\Documents\BlenderRenderApp')
job={'BlendFile':str(repo/'RENDER_MID GRAY.blend'),'CameraName':'SprayCans','StartFrame':'2','EndFrame':'2','Width':'256','Height':'256','Scale':'100','Engine':'CYCLES','IgnoreCompositor':False,'TransparentBackground':False,'OutputPath':str(root/'output')+'/', 'JobId':'tile-test','CoordinationFolder':str(root),'TileSize':128,'TileOverlap':32}
if options.large:
    job.update(Width='1536',Height='1024',TileSize=512,TileOverlap=128)
code="import bpy,runpy; bpy.context.scene.cycles.samples=16; bpy.context.scene.cycles.device='CPU'; runpy.run_path("+repr(str(repo/'Scripts'/'tile_worker.py'))+",run_name='__main__')"
args=[r'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe','-b','--factory-startup','--disable-autoexec',job['BlendFile'],'--python-expr',code,'--',json.dumps(job)]
processes=[subprocess.Popen(args,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW) for _ in range(2)]
claims=root/'_claims'/'tile-test'
if options.cancel:
    deadline=time.monotonic()+60
    while not list(claims.glob('*.claim')):
        if time.monotonic()>deadline: raise RuntimeError('Workers never claimed tiles')
        time.sleep(0.05)
    (claims/'cancel.json').write_text('{}')
for index,p in enumerate(processes):
    try:
        data=p.communicate(timeout=180)[0].decode('utf-8',errors='replace')
    except subprocess.TimeoutExpired:
        p.kill(); raise
    (root/f'worker{index}.log').write_text(data,encoding='utf-8')
    print(index,p.returncode,data[-1400:])
    assert p.returncode==0
claims=root/'_claims'/'tile-test'
if options.cancel:
    assert not (claims/'complete.json').exists()
    assert not list((root/'output').glob('*.exr'))
    print('PASS cancellation during active tile work',root)
    raise SystemExit(0)
expected=6 if options.large else 4
assert len(list(claims.glob('*.done')))==expected
assert (claims/'complete.json').exists()
assert len(list((root/'output').glob('*.exr')))==1
assert len(list((root/'output').glob('*.csv')))==1
before={p.name:p.stat().st_mtime_ns for p in claims.glob('*.done')}
restart=subprocess.run(args,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,creationflags=subprocess.CREATE_NO_WINDOW,timeout=60)
assert restart.returncode==0
assert before=={p.name:p.stat().st_mtime_ns for p in claims.glob('*.done')}
print('PASS two workers,',expected,'tiles, one assembled EXR and CSV; completed-job restart did not rerender',root)
