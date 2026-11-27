using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCOptimizer.Services.DriverTools;
using PCOptimizer.Services.Network;

namespace PCOptimizer;

/// <summary>Changes use a fake system. No network setting on this PC is touched.</summary>
public static class NetworkTests
{
    private sealed class FakeServer(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(new byte[100_000]) });
    }

    public static async Task RunAsync(string work, Action<string, bool> check)
    {
        var stats = new PingStats(5);
        foreach (var sample in new double?[] { 999, 10, 14, null, 20, 18 }) stats.Add(sample);
        check("ping statistics use a rolling window with loss and jitter", stats.Samples.Count == 5 &&
              stats.LossPercent == 20 && stats.Jitter == 3 && stats.Average == 15.5 && stats.Maximum == 20);

        var query = DnsBenchmark.BuildQuery(0x1234, "ab.com");
        check("DNS queries are well formed", query.Length == 24 && query[0] == 0x12 && query[12] == 2 && query[15] == 3);
        byte[] Reply(byte flags, byte code, byte answers) => new byte[] { 0x12, 0x34, flags, code, 0, 1, 0, answers, 0, 0, 0, 0 };
        check("only successful DNS answers count", DnsBenchmark.IsAnswer(Reply(0x81, 0x80, 1), 0x1234) &&
              !DnsBenchmark.IsAnswer(Reply(0x81, 0x83, 1), 0x1234) && !DnsBenchmark.IsAnswer(Reply(0x01, 0, 1), 0x1234) &&
              !DnsBenchmark.IsAnswer(Reply(0x81, 0x80, 1), 0x9999));

        using (var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var answer = Task.Run(async () =>
            {
                var request = await server.ReceiveAsync();
                var reply = request.Buffer.ToArray();
                (reply[2], reply[3], reply[7]) = (0x81, 0x80, 1);
                await server.SendAsync(reply, reply.Length, request.RemoteEndPoint);
            });
            var time = await DnsBenchmark.QueryAsync((IPEndPoint)server.Client.LocalEndPoint!, "example.com", default);
            await answer;
            check("a DNS lookup is timed against a local test server", time is >= 0 and < 2000);
        }
        check("the fastest DNS must also be reliable", DnsBenchmark.Fastest(new[]
            { new DnsScore("flaky", 1, 3, 18), new DnsScore("good", 12, 18, 18), new DnsScore("slow", 40, 18, 18) })!.Name == "good");
        check("six DNS providers have valid addresses", DnsBenchmark.Providers.Count == 6 &&
              DnsBenchmark.Providers.All(p => p.V4.Concat(p.V6).Count(a => IPAddress.TryParse(a, out _)) == 4));

        var speed = await new SpeedTest(new FakeServer(HttpStatusCode.OK))
            .RunAsync(TimeSpan.FromSeconds(5), 1_000_000, null, default, streams: 2);
        check("the speed test stops at its data limit", speed.Mbps > 0 && speed.Bytes is >= 1_000_000 and < 1_500_000);
        var refused = false;
        try
        {
            await new SpeedTest(new FakeServer(HttpStatusCode.TooManyRequests))
                .RunAsync(TimeSpan.FromSeconds(5), 1000, null, default);
        }
        catch (IOException ex) { refused = ex.Message.Contains("429"); }
        check("a refused speed test is reported, not scored", refused);

        // Fake Windows: scripts change these values.
        var id = Guid.NewGuid().ToString("B");
        string v4 = "10.0.0.1", v6 = "", tuning = "Normal", lso = "1";
        bool apply = true;
        var commands = new List<string>();
        ToolOutput Fake(string program, string[] args, TimeSpan limit)
        {
            if (program.EndsWith("netsh.exe"))
            {
                var line = string.Join(' ', args);
                if (line == "winsock show catalog") return new(apply ? 0 : 1, apply ? "catalog" : "");
                commands.Add(line);
                if (apply && line.Contains("autotuninglevel=disabled")) tuning = "Disabled";
                if (apply && line.Contains("autotuninglevel=normal")) tuning = "Normal";
                return new(0, "");
            }
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
            if (script.Contains("ConvertTo-Json"))
                return new(0, JsonSerializer.Serialize(new TcpState(tuning, "Enabled", "Enabled", new()
                {
                    new("*LsoV2IPv4", "LSO", lso, "", new[] { "0", "1" }, new[] { "Off", "On" }),
                    new("*SpeedDuplex", "Speed", "0", "", new[] { "0", "6" }, new[] { "Auto", "1G" })
                })));
            commands.Add(script);
            if (!apply) return new(1, "denied");
            if (script.Contains("-ResetServerAddresses")) (v4, v6) = ("", "");
            if (Regex.Match(script, @"-ServerAddresses @\(([^)]*)\)") is { Success: true } m)
            {
                var all = m.Groups[1].Value.Split(',').Select(a => a.Trim('\'')).ToList();
                if (all.Any(a => !a.Contains(':'))) v4 = string.Join(",", all.Where(a => !a.Contains(':')));
                if (all.Any(a => a.Contains(':'))) v6 = string.Join(",", all.Where(a => a.Contains(':')));
            }
            if (Regex.Match(script, @"-RegistryValue (\d+)") is { Success: true } r) lso = r.Groups[1].Value;
            return new(0, "");
        }
        var folder = Path.Combine(work, "network");
        var changes = new NetworkChanges(folder, true, Fake, _ => (v4, v6));
        var adapter = new AdapterInfo(id, 7, "Test", "Test adapter", new(), new(), new(), new() { "10.0.0.1" }, false, true, true);
        var cloud = DnsBenchmark.Providers[0];
        var quad = DnsBenchmark.Providers[2];

        var plain = new NetworkChanges(folder, false, Fake, _ => (v4, v6));
        check("network changes need administrator rights", !plain.SetDns(adapter, cloud).Ok &&
              !plain.SetTcp(adapter, "autotuning", "Disabled").Ok && !plain.ResetWinsock().Ok && commands.Count == 0);
        check("DNS switching sets IPv4 and IPv6 and confirms it",
              changes.SetDns(adapter, cloud).Ok && v4 == "1.1.1.1,1.0.0.1" && v6.StartsWith("2606:4700"));
        changes.SetDns(adapter, quad);
        check("the first original DNS is kept across switches", changes.LoadBackup().Dns.Single() is { V4: "10.0.0.1", V6: "" });
        check("restoring DNS returns the original and clears the backup",
              changes.RestoreDns(adapter).Ok && v4 == "10.0.0.1" && v6 == "" && !changes.HasDnsBackup(id));
        apply = false;
        check("a DNS change Windows refused is reported", !changes.SetDns(adapter, cloud).Ok);
        apply = true;

        var tcp = changes.ReadTcp(adapter);
        check("only offload adapter settings are offered", tcp.Properties.Single().Keyword == "*LsoV2IPv4");
        commands.Clear();
        check("values outside the allowed list are refused",
              !changes.SetTcp(adapter, "autotuning", "x & calc").Ok &&
              !changes.SetTcp(adapter, NetworkChanges.AdapterKey(adapter, "*LsoV2IPv4"), "5").Ok &&
              !changes.SetTcp(adapter, NetworkChanges.AdapterKey(adapter, "*SpeedDuplex"), "6").Ok && commands.Count == 0);
        var changed = changes.SetTcp(adapter, "autotuning", "Disabled").Ok &&
                      changes.SetTcp(adapter, NetworkChanges.AdapterKey(adapter, "*LsoV2IPv4"), "0").Ok;
        check("TCP and offload changes are applied", changed && tuning == "Disabled" && lso == "0");
        check("TCP restore returns the originals", changes.RestoreTcp(adapter).Ok && tuning == "Normal" && lso == "1" &&
              changes.LoadBackup().Tcp.Count == 0);

        commands.Clear();
        apply = false;
        check("Winsock is not reset if its list cannot be saved", !changes.ResetWinsock().Ok && commands.Count == 0);
        apply = true;
        check("Winsock reset saves the list first", changes.ResetWinsock().Ok && commands.Single() == "winsock reset" &&
              Directory.GetFiles(folder, "winsock-before-reset-*.txt").Length == 1);

        var vault = Path.Combine(work, "network-vault");
        var own = Directory.CreateDirectory(Path.Combine(work, "network-own")).FullName;
        File.WriteAllText(Path.Combine(own, "network-backup.json"), JsonSerializer.Serialize(new NetworkBackup
        { Dns = { new SavedDnsSetting { AdapterId = id, V4 = "203.0.113.66" } } }));
        var guarded = new NetworkChanges(own, true, Fake, _ => (v4, v6), SecurityTests.Stand(vault));
        commands.Clear();
        check("a network backup planted in your own folder is not used",
              !guarded.RestoreDns(adapter).Ok && guarded.DescribeDnsBackup(id) == null && commands.Count == 0);
        (v4, v6) = ("10.0.0.1", "");
        guarded.SetDns(adapter, cloud);
        check("network originals are kept in the protected folder and can be shown",
              File.Exists(Path.Combine(vault, "network-backup.json")) &&
              guarded.DescribeDnsBackup(id) == "IPv4: 10.0.0.1\nIPv6: automatic" &&
              guarded.RestoreDns(adapter).Ok && v4 == "10.0.0.1");

        var blocked = Path.Combine(work, "network-blocked");
        Directory.CreateDirectory(Path.Combine(blocked, "network-backup.json"));
        commands.Clear();
        var threw = false;
        try { new NetworkChanges(blocked, true, Fake, _ => (v4, v6)).SetDns(adapter, cloud); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { threw = true; }
        check("nothing changes if the backup cannot be saved", threw && commands.Count == 0);
    }
}
