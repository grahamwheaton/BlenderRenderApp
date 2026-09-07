"""Independent stitcher tests, using temporary small EXRs rather than Blender."""
import json
import tempfile
from pathlib import Path
import numpy as np
import OpenImageIO as o
from stitch_tiles import stitch

with tempfile.TemporaryDirectory(prefix='tile-stitch-test-') as folder:
    root=Path(folder)
    # Unequal tile widths/heights exercise orientation, strip crossing and edge tiles.
    source=np.arange(13*9*4,dtype=np.float32).reshape(9,13,4)
    source[:,:,0]=np.uint32(0x3fabcdef).view(np.float32)
    manifest={'width':13,'height':9,'tiles':[]}
    for k,core in enumerate(([0,0,5,4],[5,0,13,4],[0,4,5,9],[5,4,13,9])):
        cx0,cy0,cx1,cy1=core
        x0,y0,x1,y1=max(0,cx0-1),max(0,cy0-1),min(13,cx1+1),min(9,cy1+1)
        spec=o.ImageSpec(x1-x0,y1-y0,4,o.FLOAT)
        spec.channelnames=['CryptoObject00.r','CryptoObject00.g','CryptoObject00.b','CryptoObject00.a']
        spec.attribute('cryptomatte/1234567/name','CryptoObject')
        spec.attribute('cryptomatte/1234567/manifest',json.dumps({f'object{k}':f'{k:08x}'}))
        path=root/f'{k}.exr'
        out=o.ImageOutput.create(str(path))
        assert out.open(str(path),spec)
        assert out.write_image(np.ascontiguousarray(source[9-y1:9-y0,x0:x1]))
        assert out.close()
        manifest['tiles'].append({'core':core,'bounds':[x0,y0,x1,y1],'file':path.name})
    mp=root/'manifest.json'
    mp.write_text(json.dumps(manifest))
    stitch(mp,root/'result.exr',0.001)
    inp=o.ImageInput.open(str(root/'result.exr'))
    result=inp.read_image(format=o.FLOAT)
    assert np.array_equal(source.view(np.uint32),result.view(np.uint32))
    attr=inp.spec().get_string_attribute('cryptomatte/1234567/manifest')
    assert len(json.loads(attr))==4
    inp.close()
    for label,tiles in [('gap',manifest['tiles'][:-1]),('overlap',manifest['tiles']+[manifest['tiles'][0]])]:
        mp.write_text(json.dumps({**manifest,'tiles':tiles}))
        try:
            stitch(mp,root/f'{label}.exr')
        except ValueError:
            assert not (root/f'{label}.exr').exists()
        else:
            raise AssertionError(f'{label} was not rejected')
    print('PASS: uneven rectangular tiles, bottom-left mapping, multiple strips, exact IDs, merged manifests, missing/overlapping rejection')
