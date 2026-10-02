# Blender Render Launcher

## V8.0.0 — queue-first UI (`ui-queue-first` branch)

- Promotes the queue-first UI and the changes below to version 8.0.0.
- Build: `dist/v8/Blender Render Launcher v8.0.0.exe`.
- Sender add-on remains v5.9.1; no add-on version change is required for this app release.

### Previous V7.0.10 changes

- Completed rows always show View log and hide Render. If no local worker log exists, View log creates a labelled completion report with job details and available shared worker receipts; it does not claim to contain remote Blender console output.
- Build: `dist/v7-ui-preview/Blender Render Launcher v7.0.10.exe`. Sender add-on unchanged.

### Previous V7.0.9 changes

- Removed up/down row controls; drag handle retained.
- Completion actions (PSD/MP4, log and sequence repair) are inline buttons, with Retry still available for failed jobs. No Actions dropdown. Worker/export/error details remain in the row tooltip.
- Queue reserves a consistent vertical scrollbar gutter so the header and row edges align even with only a few jobs; removed ListBox default padding.
- Build: `dist/v7-ui-preview/Blender Render Launcher v7.0.9.exe`. Sender add-on unchanged.

### Previous V7.0.8 changes

- Top checkboxes grouped into Watch (Watch, Only my PC, Auto start, Auto retry, Force overwrite), Performance (Allow preload, Allow Children), and Automatic exports (Make PSD, Make MP4). Existing settings and behaviour unchanged.
- Build: `dist/v7-ui-preview/Blender Render Launcher v7.0.8.exe`. Sender add-on unchanged.

### Previous V7.0.7 changes

- Status column doubled from 85 to 170 pixels; full status remains available on hover.
- Actions/details expander replaced with an Actions menu on the row. Existing eligible PSD/MP4 exports, logs, retry and sequence repair commands are retained, alongside diagnostic/export/worker messages.
- Row action buttons share a 40-pixel height, consistent padding, spacing and vertical alignment, on one line.
- Build: `dist/v7-ui-preview/Blender Render Launcher v7.0.7.exe`. Sender add-on unchanged.

### Previous V7.0.6 changes

- Allow preload (off by default, saved per PC) opens one upcoming scene while the current queue runs. Resource checks use the existing CPU/GPU limits and RAM/VRAM reserves. Unknown telemetry disables speculative loading. Scene memory is estimated, not guaranteed.
- Preloaded Blender waits without executing the render worker or claiming frames. At its turn the same process runs the normal worker/claim protocol. If all available work is claimed elsewhere, it exits and the queue moves on; the shared job remains visible for progress tracking.
- Queue changes, source-file modification, cancellation and memory pressure discard an unused preload. Background renders, camera batches and viewport workers are supported; tiled jobs are not preloaded. Single-row Render does not preload another row.
- Larger preview is now first. Actions stay on one horizontal line; narrow windows scroll horizontally instead of wrapping buttons.
- Build: `dist/v7-ui-preview/Blender Render Launcher v7.0.6.exe`. Sender add-on unchanged. Local Blender smoke tests cover deferred execution, same-process reuse and losing a frame claim; multi-PC production testing is still required.

### Previous V7.0.5 changes

- Fixed modern compositor File Output validation: no implicit frame suffix; honours explicit frame placeholders, output extensions and per-item format overrides. Tested with actual Blender 5.2 renders.
- Ignore/Unignore skips a row locally, dims it and moves it to the bottom. It does not cancel other watchers.
- Per-row Render runs only that waiting job on this PC (including only that camera in a batch). Auto start is turned off to avoid running the remaining queue afterwards.
- Compact output paths retain the full path in their tooltip; row actions wrap instead of extending off the row.
- Shared PNG/JPEG/BMP/TIFF previews follow the newest completion receipt, from any worker, every ten seconds using background polling. EXR decoding remains unsupported by the inline image preview.
- Build: `dist/v7-ui-preview/Blender Render Launcher v7.0.5.exe`. Sender add-on unchanged.

### Previous V7.0.4 changes

- Restart now republishes cloud-origin rows to their original watch folder after confirmation, using a fresh ID and preserving sender identity, render settings and output policy. All eligible watchers can pick it up; filters and Auto start still apply. Local jobs remain local. The original manifest/cancellation are untouched, active old claims block restart, and restarting a batch row resubmits only that camera.
- Preview column shows the latest available row image. Generate preview launches an isolated background Workbench worker for the camera/start frame, producing a local temporary PNG without saving the `.blend` or writing to render outputs. Requests run one at a time.
- Removed the Frames X–Y line from Actions/details. The compositor output-filename validation bug reported in the Patronus logs is NOT fixed in this release.

- Per-row progress is 4px high. Completion opens Actions/details once; users can collapse it afterwards. Existing format/job-specific export and repair eligibility is unchanged.
- Cancelled rows expose Restart (cloud/local routing as described above); restart is unavailable while the local queue is running.

- Previews use an OnLoad decoder with read/write/delete sharing and dispose the source stream immediately. Regression tests keep the decoded image alive while exclusively opening, renaming and deleting its temporary source.
- Starts maximized. Output links only occupy the visible text area; order controls are 40px. Clear and Cancel Everywhere are row buttons. Resolution includes scale and a Render engine column follows it (Saved setting means the scene's engine is retained).
- Sender v5.9.1 adds precise save-validation diagnostics, without relaxing the save safeguard.

- Compact inspector and tighter queue columns; edit-only actions appear only while editing.
- Queue order is top to bottom, with a NEXT IN LINE marker. Up/down buttons and thresholded drag/drop reorder waiting jobs, including while a separate job runs. Camera jobs already dispatched together in a batch cannot be reordered mid-batch.
- Click a Blender filename to open it in the configured Blender. Each row has a status icon and a 2-pixel progress bar. Opening is shown until the worker confirms scene loading has finished.

- Queue table is the main workspace; select a row to review its settings. Expand **Actions / details** for exports, recovery, logs and global cancellation.
- **Edit settings** unlocks a draft. **Add as new job** adds a separate local job, without modifying the original job or the `.blend`. Tiled/viewport copies retain their worker type but use a fresh, local claim namespace. Existing output paths are retained: choose a new path if you need separate results.
- Loading/dropping a `.blend` switches **Auto start** off and queues every camera. Loaded camera tools, copy/paste, selection, output copying and contact sheets remain in the inspector's bottom expander.
- The bottom progress/preview follows only the currently opening/rendering local job.
- Read-only settings include advanced options; loading the source `.blend` is required to inspect camera keyframes for edited copies of received jobs.
- Build: `dist/v7-ui-preview/Blender Render Launcher v7.0.4.exe`. Sender: `dist/Blender-Render-Queue-Sender-v5.9.1.zip` (unchanged).
- UI regression: `dotnet run --project Tests/ChildWorkers -- --queue-ui` (isolated fixtures and WPF layout snapshots, not a live NAS rendering test).

## V6.6.0 — selected-camera still batches

Install sender **5.9.0** and use **Render > Cloud Render Image Selection** with camera objects selected in the current scene. It saves the shared blend once if dirty and sends every selected camera at the current frame, with per-camera resolution. No animation is created and the active camera is not changed. Each camera gets a separate queue entry/claim/audit report. One atomic `.camerabatch.json` publishes the group; launcher 6.6+ is required (older launchers ignore these batches).

Each participating worker loads the blend once per batch attempt, then claims available cameras individually, skipping cameras currently owned by peers. Cameras completed by other machines are reported complete locally too. Camera errors do not prevent other cameras being attempted. Auto retry may reload the scene for failed cameras; ordinary separate submissions are not combined. Allow Children does not create extra workers for these batches. Scene data still comes from the one shared blend and later saves remain visible to workers that have not opened it yet.

If the output path contains `{camera_name}`, it is expanded per camera. Otherwise the sanitized camera name is prefixed to the output filename. Identical resolved paths are refused. Primary compositor output selection follows existing sender behaviour; users with additional File Output nodes must give those outputs camera-specific paths too.

Builds: `dist/v6.6-experimental/Blender Render Launcher v6.6.0.exe` and `dist/Blender-Render-Queue-Sender-v5.9.0.zip`. Tests: `Tests/test_camera_batch.py` uses two actual Blender workers and four cameras; `dotnet run --project Tests/ChildWorkers -c Release -- --camera-batch-ui` checks launcher protocol/intake.

## V6.5.5 — audit CSVs in the watch folder

New shared final-render, viewport-playblast and tiled-render audit CSVs are saved directly into the job's configured watch folder (normally `W:\Working Graphics\_3D RESOURCE\CloudRender`), rather than beside rendered outputs. Filenames retain their camera/job identifiers; reports are published via temporary files in the same folder. Existing CSVs are not moved. Partial Delete/repair reads both legacy output-folder CSVs and new watch-folder CSVs while retaining output-path safety checks.

Update launchers on every worker; the sender add-on is unchanged. Delete watch folder also archives these CSVs with the rest of its contents. Build: `dist/v6.5.5-experimental/Blender Render Launcher v6.5.5.exe`.

## V6.5.4 — shared-scene saves, still claim owner and Force overwrite

Sender add-on **5.8.2** keeps the original shared blend file. It skips saving an unchanged scene; dirty scenes require a confirmed successful save. Recognised backup/permission/file-lock errors get three save attempts with 1- and 2-second delays. A persistent failure blocks submission with explicit NAS/manual Save As guidance. No snapshot copies or changes to Blender's backup preferences are made. Later workers read the latest saved shared scene.

Shared non-tiled stills display **Job claimed by MACHINE** using the worker's current heartbeat receipt. The underlying Waiting/Rendering queue state stays intact and status reverts when the claim disappears/expires. Install this launcher on watching PCs to see it.

**Force overwrite** beside Auto retry is off by default and remembered per PC. On overrides the job's output overwrite setting for ordinary final renders/playblasts; off uses saved settings (which may already enable overwrite). It does not erase shared completion receipts or steal claimed work, does not overwrite MP4/PSD exports, and does not change the uniquely named tiled output workflow. Set consistently across participating PCs before starting a new shared job if all existing images must be replaced. Retry/repair retains completed shared-frame protection.

Portable launcher: `dist/v6.5.4-experimental/Blender Render Launcher v6.5.4.exe`. Installable sender: `dist/Blender-Render-Queue-Sender-v5.8.2.zip`.

## V6.5.3 — Auto retry and Delete watch folder

**Auto retry**, beside Allow Children, defaults on (including existing settings files) and remembers an explicit opt-out. Failed render jobs get up to three additional attempts, 30 seconds apart, with separate logs and a visible countdown. It works independently of Watch/Auto start. Cancel queue, global cancellation or switching Auto retry off prevents another attempt. Original job settings remain in effect, including overwrite for local jobs; completed shared-frame claims are retained.

**Delete watch folder**, beside Reset watch, clears the configured CloudRender folder after confirmation. Stop rendering and queued work on ALL PCs first. For recovery, files/subfolders are moved into a unique `CloudRender-cleared-...` sibling folder, not permanently erased. The watch directory itself and outputs outside it remain intact. Worker claims, active export locks, linked paths and folders not named CloudRender are refused. Partial filesystem failures can leave some files in the recovery folder and others in place. Watch switches off on this PC; this is maintenance, not a replacement for Cancel everywhere. Concurrent submissions from other machines must be stopped by the operator.

Build: `dist/v6.5.3-experimental/Blender Render Launcher v6.5.3.exe`. Tests: `dotnet run --project Tests/ChildWorkers -c Release -- --retry-cleanup`.

## V6.5.2 — failure logs, Retry and Reset watch

Every new render attempt saves a timestamped local log under `%LOCALAPPDATA%/BlenderRenderLauncher/logs`. **View log** opens it in Notepad; send this file when reporting a failure. It includes machine/version, job settings, Blender arguments, stdout/stderr, child-worker messages and exit codes, exceptions and final status. Each retry gets a separate file; logs survive clearing the queue or closing the app. Older failures cannot be reconstructed retrospectively. Logs include project paths and are not automatically uploaded.

**Retry** on failed jobs requeues that job locally. Press Render jobs or use Watch + Auto start. Shared jobs retain their original identity/claims and completed-frame tracking; globally cancelled jobs cannot be resumed this way. Local overwrite settings still apply (with confirmation). Fix missing assets, permissions or settings before retrying.

**Reset watch**, beside Clear queue, clears this PC's dismissal/discovery history and rescans the current NAS folder. It restores only still-open shared jobs, skips completed/cancelled jobs, respects ONLY MY PC, and keeps queued jobs without duplicating them. Auto start remains optional; the button also performs a one-off scan with Watch off. It does not modify or restart remote jobs.

Portable build: `dist/v6.5.2-experimental/Blender Render Launcher v6.5.2.exe` (includes FFmpeg). Regression: `dotnet run --project Tests/ChildWorkers -c Release -- --recovery`.

## V6.5.1 — bundled FFmpeg

Share `dist/v6.5.1-experimental/Blender Render Launcher v6.5.1.exe`; no separate FFmpeg installation or PATH setup is required. Make MP4 and automatic MP4 export use the embedded static Windows x64 FFmpeg build. First use extracts and SHA-256 verifies it under `%LOCALAPPDATA%/BlenderRenderLauncher/tools/ffmpeg-9.0-<hash>/`, with its original licence and build/source information. Subsequent exports reuse it. The executable is larger; no administrator access or runtime download is required.

FFmpeg is an independent subprocess, supplied by Gyan (9.0 full build, GPL v3). Original notices and exact upstream source reference are in `Vendor/FFmpeg/LICENSE` and `Vendor/FFmpeg/README.txt`, embedded unchanged. Review redistribution obligations before distributing outside your organisation: https://ffmpeg.org/legal.html . The large binary is excluded from Git; stage that exact build as `Vendor/FFmpeg/ffmpeg.exe` before building (missing payload fails the build; runtime checks its pinned hash). Test with `dotnet run --project Tests/ChildWorkers -c Release -- --bundled-ffmpeg`.

## V6.5 — partial sequence repair

Completed shared, non-tiled image sequences now offer **Partial Delete** and **Check and rerun** beside Make MP4. Partial Delete groups frames by machine using the accountability receipts (or CSV fallback), confirms the count and output folder, then moves the selected images into `_partial-delete/<job-id>/<batch>` beside the originals. This is recoverable; no existing MP4/PSD is removed.

Check and rerun checks every expected frame, respecting frame step, for missing or zero-byte files. With confirmation it publishes a new NAS job with overwrite disabled, preserves original settings and sender identity, and carries forward valid frame receipts/attribution. Watchers discover it through the normal shared-folder scan; Watch, ONLY MY PC and Auto start still apply. The old job is retired to prevent it restarting alongside its repair. Use the new job for subsequent repairs. Regenerate derived MP4/PSD exports manually when needed.

Safety: operations refuse active frame claims, concurrent automatic exports/repairs, duplicate repairs, linked paths, and recorded outputs outside the configured output folder. Keep the original shared job manifest and frame receipts or CSV. This first version requires absolute, accessible output paths (Blender `//` paths are resolved against the blend file), checks existence/size rather than image corruption, and does not repair tiled images or local-only jobs. A failed move can leave a partial backup; good frames remain untouched and Check and rerun finds the actual gaps.

Regression checks: `dotnet run --project Tests/ChildWorkers -c Release -- --repair`. Portable build: `dist/v6.5-experimental/Blender Render Launcher v6.5.0.exe`.

## V6.4 — hardware-aware turbo and Eevee children

Allow Children now supports shared **Eevee image-sequence renders** as well as playblasts. Both `BLENDER_EEVEE` and `BLENDER_EEVEE_NEXT` are recognised. Cycles, tiled/single images, video outputs and non-distributed jobs remain excluded. The 24-frame eligibility minimum, sequential child trials and configured max-child cap still apply.

The old free-VRAM-must-match-used-VRAM rule is removed. Before launching the primary, the controller measures GPU memory use. During rendering it estimates per-worker VRAM growth above that baseline, retaining the largest observed estimate. Adding another worker requires that estimate plus 15% margin and the safety reserve. RAM admission similarly allows a primary-sized worker plus 15% and the reserve, rather than requiring twice its memory. Other apps can affect estimates; this cannot guarantee avoidance of allocation failures or rendering spikes.

Turbo defaults allow CPU up to 95% and GPU up to 100%; saturation alone no longer blocks a trial. Each child's actual throughput gain still determines whether it stays. Default reserves are **5% of each machine's total RAM/VRAM**, with floors of **2 GB RAM / 1 GB VRAM**, configurable in Settings. On 128 GB RAM / 24 GB VRAM that is approximately 6.4 GB / 1.2 GB reserved, in addition to predicted new-worker use. Unknown GPU telemetry or startup memory baseline prevents children; low memory retires them between frames. Multi-GPU machines use conservative worst-free-card sampling; workers are not pinned to individual GPUs.

Upgrading old settings replaces the previous unchanged resource defaults (60/65% CPU/GPU and 8/4 GB reserves) with turbo defaults. Other tuning, including max children, is preserved. Custom non-default limits are retained. No Blender add-on update is required.

Portable build: `dist/v6.4-experimental/Blender Render Launcher v6.4.0.exe`. Add `-- --eevee` to the regression-test command to exercise real Eevee worker coordination and retirement.

## V6.3.1 — worker settings and faster admission

**Settings…**, beside **Set Blender…**, contains grouped controls:

- **Worker limits:** max children (extra processes, excluding the primary; 0–8, default 2).
- **Timing:** warm-up (10 seconds), resource-check interval (3 seconds), consecutive healthy checks (2), startup allowance (2×), minimum remaining work (15 seconds).
- **Resource limits:** maximum CPU/GPU usage and minimum free RAM/VRAM. The additional memory-reservation checks remain enforced.
- **Performance checks:** trial duration (20 seconds) and minimum throughput improvement (10%).

Settings are validated and saved per user alongside watch preferences. Restore defaults only takes effect when Save is clicked. Changes apply to the next job; running jobs keep their settings snapshot.

The controller adds children **one at a time** up to the configured maximum. It measures Blender startup through the first completed frame, and requires remaining work of at least the larger of the startup allowance and minimum remaining time. This replaces the old fixed 60-second requirement. Each child must demonstrate the configured gain before another is considered. Failed or unhelpful trials stop further additions for that job. Resource pressure can retire children safely between frames.

Queue status now explains the specific limit: warm-up, timing frames, healthy-check count, CPU/GPU load, free RAM/VRAM, insufficient remaining time, or max children reached. Max children is a ceiling, not a requested worker count. Shared playblasts only; unknown NVIDIA telemetry still prevents additions.

Portable build: `dist/v6.3.1-experimental/Blender Render Launcher v6.3.1.exe`.

Additional regression modes: `--policy-only` checks settings and short-job admission; `--settings-ui` renders the settings layout; `--controller` drives the full scheduler with protocol-test processes and fixed resource samples, verifying primary + two children and 600 frames with no leftover claims. Real Blender viewport coordination remains covered by `--viewport`. Production speed-up is hardware/scene dependent.

## V6.3 experimental — Allow Children

The saved **Allow Children** checkbox is off by default. It can trial **one extra local Blender process** (two total) for distributed image-sequence playblasts with at least 24 frames. Both use the original job's settings and NAS frame claims. Normal final renders, tiled images, video outputs and uncoordinated local jobs are deliberately excluded from this first version.

The launcher waits for real local frame timings and three healthy resource samples. A trial requires CPU below 60%, GPU below 65%, at least 8 GB free RAM (and twice the primary's private memory), and at least 4 GB free VRAM (and enough to match current GPU memory use). NVIDIA `nvidia-smi` telemetry must be available; unknown GPU capacity means no child. It also requires at least 12 remaining frames and an estimated minute of work left.

After the child produces frames, aggregate local throughput is compared against the single-worker baseline over at least 60 seconds. Less than 10% gain retires the child between frames; low RAM/VRAM or unavailable telemetry also retires it. Each job gets at most one trial. These are conservative heuristics, not a guarantee of acceleration, especially when scene complexity changes between frames. Queue cards show the decision/status.

Cancelling locally or globally stops both owned workers. Closing the launcher stops owned render workers as well. A child failure does not terminate the primary: stale NAS claims can be reclaimed by the existing worker protocol. Graceful retirement finishes the current frame, releases its claim and exits before taking another. The new worker scripts recheck frame completion after acquiring a claim to avoid rendering a frame another worker just finished.

Portable build: `dist/v6.3-experimental/Blender Render Launcher v6.3.0.exe`. No sender add-on update required. Try a longer cloud playblast with the option off, then on, to compare total elapsed time on your hardware.

Regression tests: `dotnet run --project Tests/ChildWorkers/ChildWorkers.csproj -c Release` tests two background Workbench workers; add `-- --viewport` for true viewport workers. Tests create small temporary fixtures, verify every frame is owned exactly once, and check both idle and active child retirement. Set `BLENDER_EXE` if Blender is not installed at the default Blender 5.2 path. These validate safety/coordination, not an end-to-end speed improvement on production scenes.

## V6.2 — compositor-only renders and automatic exports

- Scene **Output can be OFF** when using a compositor File Output node. Compositor output filenames are now resolved by Blender itself, including camera tokens, frame numbers and EXR extensions; multilayer channel names are not appended to the filename.
- **Make PSD** and **Make MP4** checkboxes beside Watch are saved between sessions (off by default). Enabling them also processes eligible completed jobs currently in the queue. Conversion runs sequentially on this PC; it does not change the render's Complete status if conversion fails.
- PSD uses RLAYER4 and saves beside the actual compositor EXR. A normal scene multilayer EXR with Combined/Diffuse passes is not interchangeable with the compositor's Image/Diff output. Turn scene Output off and resend jobs created with the wrong source; existing queued job settings do not change retroactively.
- MP4 uses High/CRF 18 and playback FPS divided by frame step; it saves as `camera_start-end.mp4` beside the sequence. Existing MP4s are never overwritten automatically.
- NAS export locks prevent simultaneous automatic conversion of a shared job; completion receipts prevent repeating successful exports on other workers/restarts. Errors appear on the job and in Render Log. Manual export buttons remain available for retry or file selection.
- Keep the launcher open until automatic conversion finishes. Closing is blocked during conversion to allow safe completion.
- No sender add-on or EXR converter update is required. Workers should use V6.2 to get the compositor filename fix. The PSD converter must be accessible on the converting PC, and MP4 requires FFmpeg.

Portable launcher: `dist/v6.2/Blender Render Launcher v6.2.0.exe`.

## V6.1 — Make PSD

Completed EXR and multilayer EXR jobs show **Make PSD…**. This uses `BlenderBatchEXR-CLI.exe` with `--workflow rlayer4 --format auto --skip-existing`: 8-bit sRGB, COMP/RLAYERS groups and the converter's RLAYERS correction. Image and Diff passes are required; there is no silent fallback to uncorrected HDR output.

Outputs are saved beside each original EXR, automatically switching to PSB when PSD limits require it. Existing PSD/PSB files and source EXRs are preserved. Each file is processed sequentially on this PC, with converter messages shown in the dialog. **Stop after this file** allows the current conversion to finish safely.

The launcher checks beside its EXE, then `W:\Working Graphics\_Plugins\Blender\Blender Batch EXRS` (with its UNC equivalent as fallback). If unavailable, it asks you to locate the CLI. The converter is not bundled.

Reported frame paths and NAS completion records identify this job's EXRs. Tiled jobs use only the final stitched EXR from `complete.json`, never intermediate tiles. If records are unavailable, select the job's EXRs manually; the launcher never converts an entire folder implicitly.

Portable launcher: `dist/v6.1/Blender Render Launcher v6.1.0.exe`. No Blender add-on update required.

## V6.0 — Make MP4

Completed PNG, JPEG and TIFF sequence jobs now show **Make MP4…**. Choose a quality preset (High by default), review playback FPS, and save an H.264 MP4. High uses CRF 18, Medium 23, and Small file 28. Export runs on this PC, not across the render farm.

- Playback defaults to job FPS divided by frame step to preserve timing. You can override it.
- Original resolution is retained; odd dimensions are padded to even dimensions for H.264 compatibility. MP4 has no alpha or audio.
- Exact reported frame paths or shared NAS completion records are used. If records are unavailable, select the first numbered image. Missing/empty frames stop export rather than silently shortening it.
- Progress and cancellation are available. A temporary video is finalized only after successful encoding; cancellation preserves any previous destination MP4 and never changes source images.
- Install FFmpeg with the libx264 encoder, place `ffmpeg.exe` beside the launcher, or locate it when prompted. FFmpeg is not bundled in the portable EXE.
- EXR/multilayer EXR sequences are excluded: these require a colour-managed display conversion first. Tiled single-image jobs and existing video outputs do not show this button.
- The Blender add-on does not need an update for this launcher-only feature.

Portable launcher: `dist/v6.0/Blender Render Launcher v6.0.0.exe`.

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

Install the V5.8 app on workers and V5.8.1 sender add-on on the submitting machine. Set tile size (default 2048) and overlap (default 128) in the add-on preferences, then choose **Render > Cloud Image (tiled)**. **Cloud Render Image** always sends a normal single-frame job. Animation and Playblast retain their existing behaviour.

This first integration supports single-frame Cycles scenes with exactly one active multilayer EXR File Output. The compositor is restricted to Render Layers, Denoise, Mix, reroutes and frames. Unsupported layouts fail explicitly. Blender 5.2 with OpenImageIO and numpy in its bundled Python is required for this experimental path. Tiled jobs use `.tilejob.json`, so old apps do not consume them.

Workers claim tiles, run the compositor on overlapping regions, and retain the tile EXRs under the job's NAS `_claims` folder. One worker claims final assembly and launches a separate scanline stitch process. Final output is `CAMERA_FRAME_JOBID.exr` with a tile accountability CSV beside it, in the selected output directory. The unique filename avoids overwriting existing outputs; normal image format and overwrite choices do not apply to this experimental EXR mode. Keep the claim directory until the final output is verified; cleanup is manual for now.

The app displays tile counts and a finishing message at 100% while assembly runs. Cancel everywhere applies to tiled jobs too. Validation includes two independent local Blender worker processes sharing four tiles and producing one final EXR/CSV. Different PCs, network interruptions, huge images, and arbitrary compositor layouts remain unvalidated. Denoising is close but not identical to full-frame results; see `Prototypes/TiledRendering/TILED_RENDERING_RESULTS.md`.

Executable distributions now include the version number: `Blender Render Launcher v5.8.0.exe`.

Distributed waiting or active jobs have a **Cancel everywhere** action. After confirmation, the launcher writes a shared `cancel.json` marker containing the cancelling user and machine. Every V5.6 launcher polls for that marker and terminates its own Blender worker within roughly two seconds; the worker scripts also check the marker before claiming another frame. Completed frames are retained and cancelled jobs are not rediscovered when Watch mode restarts.
