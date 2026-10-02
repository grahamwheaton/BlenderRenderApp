using BlenderRenderHeadless;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

static class QueueUiTests
{
    public static void Run()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var app = new App(); app.InitializeComponent();
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                if (window.WindowState != WindowState.Maximized) throw new Exception("Not maximized by default");
                var previewFile = Path.Combine(Path.GetTempPath(), "BRH-preview-lock-" + Guid.NewGuid().ToString("N") + ".png");
                var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[]{0,0,0,255}, 4);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(previewFile)) png.Save(output);
                var preview = new PreviewImageConverter().Convert(previewFile, typeof(System.Windows.Media.ImageSource), null!, System.Globalization.CultureInfo.InvariantCulture);
                if (preview == null) throw new Exception("Preview failed to load");
                using (var exclusive = new FileStream(previewFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Move(previewFile, previewFile + ".moved");
                File.Delete(previewFile + ".moved");
                GC.KeepAlive(preview);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
                var queue = (ObservableCollection<RenderJob>)typeof(MainWindow).GetField("_queue", flags)!.GetValue(window)!;
                var source = new RenderJob { BlendFile = @"C:\Fixture\Studio.blend", CameraName = "Camera.front", Width = "1920", Height = "1080", Scale = "100", StartFrame = "1", EndFrame = "250", OutputPath = @"C:\Fixture\render\", Distributed = true, JobId = "original", RequiresViewport = true, RenderMode = "PLAYBLAST" };
                queue.Add(source);
                if (source.ResolutionLabel != "1920x1080(100%)" || source.RenderEngineLabel != "Viewport · Solid") throw new Exception("Queue labels");
                var list = (ListBox)window.FindName("QueueItems"); list.SelectedItem = source;
                var panel = (StackPanel)window.FindName("CameraSettingsContent");
                if (panel.IsEnabled) throw new Exception("Inspector was not read-only");
                var add = (Button)window.FindName("CreateEditedJobButton");
                var discard = (Button)window.FindName("DiscardEditsButton");
                if (add.Visibility != Visibility.Collapsed || discard.Visibility != Visibility.Collapsed) throw new Exception("Edit-only actions visible during review");
                Call("EditSettings_Click", window, new RoutedEventArgs());
                if (add.Visibility != Visibility.Visible || discard.Visibility != Visibility.Visible) throw new Exception("Missing edit actions");
                var draft = (CameraSetup)typeof(MainWindow).GetField("_selectedCamera", flags)!.GetValue(window)!;
                draft.Width = "2048";
                Call("CreateEditedJob_Click", window, new RoutedEventArgs());
                if (queue.Count != 2 || source.Width != "1920" || queue[1].Width != "2048" || queue[1].JobId == source.JobId || !queue[1].RequiresViewport || panel.IsEnabled)
                    throw new Exception("New-job copy did not preserve isolation/viewport settings/read-only reset");
                source.Begin(); Call("RefreshActiveRender");
                source.IsOpening = true;
                if (source.StatusDisplay != "Opening…" || source.CanReorder) throw new Exception("Opening state");
                var openingIcon = source.StatusIcon;
                Call("WorkerReady", source, "BRH_SCENE_READY");
                if (source.IsOpening || source.StatusIcon == openingIcon) throw new Exception("Ready transition/icon");
                if (((Grid)window.FindName("ActiveRenderPanel")).DataContext != source) throw new Exception("Progress follows selection rather than active job");
                source.Status = "Complete"; Call("RefreshActiveRender");
                if (source.LogVisibility != Visibility.Visible || source.RenderRowVisibility != Visibility.Collapsed) throw new Exception("Completed row must show log and hide Render");
                var report = (string)typeof(MainWindow).GetMethod("BuildCompletionReport", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { source })!;
                if (!report.Contains("not a Blender console log") || !report.Contains(source.CameraName)) throw new Exception("Missing completion report context");
                if (!source.ActionsExpanded) throw new Exception("Completed actions did not expand");
                source.ActionsExpanded = false;
                source.Status = "Complete";
                if (source.ActionsExpanded) throw new Exception("Manual collapse was not retained");
                var cancelled = new RenderJob { BlendFile = source.BlendFile, CameraName = "Cancelled camera", StartFrame = "1", EndFrame = "250", Width = "1920", Height = "1080", Scale = "100", OutputPath = source.OutputPath, JobId = "old-cancelled", RequiresViewport = true, RenderMode = "PLAYBLAST" };
                cancelled.MarkGloballyCancelled();
                if (cancelled.RestartVisibility != Visibility.Visible) throw new Exception("Restart missing");
                var restarted = (RenderJob)typeof(MainWindow).GetMethod("CreateEditedCopy", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[]{cancelled, CameraSetup.FromRenderJob(cancelled)})!;
                if (restarted.JobId == cancelled.JobId || restarted.IsGloballyCancelled || restarted.Status != "Waiting" || !restarted.RequiresViewport || cancelled.Status != "Cancelled") throw new Exception("Unsafe restart copy");
                if (((Grid)window.FindName("ActiveRenderPanel")).DataContext != null) throw new Exception("Completed job remains active");
                for (int i = 0; i < 10; i++) queue.Add(new RenderJob { BlendFile = source.BlendFile, CameraName = "Camera." + i, Width = "1920", Height = "1080", OutputPath = source.OutputPath });
                var firstWaiting = queue[1]; var later = queue[2];
                Call("QueueMoveUp_Click", new Button {Tag = later}, new RoutedEventArgs(Button.ClickEvent));
                if (queue[1] != later || !later.IsNext || firstWaiting.IsNext) throw new Exception("Move up/next label");
                Call("QueueMoveDown_Click", new Button {Tag = later}, new RoutedEventArgs(Button.ClickEvent));
                if (queue[1] != firstWaiting || !firstWaiting.IsNext) throw new Exception("Move down/next label");
                later.IsBatchReserved = true;
                Call("QueueMoveUp_Click", new Button {Tag = later}, new RoutedEventArgs(Button.ClickEvent));
                if (queue[2] != later) throw new Exception("Batch in-flight job reordered");
                later.IsBatchReserved = false;
                Call("IgnoreJob_Click", new Button { Tag = later }, new RoutedEventArgs());
                if (!later.IsIgnored || queue.Last() != later || later.RowOpacity >= 1 || later.CanReorder || later.IsNext) throw new Exception("Ignore did not skip/dim/move row");
                Call("IgnoreJob_Click", new Button { Tag = later }, new RoutedEventArgs());
                if (later.IsIgnored || later.RowOpacity != 1) throw new Exception("Unignore failed");
                firstWaiting.Begin(); firstWaiting.ReportFrame(100, null);
                later.Status = "Failed";
                queue.Add(cancelled);
                queue.Add(new RenderJob { Status = "Complete", BlendFile = source.BlendFile, CameraName = "Completed sequence", Width = "1920", Height = "1080", Scale = "100", StartFrame = "1", EndFrame = "50", Format = "PNG", Distributed = true, LogPath = "fixture.log" });
                var cloudFolder = Path.Combine(Path.GetTempPath(), "BRH-cloud-restart-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(cloudFolder);
                var blend = Path.Combine(cloudFolder, "fixture.blend"); File.WriteAllText(blend, "fixture");
                var id = Guid.NewGuid().ToString();
                var manifestPath = Path.Combine(cloudFolder, "original.camerabatch.json");
                var manifest = System.Text.Json.JsonSerializer.Serialize(new { batchId = "batch", jobs = new[] {new { jobId = id, batchId = "batch", blendFile = blend, cameraName = "Camera", senderMachine = "ORIGINAL-PC", requiresViewport = true, distributed = true, tileSize = 0 }} });
                File.WriteAllText(manifestPath, manifest);
                var cloud = new RenderJob { JobId = id, CloudManifestPath = manifestPath, BlendFile = blend, Distributed = true, CoordinationFolder = cloudFolder };
                var claims = Path.Combine(cloudFolder, "_claims", id); Directory.CreateDirectory(claims);
                var remoteImage = Path.Combine(cloudFolder, "remote.png"); File.WriteAllBytes(remoteImage, new byte[] { 1 });
                File.WriteAllText(Path.Combine(claims, "1.done"), System.Text.Json.JsonSerializer.Serialize(new { output = remoteImage, machine = "OTHER-PC" }));
                var latest = typeof(MainWindow).GetMethod("LatestSharedPreview", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] {cloud});
                if (!Equals(latest, remoteImage)) throw new Exception("Remote preview was not discovered");
                File.WriteAllText(Path.Combine(claims, "cancel.json"), "{}");
                var republished = CloudRestart.Publish(cloud);
                using (var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(republished)))
                {
                    var root = document.RootElement;
                    if (root.GetProperty("jobId").GetString() == id || root.TryGetProperty("batchId", out _) || root.GetProperty("senderMachine").GetString() != "ORIGINAL-PC" || !root.GetProperty("requiresViewport").GetBoolean()) throw new Exception("Cloud restart changed identity/settings incorrectly");
                }
                if (File.ReadAllText(manifestPath) != manifest || !File.Exists(Path.Combine(claims, "cancel.json"))) throw new Exception("Original cloud job was mutated");
                File.WriteAllText(Path.Combine(claims, "1.claim"), "{}");
                try { CloudRestart.Publish(cloud); throw new Exception("Active claims accepted"); } catch (IOException) { }
                Call("UpdateQueueState");
                var folder = Path.Combine(Path.GetTempPath(), "BRH-queue-ui"); Directory.CreateDirectory(folder);
                foreach (var size in new[] {new Size(2240, 1100), new Size(1600, 900), new Size(1200, 750)})
                {
                    var content = (FrameworkElement)window.Content;
                    content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
                    var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using var file = File.Create(Path.Combine(folder, $"queue-{size.Width}.png")); encoder.Save(file);
                }
                Console.WriteLine("PASS queue UI: read-only, independent edited job, preserved viewport, active-only progress. Screenshots: " + folder);
            }
            catch (Exception ex) { error = ex; }
            finally { window?.Close(); app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error != null) throw error;
    }
}
