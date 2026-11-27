using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PCOptimizer.Services.RegistryTools;

public enum Hive { CurrentUser, LocalMachine }

/// <summary>A value exactly as stored: its type number and raw bytes.</summary>
public sealed record RawValue(string Name, int Type, byte[] Data)
{
    public bool SameAs(RawValue other) => Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);
}

public sealed record KeyData(Hive Hive, string Path, IReadOnlyList<RawValue> Values);

/// <summary>
/// All registry access goes through here. 32-bit locations are written out with WOW6432Node, so the
/// 64-bit view is always used. Tests redirect both hives under a throwaway key.
/// </summary>
public sealed class RegistryStore
{
    private readonly string? _testRoot;
    public RegistryStore(string? testRoot = null) => _testRoot = testRoot;

    public static string HiveName(Hive hive) => hive == Hive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";

    private string Physical(Hive hive, string path) => _testRoot == null ? path : $@"{_testRoot}\{hive}\{path}";

    public RegistryKey? Open(Hive hive, string path, bool writable = false)
    {
        var real = _testRoot != null ? Hive.CurrentUser : hive;
        using var root = RegistryKey.OpenBaseKey(
            real == Hive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        return root.OpenSubKey(Physical(hive, path), writable);
    }

    public RegistryKey Create(Hive hive, string path)
    {
        var real = _testRoot != null ? Hive.CurrentUser : hive;
        using var root = RegistryKey.OpenBaseKey(
            real == Hive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
        return root.CreateSubKey(Physical(hive, path), writable: true);
    }

    public static RawValue? ReadValue(RegistryKey key, string name)
    {
        var size = 0;
        var status = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out var type, null, ref size);
        if (status == 2) return null;   // not found
        if (status != 0) throw new IOException($"Could not read a registry value (error {status}).");
        var data = new byte[size];
        if (size > 0)
        {
            status = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, data, ref size);
            if (status != 0) throw new IOException($"Could not read a registry value (error {status}).");
            if (size != data.Length) Array.Resize(ref data, size);
        }
        return new RawValue(name, type, data);
    }

    public static void WriteValue(RegistryKey key, RawValue value)
    {
        var status = RegSetValueEx(key.Handle, value.Name, 0, value.Type, value.Data, value.Data.Length);
        if (status != 0) throw new IOException($"Could not write a registry value (error {status}).");
    }

    /// <summary>
    /// Reads a whole key and everything under it. Throws if any part cannot be read, because a partial
    /// backup must never be treated as a backup.
    /// </summary>
    public List<KeyData> Capture(Hive hive, string path)
    {
        var result = new List<KeyData>();
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            using var key = Open(hive, current) ?? throw new IOException("A registry key no longer exists.");
            var values = key.GetValueNames()
                .Select(n => ReadValue(key, n) ?? throw new IOException("A registry value changed while reading."))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
            // Registry links point somewhere else; deleting through one is not safe.
            if (values.Any(v => v.Type == 6)) throw new IOException("The key contains a registry link.");
            result.Add(new KeyData(hive, current, values));
            foreach (var child in key.GetSubKeyNames().OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                pending.Push(current + "\\" + child);
        }
        return result;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out int type,
                                              byte[]? data, ref int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    private static extern int RegSetValueEx(SafeRegistryHandle key, string name, int reserved, int type,
                                            byte[] data, int size);
}
