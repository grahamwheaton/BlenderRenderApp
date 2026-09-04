# Blender Render Queue Sender

Install `render_queue_sender.py` as a Blender add-on. Its two commands appear at the bottom of Blender's **Render** menu.

- **Send to Render Queue Local** sends only to a V3 Blender Render app running in Watch mode on the same computer.
- **Send to Render Queue Cloud** writes a job to the configured shared NAS queue folder. Every V3 app already watching that folder receives the job once.

Set the same NAS folder in the add-on preferences and with the folder button beside **Watch mode** in the desktop app. Cloud jobs reference the existing `.blend` and output paths; they do not copy the Blender file.

For cooperative rendering on several computers, enable Blender's **Placeholders** setting and disable **Overwrite** before sending the job.
