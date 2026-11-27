using System.Text.Json;
using Microsoft.Win32;
using PCOptimizer.Services.RegistryTools;
using PCOptimizer.Services.Tweaks;

namespace PCOptimizer;

/// <summary>Changes happen only in a fake system and a throwaway registry key. Real settings are only read.</summary>
public static class TweakTests
{
    private const string Services = @"SYSTEM\CurrentControlSet\Services\";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string AdapterId = "{11111111-2222-3333-4444-555555555555}";
    private const string Interface = Services + @"Tcpip\Parameters\Interfaces\" + AdapterId;

    private sealed class Fake : ITweakSystem
    {
        private readonly RegistryStore _registry;
        public Fake(RegistryStore registry) => _registry = registry;
        public Guid Plan = Guid.NewGuid();
        public bool FailPower;
        public readonly Dictionary<(Guid, Guid), uint> Power = new();
        public Guid ActivePlan() => Plan;
        public uint? ReadPower(Guid plan, Guid group, Guid setting) =>
            Power.TryGetValue((plan, setting), out var v) ? v : null;
        public void WritePower(Guid plan, Guid group, Guid setting, uint value)
        {
            if (FailPower) throw new IOException("refused");
            Power[(plan, setting)] = value;
        }
        private void Write(string path, string name, object value)
        {
            using var key = _registry.Create(Hive.LocalMachine, path);
            key.SetValue(name, value);
        }
        public void SetServiceStart(string service, ServiceStart start)
        {
            Write(Services + service, "Start", start.Start);
            Write(Services + service, "DelayedAutostart", start.Delayed ? 1 : 0);
        }
        public void SetHibernation(bool on) => Write(PowerKey, "HibernateEnabled", on ? 1 : 0);
        public IReadOnlyList<Adapter> ActiveAdapters() => new[] { new Adapter(AdapterId, "Test") };
        public void SetDns(string id, IReadOnlyList<string> servers) =>
            Write(Services + @"Tcpip\Parameters\Interfaces\" + id, "NameServer", string.Join(",", servers));
    }

    public static void Run(string work, Action<string, bool> check, List<string> log)
    {
        var tweaks = TweakCatalog.Create();
        check("at least 50 tweaks with unique ids in all five categories", tweaks.Count >= 50 &&
              tweaks.Select(t => t.Id).Distinct().Count() == tweaks.Count &&
              tweaks.Select(t => t.Category).Distinct().Count() == 5);
        check("Safe tweaks are only plain per-user settings", tweaks.Where(t => t.Risk == TweakRisk.Safe)
              .All(t => !t.NeedsAdmin && t is RegistryTweak r && r.Values.Count > 0 && r.Values.All(v =>
                  v.Hive == Hive.CurrentUser && !v.Path.Contains("Policies", StringComparison.OrdinalIgnoreCase))));
        log.Add($"Tweaks: {tweaks.Count} (" + string.Join(", ", tweaks.GroupBy(t => t.Category)
            .Select(g => $"{g.Key} {g.Count()}")) + "; " + string.Join(", ", tweaks.GroupBy(t => t.Risk)
            .Select(g => $"{g.Key} {g.Count()}")) + ")");

        var root = @"Software\PCOptimizerSelfTest-" + Guid.NewGuid().ToString("N");
        var registry = new RegistryStore(root);
        try
        {
            var fake = new Fake(registry);
            var engine = new TweakEngine(tweaks, new TweakContext(registry, fake), Path.Combine(work, "tweaks.json"));
            fake.SetServiceStart("SysMain", new(2, false));
            fake.SetServiceStart("WSearch", new(2, true));
            fake.SetServiceStart("DiagTrack", new(3, false));
            fake.SetHibernation(true);
            fake.SetDns(AdapterId, new[] { "192.168.1.1" });
            foreach (var t in tweaks.OfType<PowerTweak>()) fake.Power[(fake.Plan, t.Setting)] = t.Target + 7;
            using (var key = registry.Create(Hive.CurrentUser, @"Control Panel\Desktop"))
            {
                key.SetValue("MenuShowDelay", "400");
                key.SetValue("DragFullWindows", "%keep%", RegistryValueKind.ExpandString);
            }

            // Every value under the test key, plus power values.
            string Snapshot()
            {
                var lines = new List<string>();
                void Walk(RegistryKey key, string path)
                {
                    foreach (var name in key.GetValueNames())
                    {
                        var raw = RegistryStore.ReadValue(key, name)!;
                        lines.Add($"{path}|{name}|{raw.Type}|{Convert.ToHexString(raw.Data)}");
                    }
                    foreach (var child in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(child)!;
                        Walk(sub, path + "\\" + child);
                    }
                }
                using var top = Registry.CurrentUser.OpenSubKey(root)!;
                Walk(top, "");
                lines.AddRange(fake.Power.Select(p => $"power|{p.Key}|{p.Value}"));
                lines.Sort(StringComparer.Ordinal);
                return string.Join("\n", lines);
            }

            var original = Snapshot();
            var all = engine.Apply(tweaks, true);
            log.AddRange(all.Problems);
            check("every tweak applies and reads back as on", all.Problems.Count == 0 && all.Changed == tweaks.Count &&
                  engine.AllStatuses().Values.All(s => s.State == TweakState.Applied));
            check("applying again changes nothing", engine.Apply(tweaks, true).Changed == 0 &&
                  engine.Records().Count == tweaks.Count);
            var reset = engine.Revert(null, true);
            check("reset restores every original exactly", reset.Problems.Count == 0 && reset.Changed == tweaks.Count &&
                  Snapshot() == original && engine.Records().Count == 0);

            var safe = engine.OneClickTargets(false);
            var one = engine.Apply(safe, false);
            check("One-Click applies only Safe tweaks, without administrator rights",
                  safe.Count > 10 && safe.All(t => t.Risk == TweakRisk.Safe) && one.Changed == safe.Count &&
                  tweaks.Where(t => t.Risk != TweakRisk.Safe).All(t => engine.Status(t).State != TweakState.Applied));
            engine.Revert(null, false);
            check("One-Click is fully reversible", Snapshot() == original);

            Tweak One(string id) => tweaks.First(t => t.Id == id);
            var refused = engine.Apply(new[] { One("sysmain") }, false);
            check("administrator tweaks are refused without rights",
                  refused.Changed == 0 && refused.Problems.Count == 1 && Snapshot() == original);

            engine.Apply(new[] { One("menu-delay") }, false);
            using (var key = registry.Create(Hive.CurrentUser, @"Control Panel\Desktop"))
                key.SetValue("MenuShowDelay", "250");
            engine.Revert(null, false);
            using (var key = registry.Open(Hive.CurrentUser, @"Control Panel\Desktop"))
                check("a value you changed afterward is kept", key!.GetValue("MenuShowDelay") is "250");

            var usb = (PowerTweak)One("usb-suspend");
            var first = fake.Plan;
            engine.Apply(new[] { usb }, false);
            var second = fake.Plan = Guid.NewGuid();
            fake.Power[(second, usb.Setting)] = 9;
            engine.Apply(new[] { usb }, false);
            var firstRestored = fake.Power[(first, usb.Setting)] == usb.Target + 7;
            engine.Revert(null, false);
            check("switching power plans loses no original",
                  firstRestored && fake.Power[(second, usb.Setting)] == 9);

            fake.FailPower = true;
            var failed = engine.Apply(new[] { usb }, false);
            check("a failed change leaves no record", failed.Changed == 0 && failed.Problems.Count > 0 &&
                  engine.Records().Count == 0);
            fake.FailPower = false;

            var blocker = Path.Combine(work, "tweak-blocker");
            File.WriteAllText(blocker, "x");
            var unable = new TweakEngine(tweaks, new TweakContext(registry, fake), Path.Combine(blocker, "t.json"));
            var before = Snapshot();
            var none = unable.Apply(new[] { One("transparency") }, false);
            check("no saved original means no change", none.Changed == 0 && none.Problems.Count > 0 && Snapshot() == before);

            var tampered = new[]
            {
                new TweakRecord("transparency", DateTime.Now, JsonSerializer.Serialize(new[]
                    { new SavedValue(Hive.LocalMachine, @"SOFTWARE\Evil", "x", 4, new byte[4]) })),
                new TweakRecord("dns-cloudflare", DateTime.Now, JsonSerializer.Serialize(new[]
                    { new SavedDns(AdapterId, "bad & value") }))
            };
            File.WriteAllText(engine.JournalPath, JsonSerializer.Serialize(tampered));
            var rejected = engine.Revert(null, true);
            using (var evil = registry.Open(Hive.LocalMachine, @"SOFTWARE\Evil"))
                check("altered saved originals are rejected",
                      rejected.Changed == 0 && rejected.Problems.Count == 2 && evil == null && Snapshot() == before);

            // With a protected folder, as the app uses it.
            File.Delete(engine.JournalPath);
            var vault = Path.Combine(work, "tweak-vault");
            var userFile = Path.Combine(work, "tweaks-user.json");
            var guarded = new TweakEngine(tweaks, new TweakContext(registry, fake), userFile,
                                          SecurityTests.Stand(vault));
            var start = Snapshot();
            guarded.Apply(new[] { One("menu-delay") }, false);
            check("your own tweaks never need the protected folder", !Directory.Exists(vault));
            guarded.Apply(new[] { One("sysmain"), One("dns-cloudflare") }, true);
            var adminFile = Path.Combine(vault, "tweaks-journal.json");
            check("administrator originals go only in the protected folder",
                  File.ReadAllText(adminFile).Contains("dns-cloudflare") && File.ReadAllText(adminFile).Contains("sysmain") &&
                  !File.ReadAllText(userFile).Contains("dns-cloudflare") && !File.ReadAllText(userFile).Contains("sysmain") &&
                  guarded.Records().Count == 3);
            check("saved DNS addresses can be shown before restoring",
                  guarded.DescribeSavedDns(null)?.Contains("192.168.1.1") == true &&
                  guarded.DescribeSavedDns(new[] { "menu-delay" }) == null);
            var protectedReset = guarded.Revert(null, true);
            check("protected originals restore exactly", protectedReset.Changed == 3 && Snapshot() == start &&
                  guarded.Records().Count == 0);

            guarded.Apply(new[] { One("dns-cloudflare") }, true);
            guarded.Revert(null, true);
            guarded.Apply(new[] { One("dns-cloudflare") }, true);
            File.Delete(adminFile);   // only the planted copy below remains
            File.WriteAllText(userFile, JsonSerializer.Serialize(new[] { new TweakRecord("dns-cloudflare", DateTime.Now,
                JsonSerializer.Serialize(new[] { new SavedDns(AdapterId, "203.0.113.66") })) }));
            var applied = Snapshot();
            var ignored = guarded.Revert(null, true);
            check("a DNS original planted in your own file is not restored",
                  ignored.Changed == 0 && guarded.Records().Count == 0 && Snapshot() == applied &&
                  guarded.DescribeSavedDns(null) == null);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); }

        // Real PC, read only.
        var real = new TweakEngine(tweaks, new TweakContext(new RegistryStore(), new WindowsTweakSystem()),
                                   Path.Combine(work, "unused.json"));
        var states = real.AllStatuses();
        log.Add("This PC (read only): " + string.Join(", ", states.Values.GroupBy(s => s.State)
            .Select(g => $"{g.Key} {g.Count()}")));
        foreach (var t in tweaks)
            log.Add($"  {states[t.Id].State,-11} {t.Risk,-8} {t.Id}: {states[t.Id].Detail}");
        check("no tweak fails to read on this PC", states.Values.All(s => !s.Detail.StartsWith("Could not read")));
        check("reading creates no saved originals", !File.Exists(real.JournalPath));
    }
}
