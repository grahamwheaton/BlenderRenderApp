"""Memory-bounded scanline EXR assembly. Requires Blender's OpenImageIO and numpy.
Usage: python stitch_tiles.py manifest.json output.exr --buffer-mib 32
Tile coordinates use Blender's bottom-left origin; EXR arrays are top-down.
"""
import argparse
import json
import os
from pathlib import Path
import uuid
import numpy as np
import OpenImageIO as oiio

def stitch(manifest_path, destination, buffer_mib=32):
    # OpenEXR's default per-thread compression buffers can dwarf the scanline
    # budget on high-core-count machines. Keep this dedicated finishing worker serial.
    oiio.attribute('threads',1)
    oiio.attribute('exr_threads',1)
    m = json.loads(Path(manifest_path).read_text(encoding='utf-8'))
    width, height = m.get('width',m.get('size')), m.get('height',m.get('size'))
    if not isinstance(width,int) or not isinstance(height,int) or min(width,height)<1:
        raise ValueError('Invalid canvas dimensions')
    handles=[]
    output=None
    temporary=None
    try:
        spec=None
        crypto={}
        for tile in m['tiles']:
            core, bounds=tile['core'],tile['bounds']
            cx0,cy0,cx1,cy1=core
            x0,y0,x1,y1=bounds
            if not (0<=x0<=cx0<cx1<=x1<=width and 0<=y0<=cy0<cy1<=y1<=height):
                raise ValueError('Invalid tile rectangle')
            path=Path(tile['file'])
            if not path.is_absolute():
                path=Path(manifest_path).parent/path
            handle=oiio.ImageInput.open(str(path))
            if not handle:
                raise RuntimeError(oiio.geterror())
            ts=oiio.ImageSpec(handle.spec())
            handles.append((tile,handle,ts))
            if (ts.width,ts.height)!=(x1-x0,y1-y0) or ts.depth!=1:
                raise ValueError('Unexpected tile dimensions')
            if handle.seek_subimage(1,0):
                raise ValueError('Multipart EXRs require a separate implementation')
            handle.seek_subimage(0,0)
            if spec is None:
                spec=oiio.ImageSpec(ts)
            elif ts.channelnames!=spec.channelnames or ts.channelformats!=spec.channelformats or ts.format!=spec.format:
                raise ValueError('Channel schema mismatch')
            for a in ts.extra_attribs:
                if not a.name.startswith('cryptomatte/'):
                    continue
                value=json.loads(a.value) if a.name.endswith('/manifest') else a.value
                if isinstance(value,dict):
                    previous=crypto.setdefault(a.name,{})
                    for key,item in value.items():
                        if key in previous and previous[key]!=item:
                            raise ValueError('Conflicting Cryptomatte IDs')
                        previous[key]=item
                elif a.name in crypto and crypto[a.name]!=value:
                    raise ValueError('Cryptomatte metadata mismatch')
                else:
                    crypto[a.name]=value
        if spec is None:
            raise ValueError('No tiles')
        # Validate coverage at every horizontal boundary without a full-size mask.
        edges=sorted({0,height,*[v for t,_,_ in handles for v in (t['core'][1],t['core'][3])]})
        for low,high in zip(edges,edges[1:]):
            spans=sorted((t['core'][0],t['core'][2]) for t,_,_ in handles if t['core'][1]<=low and t['core'][3]>=high)
            cursor=0
            for left,right in spans:
                if left!=cursor:
                    raise ValueError('Missing or overlapping tile cores')
                cursor=right
            if cursor!=width:
                raise ValueError('Incomplete canvas')
        spec.width=spec.full_width=width
        spec.height=spec.full_height=height
        spec.x=spec.y=spec.full_x=spec.full_y=0
        spec.tile_width=spec.tile_height=spec.tile_depth=0
        spec.attribute('compression','zip')
        for name,value in crypto.items():
            spec.attribute(name,json.dumps(value,ensure_ascii=True) if isinstance(value,dict) else value)
        budget=int(buffer_mib*1024*1024)
        bytes_per_row=(width+max(s.width for _,_,s in handles))*spec.nchannels*4
        if budget<bytes_per_row:
            raise ValueError('Buffer budget must fit one output row plus one tile row')
        rows=max(1,min(64,budget//bytes_per_row))
        destination=Path(destination).resolve()
        if destination.exists() or any(destination==Path(t['file']).resolve() for t,_,_ in handles):
            raise ValueError('Destination must be new and distinct from inputs')
        destination.parent.mkdir(parents=True,exist_ok=True)
        temporary=destination.with_name(destination.stem+'.'+uuid.uuid4().hex+'.tmp.exr')
        output=oiio.ImageOutput.create(str(temporary))
        if not output or not output.open(str(temporary),spec):
            raise RuntimeError(oiio.geterror())
        for top in range(0,height,rows):
            bottom=min(height,top+rows)
            strip=np.empty((bottom-top,width,spec.nchannels),dtype=np.float32)
            for tile,handle,ts in handles:
                cx0,cy0,cx1,cy1=tile['core']
                x0,y0,x1,y1=tile['bounds']
                a,b=max(top,height-cy1),min(bottom,height-cy0)
                if a>=b:
                    continue
                local_top=a-(height-y1)
                pixels=handle.read_scanlines(0,0,ts.y+local_top,ts.y+local_top+b-a,ts.z,0,ts.nchannels,oiio.FLOAT)
                if pixels is None:
                    raise RuntimeError(handle.geterror())
                strip[a-top:b-top,cx0:cx1]=pixels[:,cx0-x0:cx1-x0]
                del pixels
            if not output.write_scanlines(top,bottom,0,strip):
                raise RuntimeError(output.geterror())
            del strip
        if not output.close():
            raise RuntimeError(output.geterror())
        output=None
        os.replace(temporary,destination)
        return {'output':str(destination),'channels':spec.nchannels,'strip_rows':rows,'pixel_buffer_bound_bytes':rows*bytes_per_row}
    finally:
        if output:
            output.close()
        for _,handle,_ in handles:
            handle.close()
        if temporary and temporary.exists():
            temporary.unlink()

if __name__=='__main__':
    p=argparse.ArgumentParser()
    p.add_argument('manifest')
    p.add_argument('output')
    p.add_argument('--buffer-mib',type=int,default=32)
    a=p.parse_args()
    print(json.dumps(stitch(a.manifest,a.output,a.buffer_mib),indent=2))
