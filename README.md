# Blender Render Launcher

A small Windows desktop app for launching Blender animation renders without opening Blender's interface.

## Use

1. Run `Blender Render Launcher.exe`.
2. Drop a `.blend` file onto the window (or click to browse).
3. Review the cached Workbench thumbnail for each camera. The active camera is checked by default; tick any additional cameras you want to render.
4. Set the mode, frame range, output path, engine, resolution, frame rate, format, overwrite, placeholders, and compositor behavior independently for each camera.
5. Click **Add selected to queue**. One queue job is created for every checked camera.
6. Click **Render queue** to process waiting jobs sequentially.

Each queued job shows its completed-frame progress and an estimated finish time based on the average duration of the frames rendered so far.

Output paths support `{camera_name}` and `{blend_name}` tokens. Global frame options can render one still frame or use the first and last animation frames attached to each camera object.

Hold Alt while changing any per-camera setting to copy that value to the other selected cameras.

The Contact sheet button exports all camera previews and compact render details as A4-landscape PDF pages or JPEG images, arranged four columns by two rows.

The app starts with the settings saved in the `.blend` file. All overrides are applied only to the background render process; the original file is not saved or changed.

Playblast mode forces Blender's fast Workbench renderer, producing a camera-view preview that works reliably without opening Blender's viewport.

Each camera defaults to its own output subfolder to prevent queued cameras from overwriting one another. Queue jobs are snapshots, so later edits to a camera card do not change jobs already queued.

If Blender is not detected automatically, click **Set Blender…** and select `blender.exe` (usually under `C:\Program Files\Blender Foundation`).

## Build

```powershell
dotnet publish -c Release
```

The portable single-file app will be in `bin\Release\net8.0-windows\win-x64\publish`. Teammates can run that `.exe` directly without installing the .NET Desktop Runtime; Blender must still be installed.

V2 uses a three-column workspace: queue selection and camera browsing on the left, focused-camera settings in the centre, and render jobs on the right. Camera checkboxes do not change which camera is being edited.

## V3 Watch mode

V3 adds a **Watch mode** control to the render queue:

- Cloud jobs are JSON job files written to a shared NAS folder. Every app that was already watching that folder receives each new job once.
- **Auto start** is independent from Watch mode. When enabled, each incoming job resets a visible 10-second countdown and all waiting jobs render when it ends.
- Existing NAS job files are ignored when Watch mode starts, preventing old work from unexpectedly rendering.
- Start the app with `--watch --auto-start` to enable both options automatically, for example from a Windows startup shortcut.

The partner add-on is in `BlenderAddon/render_queue_sender.py`. Install it in Blender, configure its NAS folder to match the folder selected beside Watch mode, and use **Render > Cloud Render** or **3D View > View > Cloud Playblast**.

Cloud Playblast records the active 3D View shading mode (`Wireframe`, `Solid`, `Material Preview`, or `Rendered`) in the queued job. During rendering, queue progress shows the current frame and refreshes the queue thumbnail from a newly written output image every 10 frames and on the final frame.

Every machine must be able to access the `.blend` and output paths, preferably through consistent UNC NAS paths. V4 Cloud jobs do not depend on the Blender **Overwrite** or **Placeholders** settings for coordination.

## V4 NAS frame claims

V4 Cloud jobs use app-managed NAS frame claims instead of Blender placeholders. Each watching machine claims one available frame at a time, renders it with placeholders disabled internally, verifies that the output is non-empty, records completion, and then claims another frame. A claim heartbeat prevents long frames from being stolen; abandoned claims become available again after two minutes. The incoming Overwrite setting controls whether valid images that existed before this job are kept or rendered again, but it is no longer used for machine-to-machine coordination.

Watch Mode also scans existing V4 jobs when it starts. A machine can therefore join a render after the original Cloud Render or Cloud Playblast was sent. Jobs with a shared `complete.json` marker are ignored, while older V3 job files are not re-queued. Distributed rendering requires an image-sequence format; FFmpeg output is rejected.

## V5.2 distributed true viewport playblast

V5.2 Cloud Playblast only submits a request from the open authoring session. Every listening app can launch its own hidden Blender viewport worker. The workers share the playblast using atomic per-frame NAS claims: each claims a different frame, captures it with Blender's real `render.opengl` viewport operation, stages it locally, safely copies it to the shared output, and then claims another. This uses the saved 3D View shading and world settings without making the author's open Blender perform the work.

Run it from **3D View > View > Cloud Playblast**. V5 currently requires an image sequence format such as JPEG or PNG; FFmpeg is deliberately rejected so the app can verify and preview the output. Cloud Render remains the distributed V4 headless render workflow.

Enable **ONLY MY PC** beside Watch to ignore jobs sent from other computers. The app and Blender menu/preferences display their V5 version so installations can be checked at a glance.

Queue thumbnails are refreshed from the latest completed frame at most once every 10 seconds, with a final refresh when the job completes. This keeps previews current without repeatedly decoding images during rendering.

## V5.3 output detection and frame-range controls

When **Output** is disabled in Blender's Output Properties, the launcher detects the first enabled, connected compositor **File Output** node and uses its directory, filename template, and file format. The focused camera shows a **COMPOSITOR FILE OUTPUT** badge when this fallback is active. Stills-only controls have been removed, and **Range from camera keyframes** now sits directly beneath the focused camera's Frame Range fields.

V5.3.1 remembers cloud job IDs removed with **Clear queue**, **Clear completed**, or the individual remove button. Dismissed jobs are not reconstructed from NAS claim files after the app restarts.

## V5.4 synchronized viewport overlays

Cloud Playblast records the complete viewport-overlay configuration from the 3D View that submitted the job. Every remote viewport worker applies those settings before capturing a frame, so overlays cannot vary according to each machine's saved Blender workspace. For manually queued playblasts, **Viewport overlays** is available in Advanced Options and defaults to the state saved in the loaded blend file.

### V5.4.1 distributed progress

Distributed jobs poll the NAS claim folder every two seconds, so progress updates while other machines finish frames rather than only when the local worker finishes. The queue shows the shared completed-frame count, active worker count, and an ETA calculated from the recent combined throughput of all workers.

V5.4.2 prevents delayed progress messages from an individual Blender worker from replacing a newer NAS count, so distributed progress can only move forwards.

## V5.5 render accountability reports

Distributed final renders and viewport playblasts record the domain user, machine, completion time, and output path for every claimed frame. When the sequence completes, the final worker writes a UTF-8 CSV beside the rendered frames named `render-report-CAMERA-JOBID.csv`. Existing frames retained with Overwrite disabled are listed as `Pre-existing / unknown`. The per-frame work only extends the small NAS completion marker that the coordination system already writes; the CSV is assembled after rendering has finished.

## V5.6 cancel everywhere

## V5.8 experimental tiled stills

Install the V5.8 app on workers and V5.8 sender add-on on the submitting machine. In the add-on preferences enable **Experimental tiled Cloud Images**, set tile size (default 2048) and overlap (default 128), then choose **Cloud Render Image**. Animation and Playblast retain their existing behaviour.

This first integration supports single-frame Cycles scenes with exactly one active multilayer EXR File Output. The compositor is restricted to Render Layers, Denoise, Mix, reroutes and frames. Unsupported layouts fail explicitly. Blender 5.2 with OpenImageIO and numpy in its bundled Python is required for this experimental path. Tiled jobs use `.tilejob.json`, so old apps do not consume them.

Workers claim tiles, run the compositor on overlapping regions, and retain the tile EXRs under the job's NAS `_claims` folder. One worker claims final assembly and launches a separate scanline stitch process. Final output is `CAMERA_FRAME_JOBID.exr` with a tile accountability CSV beside it, in the selected output directory. The unique filename avoids overwriting existing outputs; normal image format and overwrite choices do not apply to this experimental EXR mode. Keep the claim directory until the final output is verified; cleanup is manual for now.

The app displays tile counts and a finishing message at 100% while assembly runs. Cancel everywhere applies to tiled jobs too. Validation includes two independent local Blender worker processes sharing four tiles and producing one final EXR/CSV. Different PCs, network interruptions, huge images, and arbitrary compositor layouts remain unvalidated. Denoising is close but not identical to full-frame results; see `Prototypes/TiledRendering/TILED_RENDERING_RESULTS.md`.

Executable distributions now include the version number: `Blender Render Launcher v5.8.0.exe`.

Distributed waiting or active jobs have a **Cancel everywhere** action. After confirmation, the launcher writes a shared `cancel.json` marker containing the cancelling user and machine. Every V5.6 launcher polls for that marker and terminates its own Blender worker within roughly two seconds; the worker scripts also check the marker before claiming another frame. Completed frames are retained and cancelled jobs are not rediscovered when Watch mode restarts.
