using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.World.Cluster.Social;

/// <summary>What the world knows about an online character that realm presence mirrors (a plain value, built on the world thread from the player).</summary>
public readonly record struct PresenceSnapshot(
    ulong CharacterGuid,
    string CharacterName,
    int AccountId,
    long SessionGeneration,
    uint MapId,
    uint ZoneId,
    uint AreaId,
    byte Level,
    byte Class,
    byte Race,
    byte Gender,
    int GuildId,
    string? GuildName,
    int GroupId,
    bool IsAfk,
    bool IsDnd);

/// <summary>
/// Feeds this worker's online characters into realm presence (M15.2) without ever blocking the world thread: <see cref="PublishLogin"/>, <see cref="PublishUpdate"/> and
/// <see cref="PublishLogout"/> only record the latest wish per character (at most one pending operation per character, so memory is bounded by the online count);
/// <see cref="FlushAsync"/> (run every interval by <see cref="Start"/>, or directly by a test) sends them to the <see cref="IPresenceAuthority"/> in character order, then drains
/// the kicks queued for this worker and calls <c>kickAccount</c> for each one that still matches a registration of this worker.
/// <para>
/// A transport failure (link lost, timeout, no holder) keeps the unsent operations and retries on the next flush. A rejection means a newer registration of the character on
/// another worker won: the local record is dropped and nothing is retried. Versions come from a per-publisher monotonic counter seeded from the clock and are raised above
/// what the authority reports, so an update is never lost to the authority's version fence.
/// </para>
/// <para>Thread-safety: the Publish members take a short lock and are safe from any thread; <see cref="FlushAsync"/> calls are serialised. Disposal stops the loop.</para>
/// </summary>
public sealed class ClusterPresencePublisher : IAsyncDisposable
{
    private enum Kind
    {
        Login,
        Update,
        Logout,
    }

    private readonly record struct Pending(Kind Kind, PresenceSnapshot Snapshot);

    private sealed class Known(int accountId, PresenceSnapshot snapshot, long generation, long version)
    {
        public int AccountId { get; } = accountId;
        public PresenceSnapshot Snapshot { get; set; } = snapshot;
        public long Generation { get; set; } = generation;
        public long Version { get; set; } = version;
    }

    private readonly IPresenceAuthority _authority;
    private readonly string _workerId;
    private readonly string _gatewayId;
    private readonly Action<int> _kickAccount;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly Dictionary<ulong, Pending> _pending = [];
    private readonly Dictionary<ulong, Known> _known = [];
    private readonly CancellationTokenSource _stop = new();
    private long _lastVersion;
    private Task _loop = Task.CompletedTask;
    private int _started;
    private int _disposed;

    /// <param name="authority">Where presence is written (the in-process holder or the peer link).</param>
    /// <param name="workerId">This worker's id (<see cref="RealmPresenceEntry.WorkerId"/>).</param>
    /// <param name="gatewayId">The gateway id stamped on entries (<see cref="RealmPresenceEntry.GatewayId"/>).</param>
    /// <param name="kickAccount">Disconnects this node's session of an account (<c>SessionRegistry.KickSuperseded</c>); called on the flush thread.</param>
    public ClusterPresencePublisher(IPresenceAuthority authority, string workerId, string gatewayId, Action<int> kickAccount, ILogger? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayId);
        ArgumentNullException.ThrowIfNull(kickAccount);
        _authority = authority;
        _workerId = workerId;
        _gatewayId = gatewayId;
        _kickAccount = kickAccount;
        _logger = logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _lastVersion = _time.GetUtcNow().UtcTicks;
    }

    /// <summary>Characters this publisher has registered and not yet removed.</summary>
    public int RegisteredCount
    {
        get
        {
            lock (_gate)
            {
                return _known.Count;
            }
        }
    }

    /// <summary>Operations waiting for the next flush.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>A character entered the world (a snapshot with a non-positive generation, such as a test double or a synthetic session, is ignored).</summary>
    public void PublishLogin(in PresenceSnapshot snapshot)
    {
        if (snapshot.CharacterGuid == 0 || snapshot.SessionGeneration <= 0 || string.IsNullOrWhiteSpace(snapshot.CharacterName))
        {
            return;
        }

        lock (_gate)
        {
            _pending[snapshot.CharacterGuid] = new Pending(Kind.Login, snapshot);
        }
    }

    /// <summary>The character's published fields changed (map, zone, level, guild, group, status). Ignored for a character this publisher does not track.</summary>
    public void PublishUpdate(in PresenceSnapshot snapshot)
    {
        lock (_gate)
        {
            ulong guid = snapshot.CharacterGuid;
            if (_pending.TryGetValue(guid, out Pending pending))
            {
                if (pending.Kind != Kind.Logout)
                {
                    _pending[guid] = new Pending(pending.Kind, snapshot);
                }

                return;
            }

            if (_known.ContainsKey(guid))
            {
                _pending[guid] = new Pending(Kind.Update, snapshot);
            }
        }
    }

    /// <summary>The character left the world.</summary>
    public void PublishLogout(ulong characterGuid)
    {
        lock (_gate)
        {
            bool known = _known.ContainsKey(characterGuid);
            bool hasPending = _pending.TryGetValue(characterGuid, out Pending pending);
            if (hasPending && pending.Kind == Kind.Login && !known)
            {
                _pending.Remove(characterGuid); // never reached the authority
                return;
            }

            // Also queued when nothing is known yet: a login may be in flight, and the flush that follows it finds the registration to remove.
            // A logout of a character this publisher never registered is dropped when it is sent.
            _pending[characterGuid] = new Pending(Kind.Logout, default);
        }
    }

    /// <summary>Starts the periodic flush. Idempotent.</summary>
    public void Start(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(interval, _stop.Token), CancellationToken.None);
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _logger.LogFault("cluster.presence.flush", ex, "Realm presence flush pass failed; the loop continues");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private long NextVersionAbove(long floor)
    {
        // Under the gate.
        long next = Math.Max(Math.Max(_time.GetUtcNow().UtcTicks, _lastVersion + 1), floor + 1);
        _lastVersion = next;
        return next;
    }

    private RealmPresenceEntry Build(in PresenceSnapshot s, long generation, long version) => new(
        s.CharacterGuid, s.CharacterName, s.AccountId, _workerId, _gatewayId, s.MapId, s.ZoneId, s.AreaId, s.Level, s.Class, s.Race, s.Gender,
        s.GuildId, s.GroupId, version, generation, _time.GetUtcNow(), s.GuildName, s.IsAfk, s.IsDnd);

    /// <summary>
    /// One pass: send the pending operations, then apply the kicks queued for this worker. Returns false when the authority could not be reached (the unsent operations
    /// stay pending). Never throws for a transport failure.
    /// </summary>
    public async Task<bool> FlushAsync(CancellationToken cancellationToken = default)
    {
        await _flushing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KeyValuePair<ulong, Pending>[] batch;
            lock (_gate)
            {
                batch = [.. _pending.OrderBy(static p => p.Key)];
                _pending.Clear();
            }

            for (int i = 0; i < batch.Length; i++)
            {
                try
                {
                    await SendOneAsync(batch[i].Key, batch[i].Value, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
                {
                    lock (_gate)
                    {
                        for (int j = i; j < batch.Length; j++)
                        {
                            _pending.TryAdd(batch[j].Key, batch[j].Value); // a newer wish recorded meanwhile wins
                        }
                    }

                    _logger.LogDebug("Presence flush deferred ({Message}); {Count} operations kept", ex.Message, batch.Length - i);
                    return false;
                }
            }

            try
            {
                IReadOnlyList<PresenceKick> kicks = await _authority.DrainKicksAsync(_workerId, cancellationToken).ConfigureAwait(false);
                foreach (PresenceKick kick in kicks)
                {
                    bool current;
                    lock (_gate)
                    {
                        current = _known.TryGetValue(kick.CharacterGuid, out Known? known)
                            && known.AccountId == kick.AccountId
                            && known.Generation == kick.SessionGeneration;
                    }

                    if (current)
                    {
                        _kickAccount(kick.AccountId);
                    }
                }
            }
            catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
            {
                _logger.LogDebug("Presence kick drain deferred ({Message})", ex.Message);
                return false;
            }

            return true;
        }
        finally
        {
            _flushing.Release();
        }
    }

    private static bool IsTransportFailure(Exception ex, CancellationToken ct)
        => ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private async Task SendOneAsync(ulong guid, Pending pending, CancellationToken ct)
    {
        switch (pending.Kind)
        {
            case Kind.Login:
            {
                long version;
                lock (_gate)
                {
                    version = NextVersionAbove(0);
                }

                PresenceUpsertReply reply = await _authority.RegisterAsync(Build(pending.Snapshot, pending.Snapshot.SessionGeneration, version), ct).ConfigureAwait(false);
                if (reply.Result == PresenceMutationResult.Applied && reply.Current is { } applied)
                {
                    lock (_gate)
                    {
                        _known[guid] = new Known(pending.Snapshot.AccountId, pending.Snapshot, applied.SessionGeneration, applied.PresenceVersion);
                        _lastVersion = Math.Max(_lastVersion, applied.PresenceVersion);
                    }
                }
                else
                {
                    _logger.LogWarning("Presence registration of character {Guid} was refused: {Result}", guid, reply.Result);
                }

                break;
            }

            case Kind.Update:
            {
                Known? known;
                long version;
                lock (_gate)
                {
                    if (!_known.TryGetValue(guid, out known))
                    {
                        return;
                    }

                    known.Snapshot = pending.Snapshot;
                    version = NextVersionAbove(known.Version);
                }

                PresenceUpsertReply reply = await _authority.UpsertAsync(Build(pending.Snapshot, known.Generation, version), ct).ConfigureAwait(false);
                lock (_gate)
                {
                    if (reply.Result == PresenceMutationResult.Applied)
                    {
                        known.Version = version;
                    }
                    else if (reply.Current is { } current && string.Equals(current.WorkerId, _workerId, StringComparison.Ordinal)
                        && reply.Result == PresenceMutationResult.RejectedStaleVersion)
                    {
                        known.Version = Math.Max(known.Version, current.PresenceVersion);
                        _pending.TryAdd(guid, new Pending(Kind.Update, pending.Snapshot)); // retry above the authority's version
                    }
                    else
                    {
                        _known.Remove(guid); // another worker's registration won
                    }
                }

                break;
            }

            case Kind.Logout:
            {
                Known? known;
                long version;
                lock (_gate)
                {
                    if (!_known.TryGetValue(guid, out known))
                    {
                        return;
                    }

                    version = NextVersionAbove(known.Version);
                }

                PresenceMutationResult result = await _authority.RemoveAsync(guid, version, known.Generation, _workerId, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _known.Remove(guid); // Applied, already gone, or a newer worker owns the character: nothing left for this worker to remove
                }

                if (result is PresenceMutationResult.RejectedStaleVersion or PresenceMutationResult.RejectedStaleSessionGeneration)
                {
                    _logger.LogDebug("Presence removal of character {Guid} rejected ({Result}): a newer registration owns it", guid, result);
                }

                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("cluster.presence-publisher", ex);
        }

        _stop.Dispose();
    }
}
