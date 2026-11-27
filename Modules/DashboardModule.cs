using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer.Modules;

public sealed class DashboardModule : IModule
{
    public string Id => "dashboard";
    public string Title => "Dashboard";
    public string Icon => "";
    public string Description => "Live system usage";
    public int Order => 0;
    public bool CanHide => false;
    public FrameworkElement CreateView(AppContextServices services) => new DashboardView(services);
}

public sealed class DashboardView : UserControl
{
    private sealed record Tile(TextBlock Value, TextBlock Detail, Sparkline Chart);

    private readonly Tile _cpu, _ram, _gpu, _disk, _net;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MetricsService? _metrics;
    private bool _sampling;

    public int SampleCount { get; private set; }
    public Metrics? Latest { get; private set; }

    public ScoreCard Score { get; }

    public DashboardView(AppContextServices services)
    {
        Score = new ScoreCard(services);
        var grid = new UniformGrid { Columns = 3,VerticalAlignment = VerticalAlignment.Top };
        _cpu = AddTile(grid, "CPU", "#00D4FF", 100);
        _ram = AddTile(grid, "MEMORY", "#7C3AED", 100);
        _gpu = AddTile(grid, "GPU", "#00D4FF", 100);
        _disk = AddTile(grid, "DISK ACTIVITY", "#7C3AED", 100);
        _net = AddTile(grid, "NETWORK", "#00D4FF", 0);

        var page = Ui.Page("Dashboard", "Live usage, updated about once a second");
        page.Children.Add(Score);
        page.Children.Add(grid);
        Content = Ui.Scroll(page);

        _timer.Tick += async (_, _) => await TickAsync();
        // Sample only while the page is on screen.
        IsVisibleChanged += (_, _) => { if (IsVisible) _timer.Start(); else _timer.Stop(); };
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => { if (IsVisible) _timer.Start(); };
    }

    private static Tile AddTile(Panel parent, string name, string color, double maximum)
    {
        var value = Ui.Text("--", 28, weight: FontWeights.SemiBold);
        var detail = Ui.Text(" ", 12, "MutedBrush");
        var chart = new Sparkline
        {
            Height = 58, Maximum = maximum, Stroke = (Color)ColorConverter.ConvertFromString(color)
        };
        var card = Ui.Card(Ui.Stack(Ui.Text(name, 12, "MutedBrush", FontWeights.SemiBold), value, detail, chart));
        card.Margin = new Thickness(0, 10, 12, 2);
        parent.Children.Add(card);
        return new Tile(value, detail, chart);
    }

    private async Task TickAsync()
    {
        if (_sampling) return;   // never run two samples at once
        _sampling = true;
        try
        {
            var m = await Task.Run(() => (_metrics ??= new MetricsService()).Sample());
            Latest = m;
            SampleCount++;
            Show(_cpu, m.Cpu, Percent, "All processors");
            Show(_ram, m.Ram, Percent, m.RamDetail);
            Show(_gpu, m.Gpu, Percent, m.GpuDetail);
            Show(_disk, m.Disk, Percent, "All physical disks");
            Show(_net, m.NetworkBytesPerSecond, MetricsService.Rate, m.NetworkDetail);        }
        catch (Exception ex) { _cpu.Detail.Text = "Could not read metrics: " + ex.Message; }
        finally { _sampling = false; }
    }

    private static string Percent(double v) => $"{v:N0}%";

    private static void Show(Tile tile, double? value, Func<double, string> format, string detail)
    {
        Motion.Count(tile.Value, value, format);
        tile.Detail.Text = detail;
        tile.Chart.Add(value);
    }
}