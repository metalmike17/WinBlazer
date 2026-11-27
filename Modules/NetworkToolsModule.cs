using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCOptimizer.Controls;
using PCOptimizer.Core;
using PCOptimizer.Services;
using PCOptimizer.Services.Network;

namespace PCOptimizer.Modules;

public sealed class NetworkWork
{
    public bool Changing { get; set; }
}

public sealed class NetworkToolsModule : IBackgroundModule
{
    private readonly NetworkWork _work = new();

    public string Id => "network-tools";
    public string Title => "Network Tools";
    public string Icon => "";
    public string Description => "Ping, DNS, TCP/IP and speed tools";
    public int Order => 24;

    public IDisposable StartBackground(AppContextServices services)
    {
        void Block(object? sender, CancelEventArgs e)
        {
            if (!_work.Changing) return;
            e.Cancel = true;
            MessageBox.Show("A network setting is being changed. Wait before closing WinBlazer.",
                            "Network Tools", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        Window? window = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            window = Application.Current.MainWindow;
            if (window != null) window.Closing += Block;
        });
        return new Stopper(() => { if (window != null) window.Closing -= Block; });
    }

    public FrameworkElement CreateView(AppContextServices services) => new NetworkToolsView(services, _work);

    private sealed class Stopper(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

public sealed class NetworkToolsView : UserControl, ISectionHost
{
    public void ShowSection(string section)
    {
        if (_tabs.TryGetValue(section, out var tab)) tab.IsChecked = true;
    }

    private readonly bool _isAdmin = CleanupEngine.IsAdministrator();
    private readonly NetworkWork _work;
    private readonly NetworkChanges _changes;

    private readonly ComboBox _adapters = new() { MinWidth = 220, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _info = Ui.Text("", 12);
    private readonly ContentControl _page = new() { Focusable = false };
    private readonly TextBlock _status = Ui.Text("", 12, "AccentBrush");
    private List<AdapterInfo> _adapterList = new();

    private readonly TextBox _target = new() { Text = "1.1.1.1", Width = 220, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _pingButton = Make("Start ping");
    private readonly PingStats _stats = new();
    private readonly PingChart _chart = new() { Height = 200 };
    private readonly TextBlock _pingText = Ui.Text("Not running.", 13);
    private CancellationTokenSource? _pinging;

    private readonly StackPanel _dnsRows = new();
    private List<DnsScore> _scores = new();
    private readonly StackPanel _tcpRows = new();
    private readonly TextBlock _speedText = Ui.Text("Not tested yet.", 22, weight: FontWeights.SemiBold);
    private readonly Button _speedButton = Make("Start download test");
    private CancellationTokenSource? _speed, _dnsTest;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly Dictionary<string, RadioButton> _tabs = new();
    private bool _busy;

    public bool HasLoadedTcp { get; private set; }
    public PingStats Stats => _stats;
    public IReadOnlyList<DnsScore> Scores => _scores;
    public IReadOnlyList<AdapterInfo> Adapters => _adapterList;
    public string StatusText => _status.Text;
    public string SpeedText => _speedText.Text;
    public int TcpRowCount => _tcpRows.Children.Count;

    private static Button Make(string text) => new() { Content = text, Margin = new Thickness(0, 0, 8, 6) };

    private AdapterInfo? Adapter => _adapters.SelectedIndex >= 0 && _adapters.SelectedIndex < _adapterList.Count
        ? _adapterList[_adapters.SelectedIndex] : null;

    public NetworkToolsView(AppContextServices services, NetworkWork work)
    {
        _work = work;
        _changes = new NetworkChanges(
            Path.Combine(Path.GetDirectoryName(services.Settings.FilePath)!, "Network"), _isAdmin,
            protectedFolder: ProtectedFolder.ForThisPc());
        System.Windows.Automation.AutomationProperties.SetName(_adapters, "Network adapter");
        System.Windows.Automation.AutomationProperties.SetName(_target, "Server to ping");

        var refresh = Make("Refresh");
        refresh.Role(ButtonRole.Refresh);
        refresh.Click += async (_, _) => await LoadAdaptersAsync();
        var row = new WrapPanel();
        Ui.AlignWithButtons(_adapters, _target);
        row.Children.Add(_adapters);
        row.Children.Add(refresh);
        if (!_isAdmin)
        {
            var elevate = Make("Restart as administrator");
            elevate.Click += (_, _) => RestartElevated();
            row.Children.Add(elevate);
        }
        var header = Ui.Card(Ui.Stack(row, _info));
        header.Padding = new Thickness(14, 10, 14, 10);

        _pages["Ping"] = PingPage();
        _pages["DNS"] = DnsPage();
        _pages["TCP/IP"] = TcpPage();
        _pages["Speed"] = SpeedPage();
        var tabs = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var name in _pages.Keys)
        {
            var tab = new RadioButton
            { Content = name, GroupName = "network", Style = (Style)Application.Current.FindResource("PageTab"), Margin = new Thickness(0, 0, 8, 0) };
            tab.Checked += async (_, _) => await ShowAsync(name);
            _tabs[name] = tab;
            tabs.Children.Add(tab);
        }

        var top = Ui.Stack(Ui.Text("Network Tools", 26, weight: FontWeights.SemiBold), header, tabs, _status);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;

        _adapters.SelectionChanged += async (_, _) =>
        {
            ShowInfo();
            _tcpRows.Children.Clear();
            HasLoadedTcp = false;
            if (_tabs["TCP/IP"].IsChecked == true) await LoadTcpAsync();
        };
        _info.Text = "Reading network adapters...";
        _ = LoadAdaptersAsync();
        _tabs["Ping"].IsChecked = true;
        Unloaded += (_, _) => { _pinging?.Cancel(); _speed?.Cancel(); _dnsTest?.Cancel(); };
    }

    private async Task ShowAsync(string name)
    {
        _page.Content = _pages[name];
        if (!_busy) _status.Text = "";
        if (name == "TCP/IP" && !HasLoadedTcp) await LoadTcpAsync();
    }

    public async Task OpenTabAsync(string name)
    {
        _tabs[name].IsChecked = true;
        if (name == "TCP/IP") for (var i = 0; i < 300 && !HasLoadedTcp; i++) await Task.Delay(100);
    }

    private async Task LoadAdaptersAsync()
    {
        var selected = Adapter?.Id;
        try { _adapterList = await Task.Run(() => NetworkInfo.Read(NetworkChanges.ReadStaticDns)); }
        catch (Exception ex) { _status.Text = ex.Message; return; }
        _adapters.Items.Clear();
        foreach (var a in _adapterList) _adapters.Items.Add(new ComboBoxItem { Content = a.Name });
        if (_adapterList.Count > 0)
            _adapters.SelectedIndex = Math.Max(0, _adapterList.FindIndex(a => a.Id == selected));
        ShowInfo();
    }

    private void ShowInfo()
    {
        if (Adapter is not { } a) { _info.Text = "No connected adapter with a gateway was found."; return; }
        static string List(List<string> values) => values.Count == 0 ? "none" : string.Join(", ", values);
        _info.Text = $"Local IP: {List(a.IPv4)}     Gateway: {List(a.Gateways)}\n" +
                     $"DNS: {List(a.Dns)}  ({(a.DnsAutomaticV4 ? "IPv4 automatic" : "IPv4 set manually")})\n" +
                     $"IPv6: {List(a.IPv6.Take(2).ToList())}\n{a.Description}";
    }

    // ---- Ping ----

    private FrameworkElement PingPage()
    {
        _chart.Stats = _stats;
        _pingButton.Role(ButtonRole.Main);
        _pingButton.Click += async (_, _) =>
        {
            if (_pinging != null) _pinging.Cancel();
            else await PingAsync();
        };
        var row = new WrapPanel();
        row.Children.Add(_target);
        row.Children.Add(_pingButton);
        return Ui.Stack(row, _pingText, Ui.Card(_chart),
            Ui.Text("One ping per second; the last 2 minutes are shown. Jitter is the average change between " +
                    "back-to-back replies. Red bars are missed replies. Some servers ignore ping, so 100% loss " +
                    "does not by itself mean the connection is down.", 11, "MutedBrush"));
    }

    public async Task PingAsync(int? count = null)
    {
        var host = _target.Text.Trim();
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            _pingText.Text = "Enter a server name or IP address, without http:// or a path.";
            return;
        }
        using var stop = new CancellationTokenSource();
        _pinging = stop;
        _pingButton.Content = "Stop ping";
        _target.IsEnabled = false;
        _stats.Clear();
        try
        {
            // Resolve once, so DNS delays are not counted as lost pings.
            var address = IPAddress.TryParse(host, out var ip) ? ip
                : (await Dns.GetHostAddressesAsync(host, stop.Token)).First();
            using var ping = new Ping();
            for (var i = 0; !stop.IsCancellationRequested && (count == null || i < count); i++)
            {
                var watch = Stopwatch.StartNew();
                double? time = null;
                try
                {
                    var reply = await ping.SendPingAsync(address, 1500);
                    if (reply.Status == IPStatus.Success) time = reply.RoundtripTime;
                }
                catch (PingException) { }
                if (stop.IsCancellationRequested) break;
                _stats.Add(time);
                static string Ms(double? v) => v == null ? "-" : $"{v:0.#} ms";
                _pingText.Text = $"{host} ({address})\nLast {(time == null ? "no reply" : Ms(time))}   Average {Ms(_stats.Average)}   " +
                    $"Min {Ms(_stats.Minimum)}   Max {Ms(_stats.Maximum)}\nJitter {Ms(_stats.Jitter)}   " +
                    $"Packet loss {_stats.LossPercent:0.#}% of the last {_stats.Samples.Count}";
                _chart.InvalidateVisual();
                var wait = 1000 - (int)watch.ElapsedMilliseconds;
                if (wait > 0) await Task.Delay(wait, stop.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _pingText.Text = $"Could not ping {host}: {ex.Message}"; }
        finally
        {
            _pinging = null;
            _pingButton.Content = "Start ping";
            _target.IsEnabled = true;
        }
    }

    // ---- DNS ----

    private FrameworkElement DnsPage()
    {
        var test = Make("Run DNS speed test");
        test.Role(ButtonRole.Main);
        test.Click += async (_, _) => await TestDnsAsync(test);
        var restore = Make("Restore previous DNS");
        restore.Click += async (_, _) =>
        {
            string? saved = null;
            try { if (Adapter is { } adapter) saved = _changes.DescribeDnsBackup(adapter.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { saved = "The saved addresses could not be read: " + ex.Message; }
            await ChangeAsync("Restore the DNS settings saved before Network Tools first changed them?\n\n" +
                (saved ?? "No saved setting was found for this adapter.") +
                "\n\nCheck that you recognize these addresses.", a => _changes.RestoreDns(a));
        };
        var row = new WrapPanel();
        row.Children.Add(test);
        row.Children.Add(restore);
        RenderDns();
        return Ui.Stack(row, Ui.Text(
            "The test sends lookups for common public sites to each provider. It measures lookup response time, " +
            "not download speed, and results vary between runs. Switching can break router, work, school or VPN " +
            "names, and the provider you choose receives your lookups.", 11, "MutedBrush"), _dnsRows);
    }

    private void RenderDns()
    {
        _dnsRows.Children.Clear();
        var fastest = DnsBenchmark.Fastest(_scores.Where(s => s.Name != "Current DNS"));
        void Add(string name, string detail, DnsProvider? provider)
        {
            var score = _scores.FirstOrDefault(s => s.Name == name);
            var result = score == null ? "not tested" : score.MedianMs == null ? "no answers"
                : $"{score.MedianMs:0.#} ms median, {score.Answered}/{score.Sent} answered";
            var text = Ui.Stack(Ui.Text(name + (fastest?.Name == name ? "   ★ fastest in this test" : ""), 13,
                weight: FontWeights.SemiBold), Ui.Text(result, 12, "AccentBrush"), Ui.Text(detail, 11, "MutedBrush"));
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(text);
            if (provider != null)
            {
                var use = Make("Use");
                use.VerticalAlignment = VerticalAlignment.Center;
                use.IsEnabled = _isAdmin;
                use.ToolTip = _isAdmin ? $"Switch to {provider.Name}" : "Needs administrator rights";
                ToolTipService.SetShowOnDisabled(use, true);
                use.Click += async (_, _) => await ChangeAsync(
                    $"Switch DNS on {Adapter?.Name} to {provider.Name}?\n\n{provider.Note}\nYour current setting is saved first.",
                    a => _changes.SetDns(a, provider));
                Grid.SetColumn(use, 1);
                grid.Children.Add(use);
            }
            var card = Ui.Card(grid);
            card.Padding = new Thickness(14, 8, 14, 8);
            card.Margin = new Thickness(0, 0, 8, 6);
            _dnsRows.Children.Add(card);
        }
        if (_scores.Any(s => s.Name == "Current DNS")) Add("Current DNS", "Your first current server, for comparison.", null);
        foreach (var p in DnsBenchmark.Providers.OrderBy(p => _scores.FirstOrDefault(s => s.Name == p.Name)?.MedianMs ?? double.MaxValue))
            Add(p.Name, $"{string.Join(", ", p.V4)}. {p.Note}", p);
    }

    private async Task TestDnsAsync(Button button) { button.IsEnabled = false; try { await RunDnsTestAsync(); } finally { button.IsEnabled = true; } }

    public async Task RunDnsTestAsync(int rounds = 3)
    {
        if (_dnsTest != null) return;
        using var stop = new CancellationTokenSource();
        _dnsTest = stop;
        try
        {
            var servers = DnsBenchmark.Providers.Select(p => (p.Name, IPAddress.Parse(p.V4[0]))).ToList();
            if (Adapter?.Dns.FirstOrDefault() is { } current && IPAddress.TryParse(current, out var address))
                servers.Add(("Current DNS", address));
            var progress = (IProgress<string>)new Progress<string>(t => { if (_dnsTest != null) _status.Text = t; });
            _scores = await DnsBenchmark.RunAsync(servers, progress.Report, stop.Token, rounds);
            _dnsTest = null;
            _status.Text = _scores.All(s => s.Answered == 0)
                ? "No server answered. A firewall or VPN may be blocking DNS lookups." : "DNS test finished.";
            RenderDns();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "DNS test failed: " + ex.Message; }
        finally { _dnsTest = null; }
    }

    // ---- TCP/IP and Winsock ----

    private FrameworkElement TcpPage()
    {
        var recommended = Make("Apply Windows defaults (recommended)");
        recommended.ToolTip = "Auto-tuning Normal, receive side scaling and segment coalescing enabled";
        recommended.Click += async (_, _) => await ChangeAsync(
            "Set auto-tuning to Normal and enable receive side scaling and receive segment coalescing?\n\n" +
            "Current values are saved first. Adapter settings are not changed.", a =>
            {
                var results = new[] { ("autotuning", "Normal"), ("rss", "Enabled"), ("rsc", "Enabled") }
                    .Select(s => _changes.SetTcp(a, s.Item1, s.Item2)).ToList();
                var failed = results.FirstOrDefault(r => !r.Ok);
                return failed ?? new NetResult(true, "The recommended global settings are in place.");
            });
        var restore = Make("Restore my original TCP/IP settings");
        restore.Click += async (_, _) => await ChangeAsync(
            "Restore the TCP/IP values saved before Network Tools first changed them?\n\nThe connection may drop briefly.",
            a => _changes.RestoreTcp(a));
        var winsock = Make("Reset Winsock...");
        winsock.Click += async (_, _) => await ChangeAsync(
            "Reset the Windows Winsock catalog?\n\nUse this only to repair broken networking. VPN, security and " +
            "network software may stop working until repaired or reinstalled. It cannot be undone by this app, " +
            "and you must restart the PC afterwards.", _ => _changes.ResetWinsock());
        winsock.IsEnabled = recommended.IsEnabled = restore.IsEnabled = _isAdmin;
        var row = new WrapPanel();
        row.Children.Add(recommended);
        row.Children.Add(restore);
        row.Children.Add(winsock);
        return Ui.Stack(row, Ui.Text(
            "Windows defaults suit most PCs. Changing these rarely makes a connection faster and can make it " +
            "slower. Change one thing at a time and test. Changing an adapter setting can disconnect the network " +
            "for a few seconds." + (_isAdmin ? "" : " Changes need administrator rights."), 11, "MutedBrush"), _tcpRows);
    }

    private async Task LoadTcpAsync()
    {
        if (Adapter is not { } adapter) return;
        _tcpRows.Children.Clear();
        _tcpRows.Children.Add(Ui.Text("Reading TCP/IP settings...", 12));
        try
        {
            var state = await Task.Run(() => _changes.ReadTcp(adapter));
            if (Adapter?.Id != adapter.Id) return;
            _tcpRows.Children.Clear();
            Setting("Receive window auto-tuning (all adapters)", "autotuning", state.AutoTuning,
                NetworkChanges.AutoTuningLevels, NetworkChanges.AutoTuningLevels);
            string[] onOff = { "Enabled", "Disabled" };
            Setting("Receive side scaling (all adapters)", "rss", state.Rss, onOff, onOff);
            Setting("Receive segment coalescing (all adapters)", "rsc", state.Rsc, onOff, onOff);
            foreach (var p in state.Properties)
                Setting($"{p.Name} ({adapter.Name})", NetworkChanges.AdapterKey(adapter, p.Keyword), p.Value,
                    p.ValidValues, p.ValidNames);
        }
        catch (Exception ex)
        {
            _tcpRows.Children.Clear();
            _tcpRows.Children.Add(Ui.Text(ex.Message, 12));
        }
        finally { HasLoadedTcp = true; }
    }

    private void Setting(string label, string key, string current, string[] values, string[] names)
    {
        var box = new ComboBox { MinWidth = 170, IsEnabled = _isAdmin, VerticalAlignment = VerticalAlignment.Center };
        foreach (var name in names) box.Items.Add(new ComboBoxItem { Content = name });
        box.SelectedIndex = Array.IndexOf(values, current);
        var original = box.SelectedIndex;
        System.Windows.Automation.AutomationProperties.SetName(box, label);
        var apply = Make("Apply");
        apply.Margin = new Thickness(8, 0, 0, 0);
        apply.IsEnabled = false;
        box.SelectionChanged += (_, _) => apply.IsEnabled = _isAdmin && box.SelectedIndex != original;
        apply.Click += async (_, _) =>
        {
            var value = values[box.SelectedIndex];
            await ChangeAsync($"Set \"{label}\" to {names[box.SelectedIndex]}?\n\nThe original value is saved first. " +
                              "The connection may drop briefly.", a => _changes.SetTcp(a, key, value));
        };
        var grid = new Grid { Margin = new Thickness(0, 3, 8, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = Ui.Text(label, 12);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(box, 1);
        Grid.SetColumn(apply, 2);
        grid.Children.Add(text);
        grid.Children.Add(box);
        grid.Children.Add(apply);
        _tcpRows.Children.Add(grid);
    }

    private async Task ChangeAsync(string question, Func<AdapterInfo, NetResult> change)
    {
        if (_busy || Adapter is not { } adapter) return;
        if (MessageBox.Show(question, "Network Tools", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                            MessageBoxResult.No) != MessageBoxResult.Yes) return;
        _busy = _work.Changing = true;
        _page.IsEnabled = _adapters.IsEnabled = false;
        _status.Text = "Working...";
        string message;
        try { message = (await Task.Run(() => change(adapter))).Message; }
        catch (Exception ex) { message = "The change did not complete: " + ex.Message; }
        finally
        {
            _busy = _work.Changing = false;
            _page.IsEnabled = _adapters.IsEnabled = true;
        }
        ActivityLog.Post("Network", "Network change: " + question.Split('\n')[0].TrimEnd('?'),
            !message.StartsWith("The change did not complete"), null, message);
        await LoadAdaptersAsync();
        _tcpRows.Children.Clear();
        HasLoadedTcp = false;
        if (_tabs["TCP/IP"].IsChecked == true) await LoadTcpAsync();
        _status.Text = message;
    }

    // ---- Speed ----

    private FrameworkElement SpeedPage()
    {
        _speedButton.HorizontalAlignment = HorizontalAlignment.Left;
        _speedButton.Role(ButtonRole.Main);
        _speedButton.Click += async (_, _) =>
        {
            if (_speed != null) _speed.Cancel();
            else await RunSpeedAsync(TimeSpan.FromSeconds(10), 500_000_000);
        };
        return Ui.Stack(_speedButton, _speedText, Ui.Text(
            "Downloads test data from Cloudflare (speed.cloudflare.com) for up to 10 seconds and discards it. " +
            "It can use up to about 500 MB of data, so avoid it on a limited or metered connection. A VPN, other " +
            "downloads, Wi-Fi and the distance to the server affect the result.", 11, "MutedBrush"));
    }

    public async Task RunSpeedAsync(TimeSpan duration, long cap)
    {
        using var stop = new CancellationTokenSource();
        _speed = stop;
        _speedButton.Content = "Cancel test";
        _speedText.Text = "Starting...";
        var running = true;
        var progress = (IProgress<(double, long)>)new Progress<(double, long)>(p =>
        {
            if (running) _speedText.Text = $"{p.Item1:0.0} Mbps  ({p.Item2 / 1e6:0} MB used)";
        });
        try
        {
            var result = await new SpeedTest().RunAsync(duration, cap, (m, b) => progress.Report((m, b)), stop.Token);
            running = false;
            _speedText.Text = $"Download: {result.Mbps:0.0} Mbps\n({result.Mbps / 8:0.0} MB per second; " +
                              $"{result.Bytes / 1e6:0} MB used in {result.Seconds:0.0} s)";
        }
        catch (OperationCanceledException) { running = false; _speedText.Text = "Test cancelled."; }
        catch (Exception ex) { running = false; _speedText.Text = "Speed test failed: " + ex.Message; }
        finally
        {
            _speed = null;
            _speedButton.Content = "Start download test";
        }
    }

    private void RestartElevated()
    {
        try
        {
            PCOptimizer.Core.SingleInstance.StartElevatedCopy();
            Application.Current.MainWindow?.Close();
        }
        catch (Win32Exception) { _status.Text = "Administrator restart was cancelled."; }
    }
}
