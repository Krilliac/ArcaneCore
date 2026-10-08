using ArcaneCore.Kernel.Accounts;

namespace ArcaneCore.Realm.Net;

/// <summary>
/// The logon daemon's IP-ban list, read from <c>ip_banned</c> once per <see cref="RefreshEvery"/> instead of once per
/// logon challenge. vmangos realmd queries the table for every challenge (AuthSocket.cpp:338-352), so a client that
/// keeps reconnecting costs one database round trip per connection; mangosd keeps exactly this kind of list in
/// memory (AccountMgr::LoadIPBans / IsIPBanned, AccountMgr.cpp:340-367, 412-417) and reloads it every
/// BanListReloadTimer seconds (World.cpp:697, 60). The cache follows mangosd: the whole active list is loaded, each
/// entry keeps its unban date so a temporary ban still ends on time between reloads, and a reload happens on the first
/// challenge after the period has passed (one caller reloads, the others wait for it).
/// <para>
/// A ban written after the last reload reaches the logon daemon within one period; the world daemon reads the rows
/// directly at world authentication (WorldSession, AUTH_BANNED), so such an address is still refused before it can
/// enter the world. An unban (or a ban shortened) written elsewhere also takes up to one period to let the address
/// back in at the logon screen. <see cref="RefreshEvery"/> of zero (or less) turns the cache off: every challenge reads
/// the row, as vmangos realmd does. Thread-safe; one instance per logon listener.
/// </para>
/// <para>
/// A reload that fails (a slow or large table that runs out of the store's query budget, an outage) never turns into a
/// realm-wide logon outage on its own: that challenge, and every challenge until one period after the failure, falls
/// back to the single-row read vmangos realmd does (<see cref="IBanStore.GetActiveIpBanAsync"/>); then a reload is tried
/// again. Fail closed only when that row read fails too (the error propagates and the connection is closed, as with
/// the direct read). A cancelled caller is not a failed reload.
/// </para>
/// </summary>
public sealed class RealmIpBanCache(TimeSpan refreshEvery, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private volatile Snapshot? _snapshot;

    /// <summary>How long a loaded list is used before the next challenge reloads it; zero or less means no cache.</summary>
    public TimeSpan RefreshEvery { get; } = refreshEvery;

    /// <summary>How many times the list was (re)loaded from the store; for tests and diagnostics.</summary>
    public int Reloads { get; private set; }

    /// <summary>How many reloads failed; for tests and diagnostics.</summary>
    public int FailedReloads { get; private set; }

    /// <summary>How many checks were answered by the single-row read because the list could not be loaded.</summary>
    public int RowReadFallbacks => Volatile.Read(ref _rowReadFallbacks);

    private int _rowReadFallbacks;
    private DateTimeOffset? _failedAt;

    /// <summary>Drop the loaded list, so the next challenge reads the store again (for example after a <c>.ban ip</c> in this process).</summary>
    public void Invalidate() => _snapshot = null;

    /// <summary>
    /// Whether <paramref name="address"/> (normalised, <see cref="AccountBanEvaluator.NormalizeIp"/>) has an IP ban in force
    /// (<see cref="AccountBanEvaluator.IsActive(IpBanRecord, long)"/>). A store error propagates (fail closed).
    /// </summary>
    public async ValueTask<bool> IsBannedAsync(string address, IBanStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(store);
        if (RefreshEvery <= TimeSpan.Zero)
        {
            return await store.GetActiveIpBanAsync(address, cancellationToken).ConfigureAwait(false) is not null;
        }

        Snapshot? snapshot;
        try
        {
            snapshot = await CurrentAsync(store, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            snapshot = null; // the reload failed: answer this challenge the vmangos way
        }

        if (snapshot is null)
        {
            Interlocked.Increment(ref _rowReadFallbacks);
            return await store.GetActiveIpBanAsync(address, cancellationToken).ConfigureAwait(false) is not null;
        }

        return snapshot.Bans.TryGetValue(address, out IpBanRecord? ban)
            && AccountBanEvaluator.IsActive(ban, _clock.GetUtcNow().ToUnixTimeSeconds());
    }

    /// <summary>The loaded list, reloaded when stale; null while a failed reload is less than one period old.</summary>
    private async ValueTask<Snapshot?> CurrentAsync(IBanStore store, CancellationToken cancellationToken)
    {
        Snapshot? snapshot = _snapshot;
        if (snapshot is not null && !IsStale(snapshot))
        {
            return snapshot;
        }

        await _reload.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _snapshot;
            if (snapshot is not null && !IsStale(snapshot))
            {
                return snapshot; // another challenge reloaded it while this one waited
            }

            DateTimeOffset loadedAt = _clock.GetUtcNow();
            if (_failedAt is { } failedAt && loadedAt - failedAt < RefreshEvery)
            {
                return null; // a reload failed recently: do not retry the whole list on every challenge
            }

            IReadOnlyList<IpBanRecord> rows;
            try
            {
                rows = await store.ListIpBansAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                _failedAt = loadedAt;
                FailedReloads++;
                throw;
            }

            _failedAt = null;
            var bans = new Dictionary<string, IpBanRecord>(StringComparer.Ordinal);
            long now = loadedAt.ToUnixTimeSeconds();
            foreach (IpBanRecord row in rows)
            {
                // The stored spelling may differ from the canonical one a session reports (see BanRecheckFeature).
                string key = AccountBanEvaluator.NormalizeIp(row.Ip) ?? row.Ip.Trim();
                if (AccountBanEvaluator.IsActive(row, now)
                    && (!bans.TryGetValue(key, out IpBanRecord? kept) || Outlasts(row, kept)))
                {
                    bans[key] = row;
                }
            }

            snapshot = new Snapshot(bans, loadedAt);
            _snapshot = snapshot;
            Reloads++;
            return snapshot;
        }
        finally
        {
            _reload.Release();
        }
    }

    private bool IsStale(Snapshot snapshot) => _clock.GetUtcNow() - snapshot.LoadedAt >= RefreshEvery;

    /// <summary>Of two rows for one address, the one that ends later wins (a permanent row outlasts every other).</summary>
    private static bool Outlasts(IpBanRecord candidate, IpBanRecord kept)
        => !kept.IsPermanent && (candidate.IsPermanent || candidate.UnbanDate > kept.UnbanDate);

    private sealed record Snapshot(Dictionary<string, IpBanRecord> Bans, DateTimeOffset LoadedAt);
}
