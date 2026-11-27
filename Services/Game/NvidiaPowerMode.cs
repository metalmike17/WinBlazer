using System.Runtime.InteropServices;

namespace PCOptimizer.Services.GameTools;

/// <summary>Value is null when the global profile has no setting of its own (NVIDIA's default applies).</summary>
public sealed record GpuPowerMode(uint? Value);

/// <summary>
/// The "Power management mode" global setting in NVIDIA Control Panel, through NVAPI's driver settings
/// interface. Changes the driver profile only; no clocks or voltages are touched.
/// </summary>
public static class NvidiaPowerMode
{
    public const uint PreferMaximum = 1;
    private const uint SettingId = 0x1057EB71;
    private const int SettingNotFound = -160, InsufficientRights = -137;

    // NVDRS_SETTING version 1: layout and offsets in bytes.
    private const int Size = 12320, Version = Size | (1 << 16);
    private const int IdAt = 4100, TypeAt = 4104, LocationAt = 4108, IsDefaultAt = 4112, ValueAt = 8220;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArguments();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSession(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OneHandle(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetProfile(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting(IntPtr session, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSetting(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteSetting(IntPtr session, IntPtr profile, uint id);

    private static T Function<T>(uint id) where T : Delegate
    {
        var pointer = QueryInterface(id);
        if (pointer == IntPtr.Zero) throw new IOException("This NVIDIA driver does not offer the required function.");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static void Ok(int status, string step)
    {
        if (status == 0) return;
        throw new IOException(status == InsufficientRights
            ? "NVIDIA requires administrator rights for this change."
            : $"NVIDIA {step} failed (code {status}).");
    }

    private static T WithProfile<T>(Func<IntPtr, IntPtr, T> work)
    {
        try
        {
            Ok(Function<NoArguments>(0x0150E828)(), "start-up");
            Ok(Function<CreateSession>(0x0694D52E)(out var session), "session");
            try
            {
                Ok(Function<OneHandle>(0x375DBD6B)(session), "settings load");
                Ok(Function<GetProfile>(0xDA8466A0)(session, out var profile), "global profile");
                return work(session, profile);
            }
            finally { Function<OneHandle>(0xDAD9CFF8)(session); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException("No NVIDIA driver was found. Only NVIDIA GPUs are supported.");
        }
    }

    private static GpuPowerMode ReadFrom(IntPtr session, IntPtr profile)
    {
        var buffer = Marshal.AllocHGlobal(Size);
        try
        {
            Marshal.Copy(new byte[Size], 0, buffer, Size);
            Marshal.WriteInt32(buffer, 0, Version);
            var status = Function<GetSetting>(0x73BF8338)(session, profile, SettingId, buffer);
            if (status == SettingNotFound) return new(null);
            Ok(status, "read");
            // Location 0 means this profile; "default" means nobody chose it.
            var own = Marshal.ReadInt32(buffer, LocationAt) == 0 && Marshal.ReadInt32(buffer, IsDefaultAt) == 0;
            return new(own ? (uint)Marshal.ReadInt32(buffer, ValueAt) : null);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static GpuPowerMode Read() => WithProfile(ReadFrom);

    /// <summary>A null value removes the global profile's own setting.</summary>
    public static void Write(uint? value) => WithProfile((session, profile) =>
    {
        if (value is { } wanted)
        {
            var buffer = Marshal.AllocHGlobal(Size);
            try
            {
                Marshal.Copy(new byte[Size], 0, buffer, Size);
                Marshal.WriteInt32(buffer, 0, Version);
                Marshal.WriteInt32(buffer, IdAt, unchecked((int)SettingId));
                Marshal.WriteInt32(buffer, TypeAt, 0);   // 32-bit number
                Marshal.WriteInt32(buffer, ValueAt, unchecked((int)wanted));
                Ok(Function<SetSetting>(0x577DD202)(session, profile, buffer), "change");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        else
        {
            var status = Function<DeleteSetting>(0xE4A26362)(session, profile, SettingId);
            if (status != SettingNotFound) Ok(status, "reset");
        }
        Ok(Function<OneHandle>(0xFCBC7E14)(session), "save");
        return true;
    });
}
