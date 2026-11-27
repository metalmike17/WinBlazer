using Forms = System.Windows.Forms;

namespace PCOptimizer;

public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly List<(Forms.ToolStripItem Item, Func<string> Label)> _actions = new();

    public IReadOnlyList<string> MenuLabels =>
        _icon.ContextMenuStrip!.Items.OfType<Forms.ToolStripMenuItem>().Select(i => i.Text ?? "").ToList();

    public TrayIcon(Action open, Action exit, IEnumerable<(Func<string> Label, Action Run)>? quickActions = null)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open WinBlazer", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        foreach (var (label, run) in quickActions ?? Enumerable.Empty<(Func<string>, Action)>())
            _actions.Add((menu.Items.Add(label(), null, (_, _) => run()), label));
        if (_actions.Count > 0) menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        // Labels such as Game Mode on/off are refreshed each time the menu opens.
        menu.Opening += (_, _) => { foreach (var (item, label) in _actions) item.Text = label(); };
        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon(), Text = "WinBlazer",
            ContextMenuStrip = menu, Visible = true
        };
        _icon.DoubleClick += (_, _) => open();
    }

    /// <summary>The icon built into the program, or Windows' plain one if it cannot be read.</summary>
    private static System.Drawing.Icon AppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon)
                return icon;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException) { }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>Windows shows this as a notification toast, unless notifications are turned off or silenced.</summary>
    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message.Length > 250 ? message[..247] + "..." : message,
                             Forms.ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
