using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BlenderRenderHeadless;

public sealed record WorkerTuning
{
    public int PolicyVersion { get; init; } = 2;
    public int MaxChildren { get; init; } = 2;
    public double WarmupSeconds { get; init; } = 10;
    public double CheckSeconds { get; init; } = 3;
    public int HealthyChecks { get; init; } = 2;
    public double TrialSeconds { get; init; } = 20;
    public double MinimumGainPercent { get; init; } = 10;
    public double StartupMultiplier { get; init; } = 2;
    public double MinimumRemainingSeconds { get; init; } = 15;
    public double MaxCpuPercent { get; init; } = 95;
    public double MaxGpuPercent { get; init; } = 100;
    public double MinimumRamGb { get; init; } = 2;
    public double MinimumVramGb { get; init; } = 1;
    public double MemoryReservePercent { get; init; } = 5;
    public WorkerTuning UpgradeLegacy() => this with
    {
        PolicyVersion = 2,
        MaxCpuPercent = MaxCpuPercent == 60 ? 95 : MaxCpuPercent,
        MaxGpuPercent = MaxGpuPercent == 65 ? 100 : MaxGpuPercent,
        MinimumRamGb = MinimumRamGb == 8 ? 2 : MinimumRamGb,
        MinimumVramGb = MinimumVramGb == 4 ? 1 : MinimumVramGb
    };
    public string? Validate()
    {
        bool In(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
        if (MaxChildren is < 0 or > 8) return "Max children must be 0–8 (in addition to the primary worker).";
        if (!In(WarmupSeconds, 3, 300) || !In(CheckSeconds, 1, 60)) return "Warm-up must be 3–300 seconds; resource checks 1–60 seconds.";
        if (HealthyChecks is < 1 or > 10) return "Healthy checks must be 1–10.";
        if (!In(TrialSeconds, 5, 300) || !In(MinimumGainPercent, 0, 100)) return "Trial must be 5–300 seconds; minimum gain 0–100%.";
        if (!In(StartupMultiplier, 1, 10) || !In(MinimumRemainingSeconds, 5, 600)) return "Startup allowance must be 1–10×; minimum remaining time 5–600 seconds.";
        if (!In(MaxCpuPercent, 10, 100) || !In(MaxGpuPercent, 10, 100)) return "CPU/GPU limits must be 10–100%.";
        if (!In(MinimumRamGb, 2, 1024) || !In(MinimumVramGb, 0.5, 256) || !In(MemoryReservePercent, 2, 30)) return "RAM reserve must be 2–1024 GB; VRAM reserve 0.5–256 GB; reserve percentage 2–30%.";
        return null;
    }
}

public sealed class WorkerSettingsWindow : Window
{
    public WorkerTuning Settings { get; private set; }
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
    public WorkerSettingsWindow(WorkerTuning settings)
    {
        Settings = settings; Title = "Settings — child workers"; Width = 650; Height = 730; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(27, 33, 40));
        var root = new DockPanel { Margin = new Thickness(22) }; Content = root;
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        bottom.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bottom.Children.Add(buttons);
        var reset = new Button { Content = "Restore defaults", Margin = new Thickness(0, 0, 10, 0) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 10, 0), IsCancel = true };
        var save = new Button { Content = "Save", IsDefault = true };
        buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(save);
        var panel = new StackPanel(); root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "Applies to the next queued job, not workers already running.\nAllow Children is turbo mode for shared playblasts and Eevee sequences. NVIDIA telemetry is required. Memory reserves scale to each PC.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        void Section(string name) => panel.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 10), Foreground = new SolidColorBrush(Color.FromRgb(245, 124, 35)) });
        void Field(string key, string label, string help)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 12, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            var text = new StackPanel(); text.Children.Add(new TextBlock { Text = label });
            text.Children.Add(new TextBlock { Text = help, FontSize = 11, Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 12, 0) }); row.Children.Add(text);
            var input = new TextBox { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(input, 1); row.Children.Add(input); _fields.Add(key, input); panel.Children.Add(row);
        }
        Section("WORKER LIMITS");
        Field(nameof(WorkerTuning.MaxChildren), "Max children", "Extra Blender instances, excluding the primary. 0 disables children; up to 8. Added one at a time.");
        Section("TIMING");
        Field(nameof(WorkerTuning.WarmupSeconds), "Warm-up time (seconds)", "Measure throughput after the first completed frame.");
        Field(nameof(WorkerTuning.CheckSeconds), "Resource check time (seconds)", "Interval between CPU, GPU and memory checks.");
        Field(nameof(WorkerTuning.HealthyChecks), "Consecutive healthy checks", "Required before starting each additional child.");
        Field(nameof(WorkerTuning.StartupMultiplier), "Startup allowance (×)", "Remaining work must exceed this multiple of measured Blender startup time, including its first frame.");
        Field(nameof(WorkerTuning.MinimumRemainingSeconds), "Minimum remaining time (seconds)", "Also require this much estimated work before adding a child.");
        Section("RESOURCE LIMITS");
        Field(nameof(WorkerTuning.MaxCpuPercent), "Maximum CPU usage (%)", "Do not add a child above this system-wide usage.");
        Field(nameof(WorkerTuning.MaxGpuPercent), "Maximum GPU usage (%)", "Do not add a child above this GPU usage.");
        Field(nameof(WorkerTuning.MinimumRamGb), "RAM safety reserve (GB)", "Keep this or the hardware-scaled percentage free, whichever is larger, after estimated child memory.");
        Field(nameof(WorkerTuning.MinimumVramGb), "VRAM safety reserve (GB)", "Uses measured render-worker memory growth, not all VRAM currently used. No half-capacity ceiling.");
        Field(nameof(WorkerTuning.MemoryReservePercent), "Hardware memory reserve (%)", "Percentage of total RAM and VRAM kept free. Default 5% adapts to each machine.");
        Section("PERFORMANCE CHECKS");
        Field(nameof(WorkerTuning.TrialSeconds), "Trial duration (seconds)", "Measure after the new child finishes its first frame, before considering another child.");
        Field(nameof(WorkerTuning.MinimumGainPercent), "Minimum throughput gain (%)", "Retire the trial child between frames if aggregate local speed improves by less than this.");
        void Populate(WorkerTuning value) { foreach (var pair in _fields) pair.Value.Text = Convert.ToString(typeof(WorkerTuning).GetProperty(pair.Key)!.GetValue(value), CultureInfo.InvariantCulture)!; }
        Populate(settings); reset.Click += (_, _) => { Populate(new()); _error.Text = "Defaults restored; click Save to apply."; };
        save.Click += (_, _) =>
        {
            try
            {
                var result = new WorkerTuning();
                foreach (var pair in _fields)
                {
                    var property = typeof(WorkerTuning).GetProperty(pair.Key)!;
                    object value = property.PropertyType == typeof(int) ? (object)int.Parse(pair.Value.Text, CultureInfo.InvariantCulture) : double.Parse(pair.Value.Text, CultureInfo.InvariantCulture);
                    property.SetValue(result, value);
                }
                if (result.Validate() is { } error) { _error.Text = error; return; }
                Settings = result; DialogResult = true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { _error.Text = "Enter valid numbers (whole numbers for children and check counts)."; }
        };
    }
}
