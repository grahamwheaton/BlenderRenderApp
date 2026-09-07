"""Small-resolution validation only. Uses full arrays to independently verify the streaming stitcher."""
import json
from pathlib import Path
import sys
import numpy as np
import OpenImageIO as o
from stitch_tiles import stitch

def read(path):
    i=o.ImageInput.open(str(path))
    s=o.ImageSpec(i.spec())
    a=i.read_image(format=o.FLOAT)
    i.close()
    return s,a

def png(path,array):
    a=np.ascontiguousarray(np.clip(array,0,1)*255,dtype=np.uint8)
    out=o.ImageOutput.create(str(path))
    assert out.open(str(path),o.ImageSpec(a.shape[1],a.shape[0],3,o.UINT8))
    assert out.write_image(a)
    assert out.close()

root=Path(sys.argv[1])
records=[]
for mp in sorted(root.glob('overlap_*/manifest.json'),key=lambda p:int(p.parent.name.split('_')[1])):
    m=json.loads(mp.read_text())
    destination=mp.parent/'stitched.exr'
    stats=stitch(mp,destination,4)
    s,ref=read(m['reference'])
    ss,actual=read(destination)
    expected=np.empty_like(ref)
    h,w=ref.shape[:2]
    for t in m['tiles']:
        _,a=read(t['file'])
        cx0,cy0,cx1,cy1=t['core']
        x0,y0,x1,y1=t['bounds']
        expected[h-cy1:h-cy0,cx0:cx1]=a[y1-cy1:y1-cy0,cx0-x0:cx1-x0]
    assert np.array_equal(expected.view(np.uint32),actual.view(np.uint32)), 'Stitching changed pixel bits'
    md=lambda sp:{a.name:a.value for a in sp.extra_attribs if a.name.startswith('cryptomatte/')}
    # Manifest text can be serialised differently; compare parsed values.
    norm=lambda sp:{k:json.loads(v) if k.endswith('/manifest') else v for k,v in md(sp).items()}
    assert norm(s)==norm(ss), 'Stitched metadata mismatch'
    del expected
    seam=np.zeros((h,w),dtype=bool)
    seam[h//2-4:h//2+4,:]=True
    seam[:,w//2-4:w//2+4]=True
    r={'overlap':m['overlap'],'render_seconds':m['render_seconds'],'stitch':stats,'passes':{}}
    for prefix in ('Image','Diff','Gloss','GlossDIR','AO','CryptoObject','CryptoMaterial'):
        ids=[i for i,n in enumerate(s.channelnames) if n.startswith(prefix+'.') or n.startswith(prefix+'0')]
        a,b=ref[:,:,ids],actual[:,:,ids]
        if prefix.startswith('Crypto'):
            r['passes'][prefix]={'bit_exact':bool(np.array_equal(a.view(np.uint32),b.view(np.uint32)))}
        else:
            d=np.abs(a.astype(np.float64)-b)
            r['passes'][prefix]={'mae':float(d.mean()),'seam_mae':float(d[seam].mean()),'max':float(d.max())}
    ids=[s.channelnames.index('Gloss.'+c) for c in 'RGB']
    a,b=ref[:,:,ids],actual[:,:,ids]
    # Display transform for diagnostics only. EXR channels are never transformed.
    preview=lambda v:np.maximum(v,0)**(1/2.2)
    heat=np.repeat(np.clip(np.abs(a-b).max(axis=2,keepdims=True)*20,0,1),3,axis=2)
    png(mp.parent/'gloss-reference-stitched-difference20x.png',np.concatenate((preview(a),preview(b),heat),axis=1))
    records.append(r)
(root/'validation.json').write_text(json.dumps(records,indent=2))
print(json.dumps(records,indent=2))
