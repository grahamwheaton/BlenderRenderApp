# Blender Render Queue Sender V5.9.1

Install `dist/Blender-Render-Queue-Sender-v5.9.1.zip` using Blender's Install from Disk, or install `render_queue_sender.py` directly.

V5.9.1 distinguishes a cancelled/non-finished save from a FINISHED save that still reports unsaved changes. Diagnostics include the operator result, dirty flag and file path, also printed to Blender's console. Submission remains blocked when saving cannot be verified; this update does not bypass that safeguard or assume every failure is a NAS lock.

**Render > Cloud Render Image Selection** sends all selected scene cameras at the current frame in one batch. Requires launcher 6.6+. Each camera gets its own queue entry, resolution and camera-specific filename; workers reuse one loaded scene for available cameras. No scene copies or animation are created. Paths without `{camera_name}` automatically receive a camera-name prefix. Configure additional compositor File Output nodes with camera-specific paths as well.

V5.8.2 saves the shared scene only when dirty and retries recognised backup/permission failures three times (1- and 2-second delays). If saving still fails the job is not sent. Backup preferences are left unchanged; no separate scene snapshots are created. Save As remains available for making your own manual copy. Later render workers see the latest saved contents of the shared file.

- **Render > Cloud Render Image** sends only the current frame.
- **Render > Cloud Render Animation** sends the complete configured frame range.
- **3D View > View > Cloud Render Playblast** sends the active camera as a viewport playblast.
- Both commands write a job to the configured shared NAS queue folder. Every V3 app already watching that folder receives the job once.

Set the same NAS folder in the add-on preferences and with the folder button beside **Watch mode** in the desktop app. Jobs reference the existing `.blend` and output paths; they do not copy the Blender file.

V4 coordinates shared rendering with NAS frame-claim files. **Placeholders** is not used for Cloud frame allocation. **Overwrite** only controls whether valid images that already existed before the new job are retained or rendered again.
