using System.Numerics;
using ArcaneCore.Game.ClientErrors;

namespace ArcaneCore.World.Net;

/// <summary>
/// Cross-account rate check behind <c>client/rejection-spike</c>: one (kind, reason) refusal sent to at least <see cref="MinAccounts"/> different accounts and
/// at least <see cref="Threshold"/> times within one <see cref="WindowMs"/> window means the server refuses something it should not (a broken handler or content),
/// not that one client misbehaves. One report per spike; it re-arms after a window in which the pair stayed below the threshold.
/// <para>
/// State: a <c>long[kinds x 256]</c> count and a <c>ulong[kinds x 256]</c> account-hash bitset (account id hashed to one of 64 bits, so the popcount is a lower
/// bound of the distinct accounts), plus an armed flag per pair. Reasons above 255 are not tracked (no decoded reason is that large). Windows are tumbling and
/// aligned to the first note after the previous window ended.
/// </para>
/// <para>
/// Threading: <see cref="Note"/> is called from any session thread. The per-refusal cost is one clock read, two <see cref="Interlocked"/> operations and a
/// compare-exchange only when a spike fires; it never allocates. A window rollover takes a lock and resets the arrays; a note racing the rollover may be counted in either
/// window, which can only delay or advance a report by a few refusals.
/// </para>
/// </summary>
internal sealed class ClientRejectionRateTracker(Func<long> clockMs)
{
    /// <summary>Length of the counting window.</summary>
    internal const long WindowMs = 5 * 60 * 1000;

    /// <summary>Refusals of one (kind, reason) in a window that make a spike (together with <see cref="MinAccounts"/>).</summary>
    internal const int Threshold = 300;

    /// <summary>Distinct account-hash bits that must be set for a spike.</summary>
    internal const int MinAccounts = 8;

    private static readonly int KindCount = Enum.GetValues<ClientRejectionKind>().Length;

    private readonly long[] _counts = new long[KindCount << 8];
    private readonly ulong[] _accounts = new ulong[KindCount << 8];
    private readonly int[] _fired = new int[KindCount << 8];
    private readonly object _rollGate = new();
    private long _windowStart = long.MinValue;

    /// <summary>
    /// Count one refusal sent to <paramref name="accountId"/>. True exactly when this refusal makes the spike (count at least <see cref="Threshold"/> from at
    /// least <see cref="MinAccounts"/> accounts, and not yet reported in this spike); <paramref name="count"/> and <paramref name="accounts"/> are then the window's totals.
    /// </summary>
    internal bool Note(ClientRejectionKind kind, uint reason, uint accountId, out long count, out int accounts)
    {
        count = 0;
        accounts = 0;
        if (reason > 0xFF)
        {
            return false;
        }

        long now = clockMs();
        long start = Volatile.Read(ref _windowStart);
        if (start == long.MinValue || now - start >= WindowMs)
        {
            Roll(now);
        }

        int slot = ((int)kind << 8) | (int)reason;
        ulong bit = 1UL << (int)(unchecked(accountId * 0x9E3779B1u) >> 26);
        ulong bits = Interlocked.Or(ref _accounts[slot], bit) | bit;
        count = Interlocked.Increment(ref _counts[slot]);
        accounts = BitOperations.PopCount(bits);
        return count >= Threshold && accounts >= MinAccounts && Interlocked.CompareExchange(ref _fired[slot], 1, 0) == 0;
    }

    /// <summary>
    /// Start a new window at <paramref name="now"/>. A pair stays reported only when the window that just ended was still over the threshold and no whole window
    /// passed without a note; any other pair re-arms.
    /// </summary>
    private void Roll(long now)
    {
        lock (_rollGate)
        {
            long start = Volatile.Read(ref _windowStart);
            if (start != long.MinValue && now - start < WindowMs)
            {
                return; // another thread rolled first
            }

            bool consecutive = start != long.MinValue && now - start < 2 * WindowMs;
            for (int slot = 0; slot < _counts.Length; slot++)
            {
                bool stillNoisy = consecutive && _counts[slot] >= Threshold;
                if (!stillNoisy)
                {
                    Volatile.Write(ref _fired[slot], 0);
                }

                Volatile.Write(ref _counts[slot], 0);
                Volatile.Write(ref _accounts[slot], 0);
            }

            Volatile.Write(ref _windowStart, now);
        }
    }
}
