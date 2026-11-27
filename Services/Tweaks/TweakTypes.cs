using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PCOptimizer.Services.RegistryTools;

namespace PCOptimizer.Services.Tweaks;

public enum TweakCategory { Performance, Network, Power, Visual, Privacy }
public enum TweakRisk { Safe, Moderate, Risky }
public enum TweakState { Off, Applied, Partial, Unavailable }

public sealed record TweakStatus(TweakState State, string Detail);

/// <summary>One reversible change. The saved original is a string that only the same tweak reads.</summary>
public abstract class Tweak
{
    public required string Id { get; init; }
    public required TweakCategory Category { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required TweakRisk Risk { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>When the change takes effect, if not at once.</summary>
    public string? Effect { get; init; }

    public abstract TweakStatus Read(TweakContext context);
    public abstract string Capture(TweakContext context);
    public abstract void Apply(TweakContext context);
    /// <summary>Restores only what still holds the value this tweak set.</summary>
    public abstract void Revert(TweakContext context, string original);

    protected static T Parse<T>(string original) => JsonSerializer.Deserialize<T>(original)
        ?? throw new InvalidDataException("The saved original is empty.");
}

public sealed record TweakContext(RegistryStore Registry, ITweakSystem System);

/// <summary>A number is stored as a DWORD, a string as text.</summary>
public sealed record RegistryValue(Hive Hive, string Path, string Name, object Value);

public sealed record SavedValue(Hive Hive, string Path, string Name, int? Type, byte[]? Data);

public class RegistryTweak : Tweak
{
    public IReadOnlyList<RegistryValue> Values { get; init; } = Array.Empty<RegistryValue>();

    protected virtual IReadOnlyList<RegistryValue> Targets(TweakContext context) => Values;

    /// <summary>The definition for a saved entry, or null if this tweak never writes there.</summary>
    protected virtual RegistryValue? Definition(SavedValue saved) => Values.FirstOrDefault(v =>
        v.Hive == saved.Hive && v.Path == saved.Path && v.Name == saved.Name);

    private static RawValue? Current(TweakContext context, Hive hive, string path, string name)
    {
        using var key = context.Registry.Open(hive, path);
        return key == null ? null : RegistryStore.ReadValue(key, name);
    }

    private static bool Matches(RawValue? current, object wanted) => wanted switch
    {
        int number => current is { Type: 4, Data.Length: 4 } && BitConverter.ToInt32(current.Data) == number,
        string text => current is { Type: 1 or 2 } &&
                       System.Text.Encoding.Unicode.GetString(current.Data).TrimEnd('\0') == text,
        _ => false
    };

    private static string Show(RawValue? value) => value switch
    {
        null => "not set",
        { Type: 4, Data.Length: 4 } => unchecked((uint)BitConverter.ToInt32(value.Data)).ToString(),
        { Type: 1 or 2 } => "\"" + System.Text.Encoding.Unicode.GetString(value.Data).TrimEnd('\0') + "\"",
        _ => "set"
    };

    public override TweakStatus Read(TweakContext context)
    {
        var targets = Targets(context);
        if (targets.Count == 0) return new(TweakState.Unavailable, "Nothing on this PC to change.");
        var matched = 0;
        var shown = new List<string>();
        foreach (var target in targets)
        {
            var current = Current(context, target.Hive, target.Path, target.Name);
            if (Matches(current, target.Value)) matched++;
            shown.Add($"{target.Name} = {Show(current)}");
        }
        var state = matched == targets.Count ? TweakState.Applied : matched == 0 ? TweakState.Off : TweakState.Partial;
        return new(state, string.Join(", ", shown.Distinct().Take(3)) + (shown.Count > 3 ? ", ..." : ""));
    }

    public override string Capture(TweakContext context) => JsonSerializer.Serialize(Targets(context).Select(t =>
    {
        var current = Current(context, t.Hive, t.Path, t.Name);
        return new SavedValue(t.Hive, t.Path, t.Name, current?.Type, current?.Data);
    }).ToList());

    public override void Apply(TweakContext context)
    {
        foreach (var target in Targets(context))
        {
            using var key = context.Registry.Create(target.Hive, target.Path);
            if (target.Value is int number) key.SetValue(target.Name, number, RegistryValueKind.DWord);
            else key.SetValue(target.Name, (string)target.Value, RegistryValueKind.String);
        }
    }

    public override void Revert(TweakContext context, string original)
    {
        var saved = Parse<List<SavedValue>>(original);
        // Check every entry before writing any: the saved file must not name other locations.
        if (saved.Any(s => Definition(s) == null))
            throw new InvalidDataException("The saved original names a location this tweak does not use.");
        foreach (var item in saved)
        {
            var wanted = Definition(item)!.Value;
            if (!Matches(Current(context, item.Hive, item.Path, item.Name), wanted)) continue;   // changed since
            using var key = context.Registry.Create(item.Hive, item.Path);
            if (item is { Type: { } type, Data: { } data })
                RegistryStore.WriteValue(key, new RawValue(item.Name, type, data));
            else key.DeleteValue(item.Name, throwOnMissingValue: false);
        }
    }
}

/// <summary>Per-adapter TCP values for the adapters in use.</summary>
public sealed class NagleTweak : RegistryTweak
{
    private const string Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\";
    private static readonly string[] Names = { "TcpAckFrequency", "TCPNoDelay" };

    protected override IReadOnlyList<RegistryValue> Targets(TweakContext context) =>
        context.System.ActiveAdapters().SelectMany(a => Names.Select(n =>
            new RegistryValue(Hive.LocalMachine, Interfaces + a.Id, n, 1))).ToList();

    protected override RegistryValue? Definition(SavedValue saved) =>
        saved.Hive == Hive.LocalMachine && Names.Contains(saved.Name) && saved.Path.StartsWith(Interfaces) &&
        Regex.IsMatch(saved.Path[Interfaces.Length..], @"^\{[0-9A-Fa-f\-]{36}\}$")
            ? new RegistryValue(saved.Hive, saved.Path, saved.Name, 1) : null;
}

public sealed record ServiceStart(int Start, bool Delayed);

/// <summary>Disables a service's automatic start. The running service is left until restart.</summary>
public sealed class ServiceTweak : Tweak
{
    public required string Service { get; init; }
    private string KeyPath => @"SYSTEM\CurrentControlSet\Services\" + Service;

    private ServiceStart? Current(TweakContext context)
    {
        using var key = context.Registry.Open(Hive.LocalMachine, KeyPath);
        return key?.GetValue("Start") is int start
            ? new ServiceStart(start, key.GetValue("DelayedAutostart") is 1) : null;
    }

    public override TweakStatus Read(TweakContext context) => Current(context) switch
    {
        null => new(TweakState.Unavailable, "The service is not installed."),
        { Start: 4 } => new(TweakState.Applied, "Service start: disabled"),
        { } s => new(TweakState.Off, "Service start: " +
            (s.Start == 2 ? s.Delayed ? "automatic (delayed)" : "automatic" : s.Start == 3 ? "manual" : $"type {s.Start}"))
    };

    public override string Capture(TweakContext context) => JsonSerializer.Serialize(
        Current(context) ?? throw new IOException("The service is not installed."));

    public override void Apply(TweakContext context) => context.System.SetServiceStart(Service, new(4, false));

    public override void Revert(TweakContext context, string original)
    {
        var saved = Parse<ServiceStart>(original);
        if (saved.Start is < 2 or > 4) throw new InvalidDataException("The saved service start type is not valid.");
        if (Current(context)?.Start == 4) context.System.SetServiceStart(Service, saved);
    }
}

public sealed record SavedPower(Guid Plan, uint Value);

/// <summary>A plugged-in (AC) value in the active power plan.</summary>
public sealed class PowerTweak : Tweak
{
    public required Guid Group { get; init; }
    public required Guid Setting { get; init; }
    public required uint Target { get; init; }

    public override TweakStatus Read(TweakContext context)
    {
        var value = context.System.ReadPower(context.System.ActivePlan(), Group, Setting);
        return value == null ? new(TweakState.Unavailable, "This power plan does not offer the setting.")
            : new(value == Target ? TweakState.Applied : TweakState.Off, $"Current plan, plugged in: value {value}");
    }

    public override string Capture(TweakContext context)
    {
        var plan = context.System.ActivePlan();
        var value = context.System.ReadPower(plan, Group, Setting)
                    ?? throw new IOException("The power setting is not available.");
        return JsonSerializer.Serialize(new SavedPower(plan, value));
    }

    public override void Apply(TweakContext context) =>
        context.System.WritePower(context.System.ActivePlan(), Group, Setting, Target);

    public override void Revert(TweakContext context, string original)
    {
        var saved = Parse<SavedPower>(original);
        if (context.System.ReadPower(saved.Plan, Group, Setting) == Target)
            context.System.WritePower(saved.Plan, Group, Setting, saved.Value);
    }
}

public sealed class HibernateTweak : Tweak
{
    private static bool? Enabled(TweakContext context)
    {
        using var key = context.Registry.Open(Hive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power");
        return key?.GetValue("HibernateEnabled") is int value ? value != 0 : null;
    }

    public override TweakStatus Read(TweakContext context) => Enabled(context) switch
    {
        null => new(TweakState.Unavailable, "Hibernation state could not be read."),
        false => new(TweakState.Applied, "Hibernation is off"),
        true => new(TweakState.Off, "Hibernation is on")
    };

    public override string Capture(TweakContext context) => JsonSerializer.Serialize(
        Enabled(context) ?? throw new IOException("Hibernation state could not be read."));

    public override void Apply(TweakContext context) => context.System.SetHibernation(false);

    public override void Revert(TweakContext context, string original)
    {
        if (Parse<bool>(original) && Enabled(context) == false) context.System.SetHibernation(true);
    }
}

public sealed record SavedDns(string AdapterId, string Servers);

/// <summary>IPv4 DNS servers of the physical adapters in use.</summary>
public sealed class DnsTweak : Tweak
{
    public static readonly string[] Cloudflare = { "1.1.1.1", "1.0.0.1" };

    private static string Configured(TweakContext context, string adapterId)
    {
        using var key = context.Registry.Open(Hive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + adapterId);
        return key?.GetValue("NameServer") as string ?? "";
    }

    private static string[] Split(string servers) =>
        servers.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

    public override TweakStatus Read(TweakContext context)
    {
        var adapters = context.System.ActiveAdapters();
        if (adapters.Count == 0) return new(TweakState.Unavailable, "No physical network connection found.");
        var matched = adapters.Count(a => Split(Configured(context, a.Id)).SequenceEqual(Cloudflare));
        var detail = string.Join("; ", adapters.Select(a =>
            $"{a.Name}: {(Configured(context, a.Id) is { Length: > 0 } s ? s : "automatic")}"));
        return new(matched == adapters.Count ? TweakState.Applied : matched == 0 ? TweakState.Off : TweakState.Partial,
                   detail);
    }

    public override string Capture(TweakContext context) => JsonSerializer.Serialize(
        context.System.ActiveAdapters().Select(a => new SavedDns(a.Id, Configured(context, a.Id))).ToList());

    /// <summary>One readable line per adapter in a saved original.</summary>
    public static string Describe(string original)
    {
        try
        {
            return string.Join("\n", Parse<List<SavedDns>>(original).Select(s =>
                $"Adapter {s.AdapterId}: {(s.Servers.Length == 0 ? "automatic" : s.Servers)}"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NullReferenceException)
        { return "The saved addresses could not be read."; }
    }

    public override void Apply(TweakContext context)
    {
        foreach (var adapter in context.System.ActiveAdapters()) context.System.SetDns(adapter.Id, Cloudflare);
    }

    public override void Revert(TweakContext context, string original)
    {
        var saved = Parse<List<SavedDns>>(original);
        if (saved.Any(s => !Guid.TryParse(s.AdapterId, out _) ||
                           Split(s.Servers).Any(x => !System.Net.IPAddress.TryParse(x, out _))))
            throw new InvalidDataException("The saved DNS settings are not valid.");
        foreach (var item in saved)
            if (Split(Configured(context, item.AdapterId)).SequenceEqual(Cloudflare))
                context.System.SetDns(item.AdapterId, Split(item.Servers));
    }
}
