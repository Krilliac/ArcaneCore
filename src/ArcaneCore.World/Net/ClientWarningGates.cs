using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ArcaneCore.Kernel.Logging;

namespace ArcaneCore.World.Net;

/// <summary>
/// One <see cref="LogGate"/> per session and warning kind for warnings a client can trigger at will (a mover mismatch, a login for
/// someone else's character, a bad account data type): one line per interval, with the count of the suppressed ones, instead of one
/// line per packet. The gates live as long as their session.
/// </summary>
internal static class ClientWarningGates
{
    /// <summary>Interval between two admitted lines of one kind on one session.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly ConditionalWeakTable<WorldSession, ConcurrentDictionary<string, LogGate>> Gates = new();

    /// <summary>True when the warning <paramref name="kind"/> may be logged for <paramref name="session"/> now.</summary>
    public static bool TryEnter(WorldSession session, string kind, out int suppressed)
        => Gates.GetValue(session, static _ => new ConcurrentDictionary<string, LogGate>(StringComparer.Ordinal))
            .GetOrAdd(kind, static _ => new LogGate(Interval))
            .TryEnter(out suppressed);
}
