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
