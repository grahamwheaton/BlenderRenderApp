using BlenderRenderHeadless;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

static class RecoveryTests
{
    public static void Run()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var app = new App(); app.InitializeComponent();
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    object? Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, flags)!.Invoke(window, values);
                    void Field(string name, object value) => typeof(MainWindow).GetField(name, flags)!.SetValue(window, value);
                    void Assert(bool condition, string text) { if (!condition) throw new Exception(text); }
                    var folder = Path.Combine(Path.GetTempPath(), "BRH-recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                    var blend = Path.Combine(folder,"fixture.blend"); File.WriteAllText(blend,"fixture");
                    var job = new RenderJob { BlendFile=blend, StartFrame="1",EndFrame="4",Status="Failed" };
                    Field("_activeRenderJob", job); Call("BeginAttemptLog",job);
                    var code = await (Task<int>)Call("RunStreamingAsync", Environment.ProcessPath!, new[]{"--fake-failure"},job,false)!;
                    Call("AppendLog", "Blender exit code: " + code); Call("EndAttemptLog",job);
                    var log = File.ReadAllText(job.LogPath);
                    Assert(code==7 && log.Contains("TEST missing texture diagnostic") && log.Contains("worker started") && log.Contains("exit code: 7"),"persisted stdout/stderr/exit code");
                    Call("RetryJob_Click",new Button {Tag=job},new RoutedEventArgs(Button.ClickEvent));
                    Assert(job.Status=="Waiting","retry requeued");
                    Field("_activeRenderJob",null!); Field("_cloudQueueFolder",folder); Field("_suppressWatchChange",true);
                    ((CheckBox)window.FindName("OnlyMyPcCheckBox")).IsChecked=false;
                    ((CheckBox)window.FindName("AutoStartCheckBox")).IsChecked=false;
                    var queue=(ObservableCollection<RenderJob>)typeof(MainWindow).GetField("_queue",flags)!.GetValue(window)!;
                    var dismissed=(HashSet<string>)typeof(MainWindow).GetField("_dismissedJobIds",flags)!.GetValue(window)!;
                    foreach(var state in new[]{"active","complete","cancel"})
                    {
                        var id=Guid.NewGuid().ToString(); dismissed.Add(id);
                        File.WriteAllText(Path.Combine(folder,state+".renderjob.json"),JsonSerializer.Serialize(new {jobId=id,blendFile=blend,distributed=true,senderMachine=Environment.MachineName}));
                        var claims=Path.Combine(folder,"_claims",id); Directory.CreateDirectory(claims);
                        if(state!="active") File.WriteAllText(Path.Combine(claims,state+".json"),"{}");
                    }
                    Call("ResetWatchDiscovery"); Assert(queue.Count==1 && dismissed.Count==0,"only active dismissed job restored");
                    Call("ResetWatchDiscovery"); Assert(queue.Count==1,"no duplicate restoration");
                    Console.WriteLine("PASS recovery: actual failed process log, retry, active-only watch reset, no duplicate jobs. Log: "+job.LogPath);
                }
                catch(Exception ex) { error=ex; }
                finally { window?.Close(); app.Shutdown(); System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            }));
            System.Windows.Threading.Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if(error!=null) throw error;
    }
}
