using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCOptimizer.Services.Network;

public sealed record AdapterInfo(string Id, int Index, string Name, string Description, List<string> IPv4,
    List<string> IPv6, List<string> Gateways, List<string> Dns, bool DnsAutomaticV4, bool HasGlobalIPv6, bool Physical);

public static class NetworkInfo
{
    /// <summary>fec0:0:0:ffff:: addresses are unused Windows placeholders.</summary>
    private static bool Real(IPAddress a) => !a.ToString().StartsWith("fec0:0:0:ffff", StringComparison.OrdinalIgnoreCase);

    /// <summary>Connected adapters that have a gateway, physical ones first. Contacts nothing.</summary>
    public static List<AdapterInfo> Read(Func<string, (string V4, string V6)> staticDns)
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var p = nic.GetIPProperties();
            var gateways = p.GatewayAddresses.Select(g => g.Address)
                .Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).Select(a => a.ToString()).ToList();
            if (gateways.Count == 0) continue;
            int index;
            try { index = p.GetIPv4Properties().Index; }
            catch (NetworkInformationException) { continue; }
            var addresses = p.UnicastAddresses.Select(u => u.Address).ToList();
            var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal).ToList();
            var description = nic.Description;
            var physical = !new[] { "virtual", "vpn", "tap-", "hyper-v", "wintun", "wireguard", "warp" }
                .Any(w => (description + " " + nic.Name).Contains(w, StringComparison.OrdinalIgnoreCase));
            result.Add(new AdapterInfo(nic.Id, index, nic.Name, description,
                addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()).ToList(),
                v6.Select(a => a.ToString()).ToList(), gateways,
                p.DnsAddresses.Where(Real).Select(a => a.ToString()).Distinct().ToList(),
                staticDns(nic.Id).V4.Length == 0,
                v6.Any(a => (a.GetAddressBytes()[0] & 0xE0) == 0x20), physical));
        }
        return result.OrderByDescending(a => a.Physical).ThenBy(a => a.Name).ToList();
    }
}

/// <summary>A rolling window of ping results; null means no reply.</summary>
public sealed class PingStats
{
    private readonly List<double?> _samples = new();
    public int Capacity { get; }
    public PingStats(int capacity = 120) => Capacity = capacity;
    public IReadOnlyList<double?> Samples => _samples;

    public void Add(double? milliseconds)
    {
        _samples.Add(milliseconds);
        if (_samples.Count > Capacity) _samples.RemoveAt(0);
    }

    public void Clear() => _samples.Clear();
    private IEnumerable<double> Replies => _samples.Where(s => s != null).Select(s => s!.Value);
    public double LossPercent => _samples.Count == 0 ? 0 : 100.0 * _samples.Count(s => s == null) / _samples.Count;
    public double? Last => _samples.Count == 0 ? null : _samples[^1];
    public double? Average => Replies.Any() ? Replies.Average() : null;
    public double? Minimum => Replies.Any() ? Replies.Min() : null;
    public double? Maximum => Replies.Any() ? Replies.Max() : null;

    /// <summary>Average change between replies to back-to-back pings.</summary>
    public double? Jitter
    {
        get
        {
            var changes = _samples.Zip(_samples.Skip(1)).Where(p => p.First != null && p.Second != null)
                .Select(p => Math.Abs(p.Second!.Value - p.First!.Value)).ToList();
            return changes.Count == 0 ? null : changes.Average();
        }
    }
}

public sealed record DnsProvider(string Name, string Note, string[] V4, string[] V6);

public sealed record DnsScore(string Name, double? MedianMs, int Answered, int Sent);

public static class DnsBenchmark
{
    public static readonly IReadOnlyList<DnsProvider> Providers = new DnsProvider[]
    {
        new("Cloudflare", "No filtering.", new[] { "1.1.1.1", "1.0.0.1" },
            new[] { "2606:4700:4700::1111", "2606:4700:4700::1001" }),
        new("Google", "No filtering.", new[] { "8.8.8.8", "8.8.4.4" },
            new[] { "2001:4860:4860::8888", "2001:4860:4860::8844" }),
        new("Quad9", "Blocks known malicious domains.", new[] { "9.9.9.9", "149.112.112.112" },
            new[] { "2620:fe::fe", "2620:fe::9" }),
        new("OpenDNS", "Standard service; may block phishing domains.", new[] { "208.67.222.222", "208.67.220.220" },
            new[] { "2620:119:35::35", "2620:119:53::53" }),
        new("AdGuard", "Blocks ads and trackers; some sites may break.", new[] { "94.140.14.14", "94.140.15.15" },
            new[] { "2a10:50c0::ad1:ff", "2a10:50c0::ad2:ff" }),
        new("CleanBrowsing Security", "Blocks malicious and phishing domains.",
            new[] { "185.228.168.9", "185.228.169.9" }, new[] { "2a0d:2a00:1::2", "2a0d:2a00:2::2" })
    };

    /// <summary>Common public names only; nothing from this PC is looked up.</summary>
    private static readonly string[] Names =
        { "example.com", "microsoft.com", "wikipedia.org", "amazon.com", "youtube.com", "github.com" };

    public static byte[] BuildQuery(ushort id, string name)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 });   // end of name, type A, class IN
        return bytes.ToArray();
    }

    /// <summary>A successful reply to our question with at least one answer.</summary>
    public static bool IsAnswer(byte[] reply, ushort id) => reply.Length >= 12 &&
        BinaryPrimitives.ReadUInt16BigEndian(reply) == id && (reply[2] & 0x80) != 0 && (reply[3] & 0x0F) == 0 &&
        BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(6)) > 0;

    public static async Task<double?> QueryAsync(IPEndPoint server, string name, CancellationToken token)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        using var client = new UdpClient(server.AddressFamily);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(2000);
        try
        {
            client.Connect(server);   // replies from other addresses are ignored
            var watch = Stopwatch.StartNew();
            await client.SendAsync(BuildQuery(id, name), limit.Token);
            while (true)
            {
                var reply = await client.ReceiveAsync(limit.Token);
                if (reply.Buffer.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(reply.Buffer) == id)
                    return IsAnswer(reply.Buffer, id) ? watch.Elapsed.TotalMilliseconds : null;
            }
        }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); return null; }
        catch (SocketException) { return null; }
    }

    public static double? Median(List<double> values)
    {
        if (values.Count == 0) return null;
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Servers take turns, so a short network hiccup does not count against just one of them.</summary>
    public static async Task<List<DnsScore>> RunAsync(IReadOnlyList<(string Name, IPAddress Server)> servers,
        Action<string>? progress, CancellationToken token, int rounds = 3)
    {
        var times = servers.ToDictionary(s => s.Name, _ => new List<double>());
        for (var round = 0; round < rounds; round++)
            foreach (var name in Names)
            {
                progress?.Invoke($"Testing DNS: round {round + 1} of {rounds}, {name}...");
                foreach (var (label, address) in servers)
                    if (await QueryAsync(new IPEndPoint(address, 53), name, token) is { } ms) times[label].Add(ms);
            }
        return servers.Select(s => new DnsScore(s.Name, Median(times[s.Name]), times[s.Name].Count,
            rounds * Names.Length)).ToList();
    }

    /// <summary>The fastest server that answered at least 90% of lookups.</summary>
    public static DnsScore? Fastest(IEnumerable<DnsScore> scores) => scores
        .Where(s => s.MedianMs != null && s.Answered >= s.Sent * 0.9).OrderBy(s => s.MedianMs).FirstOrDefault();
}

public sealed record SpeedResult(double Mbps, long Bytes, double Seconds);

/// <summary>Downloads test data from Cloudflare's speed test server and discards it.</summary>
public sealed class SpeedTest
{
    public const string Url = "https://speed.cloudflare.com/__down?bytes=25000000";
    private readonly HttpMessageHandler? _handler;
    public SpeedTest(HttpMessageHandler? handler = null) => _handler = handler;

    public async Task<SpeedResult> RunAsync(TimeSpan duration, long byteCap, Action<double, long>? progress,
        CancellationToken token, TimeSpan? warmUp = null, int streams = 4)
    {
        using var client = _handler == null ? new HttpClient() : new HttpClient(_handler, false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(duration);
        var warm = (warmUp ?? TimeSpan.FromSeconds(1.5)).TotalSeconds;
        long total = 0, warmBytes = 0;
        double warmAt = 0;
        var gate = new object();
        string? failure = null;
        var watch = Stopwatch.StartNew();

        async Task Stream()
        {
            var buffer = new byte[81920];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var response = await client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        failure = $"The test server answered {(int)response.StatusCode}. Try again later.";
                        return;
                    }
                    await using var body = await response.Content.ReadAsStreamAsync(stop.Token);
                    int read, received = 0;
                    while ((read = await body.ReadAsync(buffer, stop.Token)) > 0)
                    {
                        received += read;
                        lock (gate)
                        {
                            total += read;
                            var now = watch.Elapsed.TotalSeconds;
                            if (warmAt == 0 && now >= warm) (warmAt, warmBytes) = (now, total);
                        }
                        if (Interlocked.Read(ref total) >= byteCap) stop.Cancel();
                    }
                    if (received == 0) { failure = "The test server sent no data."; return; }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                if (!stop.IsCancellationRequested) failure = ex.Message;
            }
        }

        var all = Task.WhenAll(Enumerable.Range(0, streams).Select(_ => Task.Run(Stream)));
        while (await Task.WhenAny(all, Task.Delay(250)) != all)
        {
            var seconds = watch.Elapsed.TotalSeconds;
            var bytes = Interlocked.Read(ref total);
            if (seconds > 0) progress?.Invoke(bytes * 8 / seconds / 1e6, bytes);
        }
        await all;
        var end = watch.Elapsed.TotalSeconds;
        token.ThrowIfCancellationRequested();
        if (total == 0) throw new IOException(failure ?? "No data was received.");
        // Leave out the slow start when the test ran long enough after it.
        var useWarm = warmAt > 0 && end - warmAt >= 1;
        var measuredBytes = useWarm ? total - warmBytes : total;
        var measuredSeconds = useWarm ? end - warmAt : end;
        return new SpeedResult(measuredBytes * 8 / measuredSeconds / 1e6, total, end);
    }
}
