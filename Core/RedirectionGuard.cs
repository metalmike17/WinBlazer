using System.Runtime.InteropServices;

namespace PCOptimizer.Core;

/// <summary>
/// Windows' redirection protection: this process no longer follows folder links (junctions) that were made
/// without administrator rights. Without it, a folder swapped for a link after a safety check could send an
/// administrator's delete or write somewhere else. It cannot be turned off again, and it does not carry over
/// to programs this one starts.
/// </summary>
public static class RedirectionGuard
{
    private const int RedirectionTrustPolicy = 16;
    private const uint Enforce = 1;

    /// <summary>True only if Windows reports that the protection is now on.</summary>
    public static bool TryEnable()
    {
        var flags = Enforce;
        return SetProcessMitigationPolicy(RedirectionTrustPolicy, ref flags, sizeof(uint)) && IsOn;
    }

    public static bool IsOn =>
        GetProcessMitigationPolicy(GetCurrentProcess(), RedirectionTrustPolicy, out var flags, sizeof(uint)) &&
        (flags & Enforce) != 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint flags, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessMitigationPolicy(IntPtr process, int policy, out uint flags, nuint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
