using System.Globalization;
using System.Runtime.InteropServices;

namespace ArcaneCore.World.Cluster.Supervision;

/// <summary>
/// The operating system's own reading of the memory available to new processes: <c>GlobalMemoryStatusEx</c> on Windows (<c>ullAvailPhys</c>) and
/// <c>MemAvailable</c> of <c>/proc/meminfo</c> on Linux. Stateless; callable from any thread.
/// </summary>
/// <remarks>
/// Linux note: <c>/proc/meminfo</c> describes the host, not the container. Inside a cgroup with a memory limit (a container, a systemd unit with
/// <c>MemoryMax</c>) the real headroom is <c>memory.max</c> minus <c>memory.current</c> (cgroup v2) and can be far below <c>MemAvailable</c>, so the
/// admission check can admit a child the cgroup then kills. This class does not read the cgroup files; size <c>RequiredMemoryMb</c> for the limit or
/// run the supervisor without one.
/// </remarks>
public static partial class HostMemory
{
    /// <summary>Available physical memory in bytes, or null when this platform has no source here or the call failed.</summary>
    public static long? TryGetAvailableBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) && status.AvailPhys <= long.MaxValue ? (long)status.AvailPhys : null;
            }

            if (OperatingSystem.IsLinux())
                return ParseMemAvailableBytes(File.ReadAllText("/proc/meminfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // No reading from this host: the caller falls back, and says so.
        }

        return null;
    }

    /// <summary>
    /// Reads the <c>MemAvailable:</c> line (kibibytes) of a <c>/proc/meminfo</c> text and returns it in bytes; null when the line is missing or malformed.
    /// </summary>
    public static long? ParseMemAvailableBytes(string meminfo) => ParseMeminfoBytes(meminfo, "MemAvailable:");

    /// <summary>
    /// Reads the <c>MemTotal:</c> line (kibibytes) of a <c>/proc/meminfo</c> text and returns it in bytes; null when the line is missing or malformed.
    /// </summary>
    public static long? ParseMemTotalBytes(string meminfo) => ParseMeminfoBytes(meminfo, "MemTotal:");

    /// <summary>
    /// Total physical memory of the host in bytes (the same host-wide scope as <see cref="TryGetAvailableBytes"/>: <c>ullTotalPhys</c> on Windows, <c>MemTotal</c> of
    /// <c>/proc/meminfo</c> on Linux), or null when this platform has no source here or the call failed.
    /// </summary>
    public static long? TryGetTotalBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                return GlobalMemoryStatusEx(ref status) && status.TotalPhys <= long.MaxValue ? (long)status.TotalPhys : null;
            }

            if (OperatingSystem.IsLinux())
                return ParseMemTotalBytes(File.ReadAllText("/proc/meminfo"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // No reading from this host: the caller falls back, and says so.
        }

        return null;
    }

    private static long? ParseMeminfoBytes(string meminfo, string key)
    {
        ArgumentNullException.ThrowIfNull(meminfo);
        foreach (string raw in meminfo.Split('\n'))
        {
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            if (!line.StartsWith(key, StringComparison.Ordinal))
                continue;

            ReadOnlySpan<char> rest = line[key.Length..].Trim();
            int space = rest.IndexOf(' ');
            ReadOnlySpan<char> number = space < 0 ? rest : rest[..space];
            if (long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out long kib) && kib >= 0 && kib <= long.MaxValue / 1024)
                return kib * 1024;

            return null;
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
