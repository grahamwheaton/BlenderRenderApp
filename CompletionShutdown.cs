using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BlenderRenderHeadless;

public sealed class CompletionShutdownCountdown
{
    public DateTime? Deadline { get; private set; }
    public void Reset() => Deadline = null;
    public bool Tick(DateTime now, bool enabled, bool completedAndIdle)
    {
        if (!enabled || !completedAndIdle) { Reset(); return false; }
        Deadline ??= now.AddMinutes(30);
        return now >= Deadline;
    }
}

public partial class MainWindow
{
    private readonly CompletionShutdownCountdown _shutdownCountdown = new();
    private readonly DispatcherTimer _shutdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CheckBox _shutdownEnabled = new() { Content = "Shutdown on completion", Margin = new Thickness(0, 0, 12, 8), ToolTip = "Shut down this PC after 30 minutes with a completed, idle queue. Off on every launch. Unsaved applications may block shutdown." };
    private readonly TextBlock _shutdownMessage = new() { Foreground = Brushes.Orange, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancelShutdown = new() { Content = "Cancel shutdown", Margin = new Thickness(8, 0, 8, 4), Visibility = Visibility.Collapsed };

    private void InitializeCompletionShutdown()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Power", FontSize = 10, Foreground = Brushes.LightGray, Margin = new Thickness(0, 0, 0, 7) });
        panel.Children.Add(_shutdownEnabled);
        var countdown = new StackPanel { Orientation = Orientation.Horizontal };
        countdown.Children.Add(_shutdownMessage); countdown.Children.Add(_cancelShutdown); panel.Children.Add(countdown);
        var group = new Border { Child = panel, Background = new SolidColorBrush(Color.FromRgb(27, 34, 42)), BorderBrush = new SolidColorBrush(Color.FromRgb(52, 60, 70)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 7, 0, 0), Margin = new Thickness(0, 0, 10, 6) };
        var exportGroup = (Border)((StackPanel)((WrapPanel)AutoMp4CheckBox.Parent).Parent).Parent;
        ((Panel)exportGroup.Parent).Children.Add(group);
        _shutdownEnabled.Unchecked += (_, _) => ResetShutdownCountdown();
        _cancelShutdown.Click += (_, _) => _shutdownEnabled.IsChecked = false;
        _queue.CollectionChanged += (_, _) => ResetShutdownCountdown();
        _shutdownTimer.Tick += (_, _) => CheckCompletionShutdown();
        _shutdownTimer.Start();
        Closed += (_, _) => { _shutdownTimer.Stop(); ResetShutdownCountdown(); };
    }

    private void ResetShutdownCountdown()
    {
        _shutdownCountdown.Reset(); _shutdownMessage.Text = ""; _cancelShutdown.Visibility = Visibility.Collapsed;
    }

    private void CheckCompletionShutdown()
    {
        var jobs = _queue.Where(j => !j.IsIgnored).ToArray();
        var idle = jobs.Length > 0 && jobs.All(j => j.Status == "Complete" && !j.IsOpening && !j.IsBatchReserved)
            && !_queueRunning && !_loadingBlend && !_watchCleanupRunning && _exportSerial.CurrentCount > 0
            && OwnedWindows.Count == 0;
        var due = _shutdownCountdown.Tick(DateTime.UtcNow, _shutdownEnabled.IsChecked == true, idle);
        if (_shutdownCountdown.Deadline is not { } deadline) { ResetShutdownCountdown(); return; }
        _shutdownMessage.Text = $"Shutdown in {Math.Max(0, (deadline - DateTime.UtcNow).TotalMinutes):00}:{Math.Max(0, (deadline - DateTime.UtcNow).Seconds):00}";
        _cancelShutdown.Visibility = Visibility.Visible;
        if (!due) return;
        // No OS shutdown is scheduled during the countdown; cancellation cannot
        // accidentally abort a shutdown requested by someone else. Never /f.
        _shutdownEnabled.IsChecked = false;
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
            StatusText.Text = "Shutdown requested. Unsaved applications may block it.";
        }
        catch (Exception ex) { StatusText.Text = "Could not shut down: " + ex.Message; }
    }
}
