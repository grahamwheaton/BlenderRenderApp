"""Commit staged output without truncating an existing render on save failure."""
import os
from pathlib import Path
import shutil
import uuid


def output_key(path):
    # Blender canonicalizes frame_path, while File Output directories may still
    # contain Model/../Visuals. Compare equivalent absolute paths, not spellings.
    return os.path.normcase(os.path.abspath(os.path.normpath(os.fspath(path))))


def commit_output(source, destination, allow_fallback=False):
    source, destination = Path(source), Path(os.path.abspath(os.path.normpath(os.fspath(destination))))
    if not source.is_file() or source.stat().st_size == 0:
        raise RuntimeError(f'Rendered output is missing or empty: {source}')
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name('.' + destination.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        shutil.copyfile(source, temporary)
        try:
            os.replace(temporary, destination)
            return str(destination), False
        except OSError:
            # A missing directory, failed render or failed first save is not an
            # overwrite failure. Never label those cases as completed.
            if not allow_fallback or not destination.is_file():
                raise
            alternate = destination.with_name(destination.stem + '_FailedOverwrite' + destination.suffix)
            while True:
                try:
                    output = alternate.open('xb')
                    break
                except FileExistsError:
                    alternate = destination.with_name(destination.stem + '_FailedOverwrite_' + uuid.uuid4().hex[:8] + destination.suffix)
            try:
                with output, temporary.open('rb') as staged:
                    shutil.copyfileobj(staged, output)
                if alternate.stat().st_size != source.stat().st_size:
                    raise IOError('Fallback output size does not match staged render')
            except BaseException:
                alternate.unlink(missing_ok=True)
                raise
            print('BRH_FAILED_OVERWRITE:' + str(alternate), flush=True)
            return str(alternate), True
    finally:
        temporary.unlink(missing_ok=True)


def render_staged(bpy, scene, uses_compositor_output, expected_output):
    """Stage scene output and every enabled compositor output, then publish."""
    import tempfile
    mappings, saved = [], []
    original_path = scene.render.filepath
    results, failed = {}, False
    with tempfile.TemporaryDirectory(prefix='BRH-render-stage-') as folder:
        folder = Path(folder)
        primary = folder / 'scene' / Path(expected_output).name
        primary.parent.mkdir()
        tree = getattr(scene, 'compositing_node_group', None) or getattr(scene, 'node_tree', None)
        try:
            if tree and scene.render.use_compositing:
                for index, node in enumerate(tree.nodes):
                    if node.bl_idname != 'CompositorNodeOutputFile' or node.mute:
                        continue
                    attribute = 'directory' if hasattr(node, 'directory') else 'base_path'
                    original = getattr(node, attribute)
                    names = [node.file_name] if hasattr(node, 'file_name') else [slot.path for slot in node.file_slots]
                    if hasattr(node, 'file_output_items') and node.format.file_format != 'OPEN_EXR_MULTILAYER':
                        names += [item.name for item in node.file_output_items]
                    if any(os.path.isabs(name) or '..' in name.replace('\\', '/').split('/') for name in names):
                        raise RuntimeError('Force overwrite staging requires compositor filenames within their output directory')
                    staged = folder / f'node-{index}'
                    staged.mkdir()
                    saved.append((node, attribute, original))
                    mappings.append((staged, Path(bpy.path.abspath(original))))
                    setattr(node, attribute, str(staged) + os.sep)
            if not uses_compositor_output:
                scene.render.filepath = str(primary)
            result = bpy.ops.render.render(write_still=not uses_compositor_output)
            if 'FINISHED' not in result:
                raise RuntimeError('Render was cancelled; no output committed')
        finally:
            scene.render.filepath = original_path
            for node, attribute, original in saved:
                setattr(node, attribute, original)
        outputs = []
        if not uses_compositor_output:
            outputs.append((primary, Path(expected_output)))
        for staged, directory in mappings:
            for image in staged.rglob('*'):
                if image.is_file():
                    outputs.append((image, directory / image.relative_to(staged)))
        if not any(output_key(target) == output_key(expected_output) for _, target in outputs):
            raise RuntimeError(f'Render did not create expected staged output: {expected_output}')
        for image, target in outputs:
            actual, fallback = commit_output(image, target, True)
            results[output_key(target)] = actual
            failed |= fallback
        return results[output_key(expected_output)], failed
