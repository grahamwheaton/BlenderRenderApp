using BlenderRenderHeadless;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Collections.ObjectModel;
using System.Windows.Controls;

static class CameraBatchUiTests
{
    public static void Run()
    {
        Exception? error=null;
        var thread=new Thread(()=>
        {
            var app=new App(); app.InitializeComponent();
            app.Dispatcher.BeginInvoke(new Action(async()=>
            {
                MainWindow? window=null;
                try
                {
                    window=new MainWindow(); const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    object? Call(string n, params object[] a)=>typeof(MainWindow).GetMethod(n,flags)!.Invoke(window,a);
                    void Set(string n,object v)=>typeof(MainWindow).GetField(n,flags)!.SetValue(window,v);
                    Set("_suppressWatchChange",true);
                    ((CheckBox)window.FindName("OnlyMyPcCheckBox")).IsChecked=false;
                    ((CheckBox)window.FindName("AutoStartCheckBox")).IsChecked=false;
                    Set("_blenderExe",Environment.ProcessPath!);
                    var root=Path.Combine(Path.GetTempPath(),"BRH-batch-ui-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                    var blend=Path.Combine(root,"test.blend"); File.WriteAllText(blend,"fixture");
                    var batchId=Guid.NewGuid().ToString();
                    var jobs=Enumerable.Range(0,3).Select(i=>new {jobId=Guid.NewGuid().ToString(),batchId,blendFile=blend,distributed=true,cameraName="Camera"+i,startFrame=7,endFrame=7}).ToList();
                    var json=JsonSerializer.Serialize(new{batchId,jobs});
                    Call("QueueIncomingJob",json,"Test",Path.Combine(root,"test.camerabatch.json"));
                    Call("QueueIncomingJob",json,"Test",Path.Combine(root,"test.camerabatch.json"));
                    var queue=(ObservableCollection<RenderJob>)typeof(MainWindow).GetField("_queue",flags)!.GetValue(window)!;
                    if(queue.Count!=3 || queue.Any(j=>j.BatchId!=batchId))throw new Exception("Batch intake/deduplication");
                    await ((Task)Call("RunCameraBatch",queue.ToList())!).WaitAsync(TimeSpan.FromSeconds(30));
                    if(queue.Any(j=>j.Status!="Complete" || !j.RenderedFiles.ContainsKey(7) || !File.ReadAllText(j.LogPath).Contains("Batch process exit: 0"))) throw new Exception("Batch protocol did not update per-camera status/logs/outputs");
                    Console.WriteLine("PASS batch launcher: envelope/deduplication, one process, per-camera status/output paths and logs.");
                }
                catch(Exception ex){error=ex;}
                finally{window?.Close();app.Shutdown();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background);}
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(error!=null)throw error;
    }
}
