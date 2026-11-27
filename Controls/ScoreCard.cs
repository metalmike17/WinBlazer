using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCOptimizer.Core;
using PCOptimizer.Services;

namespace PCOptimizer.Controls;

/// <summary>The dashboard's optimization score with fixes for the lowest areas.</summary>
public sealed class ScoreCard : UserControl
{
    private readonly AppContextServices _services;
    private readonly TextBlock _number = Ui.Text("--", 46, weight: FontWeights.Bold);
    private readonly TextBlock _word = Ui.Text("Calculating...", 12, "MutedBrush");
    private readonly TextBlock _status = Ui.Text("", 12, "AccentBrush");
    private readonly StackPanel _areas = new();
    private readonly Button _again = new() { Content = "Recalculate", Margin = new Thickness(0, 8, 0, 0) };
    private bool _busy;

    public ScoreResult? Result { get; private set; }

    public ScoreCard(AppContextServices services)
    {
        _services = services;
        _again.Click += async (_, _) => await RefreshAsync();
        var left = Ui.Stack(Ui.Text("OPTIMIZATION SCORE", 11, "MutedBrush", FontWeights.SemiBold), _number, _word, _again);
        left.Width = 150;
        left.Margin = new Thickness(0, 0, 28, 0);   // keeps Recalculate clear of the score details
        var right = Ui.Stack(_areas, _status, Ui.Text(
            "A housekeeping checklist made by this app, not a speed benchmark. A higher score does not promise a " +
            "faster PC.", 10, "MutedBrush"));
        var dock = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        dock.Children.Add(left);
        dock.Children.Add(right);
        var card = Ui.Card(dock);
        card.Margin = new Thickness(0, 10, 12, 2);
        Content = card;
        var started = false;
        Loaded += async (_, _) => { if (!started) { started = true; await RefreshAsync(); } };
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _again.IsEnabled = _areas.IsEnabled = false;
        _word.Text = "Calculating...";
        try
        {
            var admin = CleanupEngine.IsAdministrator();
            var inputs = await Task.Run(() => OptimizationScore.Collect(_services.Settings, admin));
            Show(OptimizationScore.Calculate(inputs));
        }
        catch (Exception ex) { _word.Text = "Not available"; _status.Text = ex.Message; }
        finally { _busy = false; _again.IsEnabled = _areas.IsEnabled = true; }
    }

    private static Brush Color_(double fraction) =>
        Ui.Brush(fraction >= 0.8 ? "GoodBrush" : fraction >= 0.5 ? "WarnBrush" : "BadBrush");

    private void Show(ScoreResult result)
    {
        Result = result;
        Motion.Count(_number, result.Score, v => $"{v:0}", "--");
        _number.Foreground = Color_((result.Score ?? 0) / 100.0);
        _word.Text = result.Score switch
        { null => "Not available", >= 90 => "out of 100: very tidy", >= 70 => "out of 100: good", _ => "out of 100: room to tidy up" };
        _areas.Children.Clear();
        foreach (var area in result.Areas)
        {
            var title = Ui.Text($"{area.Title}: {(area.Percent is { } p ? p + "%" : "not scored")}", 12,
                                weight: FontWeights.SemiBold);
            if (area.Fraction is { } f) title.Foreground = Color_(f);
            var row = Ui.Stack(title, Ui.Text(area.Detail, 10, "MutedBrush"));
            row.Margin = new Thickness(0, 1, 0, 3);
            if (area.Fraction < 1)
            {
                var fix = new Button
                {
                    Content = area.FixLabel, FontSize = 11, Padding = new Thickness(10, 3, 10, 3),
                    HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 120, Margin = new Thickness(0, 0, 0, 8),
                    ToolTip = area.FixAction != null ? "Shows what will change and asks first" : "Opens the page so you can decide"
                };
                fix.Click += async (_, _) => await FixAsync(area);
                row.Children.Add(fix);   // directly under the item it fixes
            }
            _areas.Children.Add(row);
        }
    }

    private async Task FixAsync(ScoreArea area)
    {
        if (_busy || _services.Shell is not { } shell) return;
        if (area.FixAction == null) { shell.Navigate(area.FixModule, area.FixSection); return; }
        _status.Text = "Working...";
        _areas.IsEnabled = false;
        var message = await shell.RunQuickActionAsync(area.FixAction);
        _areas.IsEnabled = true;
        await RefreshAsync();
        _status.Text = message;
    }
}
