# Blender Render Queue Sender V5.11.0

Install `dist/Blender-Render-Queue-Sender-v5.11.0.zip` using Blender's Install from Disk, or install `render_queue_sender.py` directly.

V5.11.0 adds **3D View > View > Cloud Playblast Still Images — Selected Cameras**. Select cameras and set the current frame; each camera gets one viewport still job. Uses the current viewport shading and overlay settings. Saves the shared scene once and leaves the scene's camera and animation range unchanged. The existing selected-camera animation playblast remains available.

V5.10.1 also retries saves that return FINISHED while Blender still reports unsaved changes. All retryable save failures share a three-attempt limit with 1- and 2-second delays. Pending view-layer updates are flushed before retrying a dirty save; this is not a UI event-loop wait. Console diagnostics show each failure and retry decision. A persistent dirty state still blocks submission. Backups remain enabled according to your existing preferences; no separate scene copies are made.

V5.9.1 distinguishes a cancelled/non-finished save from a FINISHED save that still reports unsaved changes. Diagnostics include the operator result, dirty flag and file path, also printed to Blender's console. Submission remains blocked when saving cannot be verified; this update does not bypass that safeguard or assume every failure is a NAS lock.

**Render > Cloud Render Image Selection** sends all selected scene cameras at the current frame in one batch. Requires launcher 6.6+. Each camera gets its own queue entry, resolution and camera-specific filename; workers reuse one loaded scene for available cameras. No scene copies or animation are created. Paths without `{camera_name}` automatically receive a camera-name prefix. Configure additional compositor File Output nodes with camera-specific paths as well.

V5.8.2 saves the shared scene only when dirty and retries recognised backup/permission failures three times (1- and 2-second delays). If saving still fails the job is not sent. Backup preferences are left unchanged; no separate scene snapshots are created. Save As remains available for making your own manual copy. Later render workers see the latest saved contents of the shared file.

- **Render > Cloud Render Image** sends only the current frame.
- **Render > Cloud Render Animation** sends the complete configured frame range.
- **3D View > View > Cloud Render Playblast** sends the active camera as a viewport playblast.
- Both commands write a job to the configured shared NAS queue folder. Every V3 app already watching that folder receives the job once.

Set the same NAS folder in the add-on preferences and with the folder button beside **Watch mode** in the desktop app. Jobs reference the existing `.blend` and output paths; they do not copy the Blender file.

V4 coordinates shared rendering with NAS frame-claim files. **Placeholders** is not used for Cloud frame allocation. **Overwrite** only controls whether valid images that already existed before the new job are retained or rendered again.
# Sender v5.10.0 — selected camera playblasts

Install `dist/Blender-Render-Queue-Sender-v5.10.0.zip`. In **3D View > View**, choose **Cloud Playblast selected cameras** after selecting camera objects. Non-camera objects are ignored.

Sends the scene frame range/step for each selected camera, preserving the invoking viewport's shading and overlay settings and per-camera resolution. Saves the original shared scene once; no additional `.blend` copies and no rendering in the submitting Blender. Outputs use `{camera_name}` or a camera-name prefix to avoid collisions.

Each camera is a separate distributed viewport job (not a still-image camera batch); existing v8 workers can consume these jobs. If a NAS error interrupts publishing, the error reports how many jobs were already sent. Check the queue before resending.

