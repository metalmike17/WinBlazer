using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PCOptimizer.Controls;
using PCOptimizer.Services;

namespace PCOptimizer;

/// <summary>The Ctrl+K search box shown over the window.</summary>
public sealed class SearchOverlay : Grid
{
    private readonly TextBox _query = new() { FontSize = 16, Padding = new Thickness(10, 8, 10, 8) };
    private readonly ListBox _results = new() { MaxHeight = 360, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _hint = Ui.Text("", 11, "MutedBrush");
    private readonly Func<IReadOnlyList<SearchEntry>> _entries;
    private readonly Action<SearchEntry> _open;
    private IInputElement? _previousFocus;

    public bool IsOpen => Visibility == Visibility.Visible;
    public SearchEntry? Selected => (_results.SelectedItem as ListBoxItem)?.Tag as SearchEntry;
    public int ResultCount => _results.Items.Count;
    public string Query { get => _query.Text; set => _query.Text = value; }

    public SearchOverlay(Func<IReadOnlyList<SearchEntry>> entries, Action<SearchEntry> open)
    {
        _entries = entries;
        _open = open;
        Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(_query, "Search WinBlazer");
        _results.Foreground = Ui.Brush("TextBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(_results, ScrollBarVisibility.Disabled);
        var panel = new Border
        {
            Width = 560, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 70, 0, 0),
            Background = Ui.Brush("PanelBrush"), CornerRadius = new CornerRadius(14),
            BorderBrush = Ui.Brush("AccentBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(12),
            Child = Ui.Stack(_query, _results, _hint)
        };
        Children.Add(panel);
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == this) Close(); };
        _query.TextChanged += (_, _) => Update();
        // Arrow keys move through results while typing stays in the box.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter) Choose();
            else if (e.Key is Key.Down or Key.Up && _results.Items.Count > 0)
            {
                _results.SelectedIndex = Math.Clamp(_results.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0,
                                                    _results.Items.Count - 1);
                _results.ScrollIntoView(_results.SelectedItem);
            }
            else return;
            e.Handled = true;
        };
        _results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_results, (DependencyObject)e.OriginalSource) is ListBoxItem item)
            { _results.SelectedItem = item; Choose(); }
        };
    }

    public void Open()
    {
        if (!IsOpen) _previousFocus = Keyboard.FocusedElement;
        Visibility = Visibility.Visible;
        _query.Text = "";
        Update();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _query.Focus());
    }

    public void Close()
    {
        Visibility = Visibility.Collapsed;
        _previousFocus?.Focus();
    }

    private void Update()
    {
        _results.Items.Clear();
        foreach (var entry in SearchIndex.Find(_entries(), _query.Text))
            _results.Items.Add(new ListBoxItem
            {
                Tag = entry, Padding = new Thickness(8, 4, 8, 4), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Foreground = Brushes.Black,
                Content = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Inlines =
                    {
                        new System.Windows.Documents.Run(entry.Title) { FontWeight = FontWeights.SemiBold },
                        new System.Windows.Documents.Run("   " + entry.Subtitle) { FontSize = 11 }
                    }
                }
            });
        // A light list keeps the standard selection highlight readable.
        _results.Background = _results.Items.Count > 0 ? Brushes.WhiteSmoke : Brushes.Transparent;
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
        _hint.Text = _results.Items.Count == 0 ? "No matches." : "↑↓ to move, Enter to open, Esc to close";
    }

    public void Choose()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: SearchEntry entry }) return;
        Visibility = Visibility.Collapsed;
        _open(entry);
    }
}

/// <summary>The F1 list of keyboard shortcuts.</summary>
public sealed class ShortcutHelp : Grid
{
    private readonly StackPanel _rows = new();

    public bool IsOpen => Visibility == Visibility.Visible;

    public ShortcutHelp()
    {
        Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        Visibility = Visibility.Collapsed;
        var panel = new Border
        {
            Width = 520, MaxHeight = 560, VerticalAlignment = VerticalAlignment.Center,
            Background = Ui.Brush("PanelBrush"), CornerRadius = new CornerRadius(14),
            BorderBrush = Ui.Brush("AccentBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(18),
            Child = Ui.Scroll(Ui.Stack(Ui.Text("Keyboard shortcuts", 18, weight: FontWeights.SemiBold), _rows,
                Ui.Text("Shortcuts press the same buttons you can click, so confirmations still appear. " +
                        "Press Esc or click outside to close.", 11, "MutedBrush")))
        };
        Children.Add(panel);
        MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == this) Close(); };
    }

    public void Open(IEnumerable<(string Keys, string Description)> rows)
    {
        _rows.Children.Clear();
        foreach (var (keys, description) in rows)
        {
            var row = new DockPanel();
            var key = Ui.Text(keys, 12, "AccentBrush", FontWeights.SemiBold);
            key.Width = 170;
            row.Children.Add(key);
            row.Children.Add(Ui.Text(description, 12));
            _rows.Children.Add(row);
        }
        Visibility = Visibility.Visible;
    }

    public void Close() => Visibility = Visibility.Collapsed;
}

/// <summary>A notification in the bottom-right corner of the window.</summary>
public sealed class ToastHost : Border
{
    private readonly TextBlock _title = Ui.Text("", 14, weight: FontWeights.SemiBold);
    private readonly TextBlock _message = Ui.Text("", 12);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(7) };

    public string Text => IsShowing ? _title.Text + ": " + _message.Text : "";
    public bool IsShowing => Visibility == Visibility.Visible;

    public ToastHost()
    {
        Width = 330;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 24, 24);
        Padding = new Thickness(14, 10, 14, 10);
        CornerRadius = new CornerRadius(12);
        BorderThickness = new Thickness(1);
        BorderBrush = Ui.Brush("AccentBrush");
        Background = Ui.Brush("PanelBrush");
        Visibility = Visibility.Collapsed;
        Cursor = Cursors.Hand;
        ToolTip = "Click to dismiss";
        Child = Ui.Stack(_title, _message);
        MouseLeftButtonDown += (_, _) => Hide();
        _timer.Tick += (_, _) => Hide();
    }

    public void Show(string title, string message)
    {
        _title.Text = title;
        _message.Text = message;
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
        _timer.Stop();
        _timer.Start();
    }

    private void Hide()
    {
        _timer.Stop();
        Visibility = Visibility.Collapsed;
    }
}
