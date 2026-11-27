using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PCOptimizer.Services.ShortcutTools;

public sealed record LinkData(string Target, string Arguments, string WorkingDirectory, string IconPath,
                              int IconIndex, bool Advertised);

/// <summary>
/// Reads and writes .lnk files through the Windows shell. Resolve is never called, so reading cannot
/// change a shortcut or start a search.
/// </summary>
public static class ShellLink
{
    private const int Capacity = 4096;

    private static T Use<T>(string? path, Func<IShellLinkW, T> work)
    {
        var instance = new ShellLinkObject();
        try
        {
            var link = (IShellLinkW)instance;
            if (path != null) ((IPersistFile)link).Load(path, 0);
            return work(link);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    public static LinkData Read(string path) => Use(path, link =>
    {
        var text = new StringBuilder(Capacity);
        string Get(Action<StringBuilder> read)
        {
            text.Clear();
            read(text);
            return text.ToString();
        }
        ((IShellLinkDataList)link).GetFlags(out var flags);
        // The raw form keeps variables such as %windir%; fall back to the stored path.
        var target = Get(b => link.GetPath(b, b.Capacity, IntPtr.Zero, 4));
        if (target.Length == 0) target = Get(b => link.GetPath(b, b.Capacity, IntPtr.Zero, 0));
        var arguments = Get(b => link.GetArguments(b, b.Capacity));
        var directory = Get(b => link.GetWorkingDirectory(b, b.Capacity));
        text.Clear();
        link.GetIconLocation(text, text.Capacity, out var index);
        return new LinkData(target, arguments, directory, text.ToString(), index, (flags & 0x1000) != 0);
    });

    /// <summary>Changes only the given fields; other shortcut properties are kept.</summary>
    public static void Update(string path, string? target = null, string? arguments = null,
                              string? workingDirectory = null, string? iconPath = null) => Use(path, link =>
    {
        LinkData before = ReadFrom(link);
        if (target != null) link.SetPath(target);
        if (arguments != null) link.SetArguments(arguments);
        if (workingDirectory != null) link.SetWorkingDirectory(workingDirectory);
        if (iconPath != null) link.SetIconLocation(iconPath, before.IconIndex);
        ((IPersistFile)link).Save(path, true);
        return true;
    });

    private static LinkData ReadFrom(IShellLinkW link)
    {
        var text = new StringBuilder(Capacity);
        link.GetIconLocation(text, text.Capacity, out var index);
        return new LinkData("", "", "", text.ToString(), index, false);
    }

    /// <summary>Creates a new shortcut (used by the self-test).</summary>
    public static void Create(string path, string target, string arguments = "", string workingDirectory = "") =>
        Use(null, link =>
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(workingDirectory);
            ((IPersistFile)link).Save(path, true);
            return true;
        });

    /// <summary>For Windows Installer shortcuts: true if the product is no longer installed.</summary>
    public static bool? AdvertisedProductMissing(string path)
    {
        var product = new StringBuilder(39);
        if (MsiGetShortcutTarget(path, product, null, null) != 0) return null;
        return ProductMissing(product.ToString());
    }

    public static bool ProductMissing(string productCode) => MsiQueryProductState(productCode) == -1;   // unknown

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetShortcutTargetW")]
    private static extern int MsiGetShortcutTarget(string shortcut, StringBuilder product, StringBuilder? feature,
                                                   StringBuilder? component);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string product);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkObject { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void AddDataBlock(IntPtr block);
        void CopyDataBlock(uint signature, out IntPtr block);
        void RemoveDataBlock(uint signature);
        void GetFlags(out uint flags);
        void SetFlags(uint flags);
    }
}
