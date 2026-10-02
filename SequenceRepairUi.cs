using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlenderRenderHeadless;

public partial class MainWindow
{
    private async void PartialDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: RenderJob job }) return;
        try
        {
            using var lease = SequenceRepair.Lock(job);
            var frames = await Task.Run(() => SequenceRepair.Read(job));
            var panel = new StackPanel { Margin = new Thickness(18) };
            var dialog = new Window { Title = "Partial Delete — select machines", Width = 520, Height = 430, Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(28,34,41)), Foreground = Brushes.White };
            panel.Children.Add(new TextBlock { Text = "Choose machines whose frames should be removed.\nFiles move to _partial-delete beside the sequence.\nExisting MP4/PSD exports are kept unchanged.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,16) });
            var list = new StackPanel(); var checks = new List<CheckBox>();
            foreach (var group in frames.Where(f => File.Exists(f.Output)).GroupBy(f => f.Machine, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key))
            {
                var check = new CheckBox { Content = $"{(string.IsNullOrWhiteSpace(group.Key) ? "Unknown machine" : group.Key)} — {group.Count()} frames", Tag = group.Key, Margin = new Thickness(0,6,0,6) };
                checks.Add(check); list.Children.Add(check);
            }
            panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 210, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var delete = new Button { Content = "Delete selected frames", Margin = new Thickness(0,18,0,0), Padding = new Thickness(12,8,12,8) };
            delete.Click += (_, _) => { if (checks.Any(c => c.IsChecked == true)) dialog.DialogResult = true; };
            panel.Children.Add(delete); dialog.Content = panel;
            if (dialog.ShowDialog() != true) return;
            var machines = checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selected = frames.Where(f => machines.Contains(f.Machine) && File.Exists(f.Output)).ToList();
            if (MessageBox.Show(this, $"Remove {selected.Count} frames from:\n{SequenceRepair.OutputDirectory(job)}\n\nThey can be recovered from _partial-delete. Then use Check and rerun to replace them.", "Confirm Partial Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var count = await Task.Run(() => SequenceRepair.Quarantine(job, selected));
            job.ExportStatus = $"{count} frames moved to _partial-delete. Use Check and rerun.";
            AppendLog(job.ExportStatus);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Partial Delete", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void CheckAndRerun_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: RenderJob job }) return;
        try
        {
            using var lease = SequenceRepair.Lock(job);
            var frames = await Task.Run(() => SequenceRepair.Read(job));
            var missing = frames.Count(f => !SequenceRepair.Valid(f));
            if (missing == 0) { MessageBox.Show(this, "All expected frames exist and are non-empty. Nothing to rerun.", "Check and rerun"); return; }
            if (MessageBox.Show(this, $"{missing} missing or empty frames. Publish a repair job to watching machines?\n\nGood frames are preserved. Watch filters and Auto start settings still apply. Existing MP4/PSD files will not be replaced.", "Check and rerun", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var path = await Task.Run(() => SequenceRepair.Publish(job, frames));
            QueueIncomingJob(File.ReadAllText(path), "Repair", path);
            job.ExportStatus = $"Repair published for {missing} frames. Use the new job for further repairs.";
            AppendLog(job.ExportStatus);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Check and rerun", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
