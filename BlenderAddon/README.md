# Blender Render Queue Sender

Install `render_queue_sender.py` as a Blender add-on.

- **Render > Cloud Render** sends the active camera as a final render.
- **3D View > View > Cloud Playblast** sends the active camera as a Workbench playblast.
- Both commands write a job to the configured shared NAS queue folder. Every V3 app already watching that folder receives the job once.

Set the same NAS folder in the add-on preferences and with the folder button beside **Watch mode** in the desktop app. Jobs reference the existing `.blend` and output paths; they do not copy the Blender file.

V4 coordinates shared rendering with NAS frame-claim files. **Placeholders** is not used for Cloud frame allocation. **Overwrite** only controls whether valid images that already existed before the new job are retained or rendered again.
