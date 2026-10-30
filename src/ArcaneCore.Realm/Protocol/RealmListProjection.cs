using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.Kernel.Realms;

namespace ArcaneCore.Realm.Protocol;

/// <summary>What the realm list shows for one realm to one account, and why.</summary>
/// <param name="Flags">The flags to put on the wire.</param>
/// <param name="HeartbeatAge">Realm clock minus the world's last heartbeat; null when the row has none. Zero when the world's clock is ahead.</param>
/// <param name="ClockSkewSuspect">The heartbeat is further in the future than <see cref="RealmListProjection.MaxClockSkew"/>: the two hosts' clocks disagree.</param>
public readonly record struct RealmView(RealmFlags Flags, TimeSpan? HeartbeatAge, bool ClockSkewSuspect);

/// <summary>
/// Turns a realm row (what the world wrote, how old its heartbeat is) and the asking account into the flags the logon daemon sends.
/// Pure: no clock, no I/O, no mutation of the row. The rules, in order (docs/ops/realm-liveness.md):
/// <list type="number">
/// <item><see cref="RealmRuntimeState.Unknown"/>: the stored flags; no heartbeat is enforced.</item>
/// <item><see cref="RealmRuntimeState.Stopped"/>, <see cref="RealmRuntimeState.Starting"/>, or a heartbeat older than
/// <see cref="RealmLivenessOptions.HeartbeatStaleSeconds"/> (or none at all): the stored flags plus Offline.</item>
/// <item><see cref="RealmRuntimeState.Restarting"/> with a fresh heartbeat: plus Invalid (red) or Offline, per <see cref="RealmLivenessOptions.RestartingAs"/>.</item>
/// <item><see cref="RealmRuntimeState.Online"/> and <see cref="RealmRuntimeState.ShuttingDown"/> with a fresh heartbeat: the stored flags.</item>
/// <item>A realm locked above the account's security level: plus Offline, in every state (vmangos AuthSocket.cpp:1088-1090;
/// 1.x clients have no locked display, so the lock is shown as offline).</item>
/// </list>
/// </summary>
public static class RealmListProjection
{
    /// <summary>How far ahead of the realm daemon's clock a heartbeat may be and still count as fresh (clock drift between hosts).</summary>
    public const int MaxClockSkewSeconds = 5;

    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromSeconds(MaxClockSkewSeconds);

    /// <summary>The view of one realm for one account at <paramref name="now"/>.</summary>
    public static RealmView Evaluate(RealmEntry realm, AccountSecurity accountSecurity, DateTimeOffset now, RealmLivenessOptions options)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(options);

        TimeSpan? raw = realm.HeartbeatUtc is { } beat ? now - AsUtc(beat) : null;
        bool skew = raw is { } r && r < -MaxClockSkew;
        // A heartbeat ahead of this clock is drift between hosts, not a dead world: it counts as fresh (age 0) and the caller warns.
        TimeSpan? age = raw is { } a ? (a < TimeSpan.Zero ? TimeSpan.Zero : a) : null;
        RealmFlags flags = realm.Flags;

        if (realm.State != RealmRuntimeState.Unknown)
        {
            bool stale = options.HeartbeatStaleSeconds > 0
                && (age is not { } seen || seen > TimeSpan.FromSeconds(options.HeartbeatStaleSeconds));
            switch (realm.State)
            {
                case RealmRuntimeState.Stopped:
                case RealmRuntimeState.Starting:
                    flags |= RealmFlags.Offline;
                    break;
                case RealmRuntimeState.Restarting:
                    flags |= stale ? RealmFlags.Offline
                        : options.RestartingAs == RestartingRealmDisplay.Offline ? RealmFlags.Offline : RealmFlags.Invalid;
                    break;
                default:
                    if (stale)
                    {
                        flags |= RealmFlags.Offline;
                    }

                    break;
            }
        }

        if (realm.AllowedSecurityLevel > (byte)accountSecurity)
        {
            flags |= RealmFlags.Offline;
        }

        return new RealmView(flags, age, skew);
    }

    /// <summary>
    /// The realm list for one account: a copy of each row with the effective flags. The rows the store returned are not changed
    /// (a store may hand out tracked or cached entities). <paramref name="clockSkewSuspect"/> is true when any heartbeat was ahead of the clock.
    /// </summary>
    public static IReadOnlyList<RealmEntry> Apply(
        IReadOnlyList<RealmEntry> realms, AccountSecurity accountSecurity, DateTimeOffset now, RealmLivenessOptions options, out bool clockSkewSuspect)
    {
        ArgumentNullException.ThrowIfNull(realms);
        clockSkewSuspect = false;
        var shown = new List<RealmEntry>(realms.Count);
        foreach (RealmEntry realm in realms)
        {
            if (realm is null)
            {
                throw new ArgumentException("Realm list cannot contain null entries.", nameof(realms));
            }

            RealmView view = Evaluate(realm, accountSecurity, now, options);
            clockSkewSuspect |= view.ClockSkewSuspect;
            shown.Add(new RealmEntry
            {
                Id = realm.Id,
                Name = realm.Name,
                Address = realm.Address,
                Type = realm.Type,
                Flags = view.Flags,
                Population = realm.Population,
                Category = realm.Category,
                State = realm.State,
                HeartbeatUtc = realm.HeartbeatUtc,
                AllowedSecurityLevel = realm.AllowedSecurityLevel,
            });
        }

        return shown;
    }

    // A column read back as Unspecified (SQLite stores text) was written as UTC.
    private static DateTimeOffset AsUtc(DateTime value)
        => new(value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
