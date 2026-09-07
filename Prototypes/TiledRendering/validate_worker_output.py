import json
import csv
from pathlib import Path
import sys
import numpy as np
import OpenImageIO as o
o.attribute('threads',1)
o.attribute('exr_threads',1)
root=Path(sys.argv[1])
folder=root/'_claims'/'tile-test'
m=json.loads((folder/'tiles.json').read_text())
output=Path(json.loads((folder/'complete.json').read_text())['output'])
final=o.ImageInput.open(str(output))
fs=final.spec()
assert (fs.width,fs.height)==(m['width'],m['height'])
metadata=lambda s:{a.name:json.loads(a.value) if a.name.endswith('/manifest') else a.value for a in s.extra_attribs if a.name.startswith('cryptomatte/')}
for tile in m['tiles']:
    inp=o.ImageInput.open(tile['file'])
    ts=inp.spec()
    assert ts.channelnames==fs.channelnames
    for key,value in metadata(ts).items():
        actual=metadata(fs)[key]
        if isinstance(value,dict): assert all(actual.get(k)==v for k,v in value.items())
        else: assert value==actual
    x0,y0,x1,y1=tile['bounds']; cx0,cy0,cx1,cy1=tile['core']
    top=m['height']-cy1; bottom=m['height']-cy0
    for row in range(top,bottom,8):
        end=min(bottom,row+8)
        local=row-(m['height']-y1)
        a=inp.read_scanlines(0,0,ts.y+local,ts.y+local+end-row,0,0,ts.nchannels,o.FLOAT)[:,cx0-x0:cx1-x0]
        b=final.read_scanlines(0,0,row,end,0,0,fs.nchannels,o.FLOAT)[:,cx0:cx1]
        assert np.array_equal(a.view(np.uint32),b.view(np.uint32)), 'Pixel bits differ'
    inp.close()
final.close()
with output.with_suffix('.csv').open(encoding='utf-8-sig',newline='') as stream:
    rows=list(csv.DictReader(stream))
assert len(rows)==len(m['tiles']) and all(row['Rendered by'] and row['Machine'] for row in rows)
print('PASS',fs.width,fs.height,fs.nchannels,'channels: every tile-core bit preserved; Cryptomatte metadata retained; CSV attribution complete')
