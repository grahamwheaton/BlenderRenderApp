"""Experimental single-frame Cycles tile worker. Never saves the blend file."""
import bpy
import csv
import getpass
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import threading
import time
import uuid
sys.path.insert(0, str(Path(__file__).parent))
from stitch_tiles import stitch

job=json.loads(sys.argv[sys.argv.index('--')+1])
j={k[0].lower()+k[1:]:v for k,v in job.items()}
safe=lambda v:re.sub(r'[^A-Za-z0-9_.-]+','_',str(v))
folder=Path(j['coordinationFolder'])/'_claims'/safe(j['jobId'])
folder.mkdir(parents=True,exist_ok=True)
cancel=folder/'cancel.json'
def check_cancel():
    if cancel.exists():
        raise InterruptedError('Cancelled everywhere')
def atomic(path,data):
    temporary=path.with_name(path.name+'.'+uuid.uuid4().hex+'.tmp')
    temporary.write_text(json.dumps(data),encoding='utf-8')
    os.replace(temporary,path)
def claim(path):
    check_cancel()
    try:
        fd=os.open(path,os.O_CREAT|os.O_EXCL|os.O_WRONLY)
        with os.fdopen(fd,'w') as stream:
            json.dump({'machine':socket.gethostname(),'pid':os.getpid()},stream)
        return True
    except FileExistsError:
        try:
            if time.time()-path.stat().st_mtime>120:
                stale=path.with_name(path.name+'.stale.'+uuid.uuid4().hex)
                os.replace(path,stale)
                stale.unlink()
        except OSError:
            pass
        return False
def heartbeat(path,stop):
    while not stop.wait(10):
        try: os.utime(path,None)
        except OSError: return
def run():
    check_cancel()
    s=bpy.context.scene
    if int(j['startFrame'])!=int(j['endFrame']):
        raise ValueError('Tile jobs must contain one frame')
    camera=bpy.data.objects.get(j['cameraName'])
    if camera is None or camera.type!='CAMERA':
        raise ValueError('Missing camera')
    s.camera=camera
    if j['engine'] not in ('KEEP','CYCLES') or (j['engine']=='KEEP' and s.render.engine!='CYCLES'):
        raise ValueError('Experimental tiles require Cycles')
    s.render.engine='CYCLES'
    s.frame_set(int(j['startFrame']))
    s.render.resolution_x=int(j['width'])
    s.render.resolution_y=int(j['height'])
    s.render.resolution_percentage=int(j['scale'])
    width=max(1,s.render.resolution_x*s.render.resolution_percentage//100)
    height=max(1,s.render.resolution_y*s.render.resolution_percentage//100)
    size=int(j['tileSize']); overlap=int(j['tileOverlap'])
    if size<32 or overlap<0 or overlap>size:
        raise ValueError('Invalid tile size or overlap')
    tree=getattr(s,'compositing_node_group',None) or s.node_tree
    allowed={'CompositorNodeRLayers','CompositorNodeOutputFile','CompositorNodeDenoise','ShaderNodeMix','CompositorNodeMixRGB','NodeReroute','NodeFrame'}
    if tree is None or j['ignoreCompositor']:
        raise ValueError('Tiles currently require an enabled compositor with one multilayer EXR File Output')
    unsupported=[n.name for n in tree.nodes if n.bl_idname not in allowed]
    if unsupported:
        raise ValueError('Unsupported experimental compositor nodes: '+', '.join(unsupported))
    outputs=[n for n in tree.nodes if n.bl_idname=='CompositorNodeOutputFile' and not n.mute]
    if len(outputs)!=1 or outputs[0].format.file_format!='OPEN_EXR_MULTILAYER':
        raise ValueError('Exactly one active multilayer EXR File Output is required')
    out=outputs[0]
    if not hasattr(out,'directory'):
        raise ValueError('This experimental worker requires Blender 5.2 File Output support')
    out.format.color_depth='32'; out.format.exr_codec='ZIP'
    s.render.use_compositing=True
    s.render.use_sequencer=False
    s.render.film_transparent=j['transparentBackground']
    if hasattr(s.render,'save_output'): s.render.save_output=False
    # Match the queued dimensions even when the per-camera resolution handler exists.
    p=getattr(camera.data,'per_camera_resolution',None)
    if p and p.use_custom_resolution:
        p.resolution_x=s.render.resolution_x; p.resolution_y=s.render.resolution_y; p.resolution_percentage=s.render.resolution_percentage
    s.render.use_border=s.render.use_crop_to_border=True
    path=str(j['outputPath']).replace('{camera_name}',safe(camera.name)).replace('{blend_name}',Path(bpy.data.filepath).stem)
    path=Path(bpy.path.abspath(path))
    destination_dir=path if str(j['outputPath']).endswith(('/','\\')) else path.parent
    destination_dir.mkdir(parents=True,exist_ok=True)
    final=destination_dir/f'{safe(camera.name)}_{s.frame_current:04d}_{safe(j["jobId"])}.exr'
    tiles=[]
    for y in range(0,height,size):
        for x in range(0,width,size):
            core=[x,y,min(width,x+size),min(height,y+size)]
            bounds=[max(0,x-overlap),max(0,y-overlap),min(width,core[2]+overlap),min(height,core[3]+overlap)]
            tiles.append({'core':core,'bounds':bounds,'file':str(folder/f'tile_{len(tiles)}'/'result.exr')})
    author=os.environ.get('USERDOMAIN','')+'\\'+getpass.getuser()
    while True:
        check_cancel()
        if (folder/'complete.json').exists(): return
        for index,tile in enumerate(tiles):
            check_cancel()
            done=folder/f'{index}.done'
            if done.exists(): continue
            lock=folder/f'{index}.claim'
            if not claim(lock): continue
            stop=threading.Event(); thread=threading.Thread(target=heartbeat,args=(lock,stop),daemon=True); thread.start()
            try:
                if done.exists(): continue
                target=Path(tile['file']); target.parent.mkdir(exist_ok=True)
                out.directory=str(target.parent); out.file_name='result'
                x0,y0,x1,y1=tile['bounds']
                s.render.border_min_x=x0/width; s.render.border_max_x=x1/width
                s.render.border_min_y=y0/height; s.render.border_max_y=y1/height
                if 'FINISHED' not in bpy.ops.render.render(): raise RuntimeError('Tile render failed')
                check_cancel()
                if not target.exists() or target.stat().st_size==0: raise RuntimeError('Tile output missing')
                atomic(done,{'author':author,'machine':socket.gethostname(),'completed_at':time.time(),'output':str(target),'core':tile['core']})
                print(f'BRH_FRAME_DONE:{index}| |{sum((folder/f"{i}.done").exists() for i in range(len(tiles)))}|{len(tiles)}',flush=True)
            finally:
                stop.set(); thread.join(); lock.unlink(missing_ok=True)
        if all((folder/f'{i}.done').exists() for i in range(len(tiles))):
            lock=folder/'stitch.claim'
            if claim(lock):
                stop=threading.Event(); thread=threading.Thread(target=heartbeat,args=(lock,stop),daemon=True); thread.start()
                try:
                    check_cancel()
                    if (folder/'complete.json').exists(): return
                    manifest=folder/'tiles.json'
                    atomic(manifest,{'width':width,'height':height,'tiles':tiles})
                    print('BRH: Stitching tiles',flush=True)
                    if not final.exists():
                        python = Path(sys.prefix)/'bin'/'python.exe'
                        if not python.exists():
                            raise RuntimeError('Bundled Blender Python is required for isolated stitching')
                        subprocess.run([str(python),str(Path(__file__).with_name('stitch_tiles.py')),str(manifest),str(final),'--buffer-mib','32'],check=True,creationflags=subprocess.CREATE_NO_WINDOW)
                    check_cancel()
                    with final.with_suffix('.csv').open('w',newline='',encoding='utf-8-sig') as report:
                        writer=csv.writer(report); writer.writerow(['Tile','Core','Rendered by','Machine','Completed UTC epoch'])
                        for i in range(len(tiles)):
                            data=json.loads((folder/f'{i}.done').read_text())
                            writer.writerow([i,data['core'],data['author'],data['machine'],data['completed_at']])
                    atomic(folder/'complete.json',{'output':str(final),'tiles':len(tiles),'completed_at':time.time()})
                    print('BRH: Stitched output: '+str(final),flush=True)
                    return
                finally:
                    stop.set(); thread.join(); lock.unlink(missing_ok=True)
        time.sleep(2)

try:
    run()
except InterruptedError:
    print('BRH: Distributed job cancelled everywhere.',flush=True)
except Exception:
    import traceback
    traceback.print_exc(); sys.stderr.flush(); os._exit(1)
sys.stdout.flush(); os._exit(0)
