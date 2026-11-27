using System.Windows;
using System.Windows.Controls;
using PCOptimizer.Controls;
using PCOptimizer.Services;

namespace PCOptimizer.Modules;

/// <summary>Read-only: this page never changes or deletes files.</summary>
public sealed class DiskAnalyzerView : UserControl
{
    private readonly ComboBox _path = new() { IsEditable = true, Width = 360, Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _scan = new() { Content = "Analyze" };
    private readonly Button _cancel = new() { Content = "Cancel", IsEnabled = false };
    private readonly Button _up = new() { Content = "Up one level", IsEnabled = false };
    private readonly TextBlock _status = Ui.Text("Choose a drive or type a folder path, then press Analyze.", 12, "MutedBrush");
    private readonly TextBlock _crumb = Ui.Text("", 13, "AccentBrush");
    private readonly ListBox _list = new();
    private CancellationTokenSource? _cancellation;

    public Treemap Map { get; } = new();
    public int ListCount => _list.Items.Count;

    private async Task LoadDriveListAsync()
    {
        try
        {
            var roots = await Task.Run(() => DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
                .Select(d => d.RootDirectory.FullName).ToList());
            roots.ForEach(root => _path.Items.Add(root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _status.Text = ex.Message; }
    }

    private sealed record Row(DiskNode Node)
    {
        public override string ToString() =>
            $"{CleanupEngine.Size(Node.Size),10}  {(Node.IsFolder ? "[folder] " : "")}{Node.Name}";
    }

    public DiskAnalyzerView()
    {
        _path.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _ = LoadDriveListAsync();

        Controls.Ui.AlignWithButtons(_path);
        _scan.Role(ButtonRole.Refresh);
        _scan.Click += async (_, _) => await AnalyzeAsync(_path.Text);
        _cancel.Role(ButtonRole.Cancel);
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        _up.Click += (_, _) => { if (Map.Node?.Parent is { } parent) ShowNode(parent); };
        Map.FolderClicked += ShowNode;
        _list.Background = Ui.Brush("GlassBrush");
        _list.Foreground = Ui.Brush("TextBrush");
        _list.BorderBrush = Ui.Brush("EdgeBrush");
        _list.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        _list.MouseDoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) OpenSelected(); };

        var controls = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var c in new UIElement[] { _path, _scan, _cancel, _up }) controls.Children.Add(c);
        var top = Ui.Stack(controls, _status, _crumb);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Map.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_list, 1);
        body.Children.Add(Map);
        body.Children.Add(_list);

        var dock = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(body);
        Content = dock;
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is Row { Node.IsFolder: true } row) ShowNode(row.Node);
    }

    private void ShowNode(DiskNode node)
    {
        Map.Node = node;
        _crumb.Text = $"{node.Path}  ({CleanupEngine.Size(node.Size)})";
        _up.IsEnabled = node.Parent != null;
        _list.ItemsSource = node.Children.Select(c => new Row(c)).ToList();
    }

    public async Task AnalyzeAsync(string path)
    {
        if (_cancellation != null) return;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            _status.Text = @"Enter a full path, such as C:\ or D:\Games.";
            return;
        }
        var source = _cancellation = new CancellationTokenSource();
        _scan.IsEnabled = false;
        _cancel.IsEnabled = true;
        _status.Text = "Analyzing... large drives can take several minutes.";
        var progress = new Progress<long>(n => { if (_cancellation == source) _status.Text = $"Analyzing... {n:N0} files"; });
        try
        {
            var result = await Task.Run(() => DiskTree.Scan(path, progress, source.Token));
            _cancellation = null;
            ShowNode(result.Root);
            _status.Text = $"{result.Files:N0} files. {result.UnreadableFolders:N0} folders could not be read, " +
                           $"{result.SkippedLinks:N0} links or cloud placeholders skipped. Sizes are file sizes, " +
                           "not exact space on disk. Click a folder to drill down.";
        }
        catch (OperationCanceledException) { _status.Text = "Analysis cancelled."; }
        catch (Exception ex) { _status.Text = "Analysis failed: " + ex.Message; }
        finally
        {
            _cancellation = null;
            source.Dispose();
            _scan.IsEnabled = true;
            _cancel.IsEnabled = false;
        }
    }
}
