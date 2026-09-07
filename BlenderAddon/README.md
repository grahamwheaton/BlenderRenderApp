# Blender Render Queue Sender V5.7

Install `render_queue_sender.py` as a Blender add-on.

- **Render > Cloud Render Image** sends only the current frame.
- **Render > Cloud Render Animation** sends the complete configured frame range.
- **3D View > View > Cloud Render Playblast** sends the active camera as a viewport playblast.
- Both commands write a job to the configured shared NAS queue folder. Every V3 app already watching that folder receives the job once.

Set the same NAS folder in the add-on preferences and with the folder button beside **Watch mode** in the desktop app. Jobs reference the existing `.blend` and output paths; they do not copy the Blender file.

V4 coordinates shared rendering with NAS frame-claim files. **Placeholders** is not used for Cloud frame allocation. **Overwrite** only controls whether valid images that already existed before the new job are retained or rendered again.
