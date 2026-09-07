# Tiled rendering prototype — second round

## V5.8 worker integration follow-up

Two independent local Blender processes completed a 1536 x 1024 image using six 512-pixel cores and 128-pixel overlaps. Each process rendered three tiles; one claimed assembly and produced one EXR and one CSV. Tests used a temporary 16-sample override and did not save the source blend.

Every output tile-core pixel was compared bit-for-bit against the assembled file in eight-row strips, across all 88 channels. All matched, Cryptomatte metadata was retained, and all six CSV records contained user/machine attribution. This compares assembly against the source tiles, not against a full-frame render.

A new worker reopening the completed job exited without changing any tile completion timestamps. A separate two-worker test placed a cancellation marker after a tile claim was acquired: both exited and no final EXR or complete marker was published. The script test checks cooperative cancellation between operations; it does not verify the WPF app's immediate process-kill path.

Output: `C:\Users\Graham\Documents\ChatGPT\BlenderRenderHeadless\tile-worker-tests\ed4aaafdd9d442fcbb79dba4a8374861\output\SprayCans_0002_tile-test.exr`.

Still unverified: separate physical machines, NAS failures, interrupted-worker stale-claim recovery, and 15k production-sized scenes. No new executable was required for this validation round.

Source: `C:\Users\Graham\Documents\BlenderRenderApp\RENDER_MID GRAY.blend`.
The blend file was opened with factory startup and automatic scripts disabled. It was not saved or modified on disk. The current compositor has four denoisers.

## Scope

Local four-tile experiments using the existing compositor, 32-bit ZIP multilayer EXRs, and lossless tile-core assembly. This is a prototype, not an app release or a multi-machine validation. No 15k render has been run.

## Results

At 512 x 512 (32 samples), tested overlap margins of 0, 32, 64 and 128 pixels on each interior edge. Average Gloss-pass error within an eight-pixel-wide cross around tile boundaries:

| Overlap | Mean absolute error versus full-frame reference |
|---|---:|
| 0 | 0.00057817 |
| 32 | 0.00005229 |
| 64 | 0.00002118 |
| 128 | 0.00001795 |

At 1024 x 1024 (128 samples):

| Overlap | Gloss boundary MAE | GlossDIR boundary MAE |
|---|---:|---:|
| 64 | 0.00001646 | 0.00011490 |
| 128 | 0.00000789 | 0.00004267 |

These are scene-linear numerical differences, not perceptual percentages. Black background and alpha values are included in the metric. Maximum differences remain measurable: Gloss 0.00756, GlossDIR 0.02390 at 1024/128. The colour passes are not identical to a full-frame render. 128 pixels is a provisional overlap, not a guarantee for other scenes or larger images.

All 88 channels survived. Object and Material Cryptomatte pixel bits and parsed manifests exactly matched the full-frame reference in every tested configuration. Stitched pixels also matched an independent direct tile-core assembly bit-for-bit. AO matched the reference exactly.

## Streaming implementation

`stitch_tiles.py` reads and writes short scanline strips; it never allocates a full canvas. It checks rectangle coverage, input dimensions, channel schema, conflicting Cryptomatte IDs and existing destinations. It merges compatible Cryptomatte manifests and writes a temporary EXR before publishing the complete result. It performs no colour conversion, resampling or blending.

Separate small tests cover non-square canvases, uneven tile sizes, bottom-left coordinate conversion, multiple strips, exact ID preservation, merged manifests, and rejection of missing/overlapping cores.

Fresh Windows process measurements with a 4 MiB pixel-buffer budget:

| Image | Default compression threading peak | One compression thread peak | Serial stitch time |
|---|---:|---:|---:|
| 512 square / 88 channels | 260.6 MiB | 90.0 MiB | 0.51 s |
| 1024 square / 88 channels | 797.6 MiB | 105.3 MiB | 2.16 s |

Total process memory exceeds the pixel-buffer budget because of Python, native libraries, metadata and EXR compression buffers. Memory still scales with scanline width, channel count, compression and open files; these measurements are not a 15k memory guarantee. OpenImageIO supports scanline reads and configurable EXR threading: https://openimageio.readthedocs.io/en/stable/imageinput.html and https://openimageio.readthedocs.io/en/v3.1.14.0/imageioapi.html .

## Usage

Use the Python bundled with Blender 5.2, which has OpenImageIO and numpy installed on this machine.

`python stitch_tiles.py manifest.json stitched.exr --buffer-mib 32`

The manifest contains a square `size` or rectangular `width`/`height`, and `tiles` with `file`, `core` and `bounds`. Rectangles are integer `[x0,y0,x1,y1]`, bottom-left origin, exclusive maxima. `bounds` includes overlap; `core` specifies the exact output region. Paths can be absolute or relative to the manifest. Only single-part, flat scanline EXRs are currently targeted; arbitrary compositor trees, deep/multipart data and production cancellation/resume are not implemented here.

`test_stitch_tiles.py` runs the independent synthetic tests. `tile_probe.py` runs the Blender test scene experiment. `evaluate_tiles.py` is a small-image verifier that intentionally uses full arrays and must not be used on production-size images. `benchmark_stitch.py` measures a standalone Windows stitcher process.

## Next integration boundary

The prototype is ready to be used as the basis for tile-job coordination and a finishing worker. Before production use: test a larger real scene, validate Cryptomatte selection in the intended downstream application, and test separate worker processes/machines. Preserve consistent scene assets, seeds, sample settings and Blender versions. The existing desktop executable has not changed. Future executables must include their version in the filename.
