using System.Collections.Concurrent;
using ArcaneCore.Data.Characters.Ownership;
using ArcaneCore.Data.Characters.Transfers;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.World.Clustering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Persistence;

/// <summary>
/// The login claim and the logout release of a character's per-character claim (<c>cluster_transfer_owner</c>; design _wf/writer-lock-DESIGN.md section 1b, step C5).
/// In Worker fencing scope the write fence refuses every claim-checked save of a character this worker has not claimed, so a login must claim the character before it is
/// attached (<see cref="ClaimAsync"/>) and a logout releases the claim once the character's last write is durable (<see cref="ScheduleRelease"/>). Outside Worker scope (Realm scope, no store,
/// no token) every call is a no-op that admits the login: no claim exists in those modes.
/// <para>
/// The claim is built with the login-time reap (design section 5.4) from <c>Cluster:Fencing:ReapDeadOwners</c> and <c>Cluster:Fencing:ReapAfterSeconds</c>, backed by the real shard advisory-lock probe
/// (<see cref="WorldWriterGuard.ProbeShardFree"/>): a character claimed by a dead shard whose lock is free and whose heartbeat is stale is taken over instead of refusing the login.
/// </para>
/// <para>
/// Every successful claim (<see cref="CharacterClaimOutcome.Claimed"/>, <see cref="CharacterClaimOutcome.AlreadyHeld"/> or a take-over) is given a per-character claim ticket, a monotonic generation
/// (<see cref="ClaimWithTicketAsync"/>). A release carries the ticket of the login that is releasing and runs only while that ticket is the newest live one of the character, so a newer login
/// (which claimed before or after the release was scheduled) always invalidates the releases of older logins: a session replacement or a quick relogin never loses its claim.
/// </para>
/// <para>
/// Thread-safe. A claim and a release of the same character never interleave (a striped async gate), and a login that claims while an earlier logout's release is still waiting for its flush
/// supersedes that release, so a release can never delete the claim of a session that logged in after it was scheduled. A release whose flush is not durable keeps the claim (the retained
/// saves need it) and retries a few times; a claim that is still kept is refreshed by the next login of that character on this shard and is taken over when this incarnation dies.
/// </para>
/// </summary>
public sealed class CharacterClaimService(
    IServiceProvider services,
    IOptions<CharacterFencingOptions>? fencingOptions = null,
    ILogger<CharacterClaimService>? logger = null) : IDisposable
{
    private const int GateStripes = 32;

    private readonly SemaphoreSlim[] _gates = [.. Enumerable.Range(0, GateStripes).Select(static _ => new SemaphoreSlim(1, 1))];
    private readonly object _pendingGate = new();
    private readonly Dictionary<int, object> _pendingReleases = [];
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<int, List<long>> _liveTickets = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, System.Runtime.CompilerServices.StrongBox<long>> _bound = new();
    private long _generation;
    private int _disposed;

    /// <summary>Delay before a release is retried after a flush that was not durable, times the attempt number (test seam).</summary>
    internal TimeSpan ReleaseRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts of a release whose flush is not durable (test seam).</summary>
    internal int ReleaseAttempts { get; init; } = 3;

    /// <summary>Poll interval of a received claim's release while the transfer that brought it is still in flight (test seam).</summary>
    internal TimeSpan TransferSettlePoll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>True when the verdict lets the login proceed: the character is claimed, or no claim exists in this mode.</summary>
    public static bool Admits(CharacterClaimResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsClaimed || result.Outcome == CharacterClaimOutcome.NotWorkerScope;
    }

    /// <summary>The reap policy of the configured options, or null when <c>Cluster:Fencing:ReapDeadOwners</c> is off or there is no guard to probe the shard lock with.</summary>
    public CharacterClaimReapPolicy? BuildReapPolicy()
    {
        CharacterFencingOptions fencing = fencingOptions?.Value ?? new();
        if (!fencing.ReapDeadOwners || services.GetService<WorldWriterGuard>() is not { } guard)
            return null;
        return new CharacterClaimReapPolicy(
            TimeSpan.FromSeconds(fencing.ReapAfterSeconds),
            (scope, cancellationToken) => Task.Run(() => guard.ProbeShardFree(scope), cancellationToken));
    }

    /// <summary>
    /// The login claim of <paramref name="characterId"/> for <paramref name="token"/> (the world's token). Returns <see cref="CharacterClaimOutcome.NotWorkerScope"/> without any SQL when there is
    /// no token, it is not a worker-shard token, or no <see cref="EfTransferDecisionStore"/> is registered. A store failure propagates (the login is refused as failed).
    /// </summary>
    public async Task<CharacterClaimResult> ClaimAsync(int characterId, CharacterOwnershipToken? token, CancellationToken cancellationToken = default)
        => (await ClaimWithTicketAsync(characterId, token, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// <see cref="ClaimAsync"/> that also returns the login's claim ticket: a monotonic generation (greater than zero) for every claimed outcome, including
    /// <see cref="CharacterClaimOutcome.AlreadyHeld"/>; zero when nothing was claimed (refused, or no claim exists in this mode). Pass it to <see cref="ScheduleRelease"/>.
    /// <paramref name="quietRefusal"/> logs a refusal at debug instead of warning, for a caller that expects refusals and reports the outcome itself.
    /// </summary>
    public async Task<(CharacterClaimResult Result, long Ticket)> ClaimWithTicketAsync(
        int characterId, CharacterOwnershipToken? token, CancellationToken cancellationToken = default, bool quietRefusal = false)
    {
        if (token is null || !CharacterOwnershipScopes.IsWorker(token.Scope) || services.GetService<EfTransferDecisionStore>() is not { } store)
            return (new CharacterClaimResult(CharacterClaimOutcome.NotWorkerScope, null, null), 0);

        SemaphoreSlim gate = GateOf(characterId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CharacterClaimResult result = await store.TryClaimCharacterAsync((ulong)characterId, token, BuildReapPolicy(), cancellationToken).ConfigureAwait(false);
            long ticket = 0;
            if (result.IsClaimed)
            {
                ticket = IssueTicket(characterId);
            }
            else if (quietRefusal)
            {
                // A refusal the caller expects (a login waiting for the replaced session's logout on another worker): the caller reports the outcome once.
                logger?.LogDebug("character {Character} login refused: claim outcome {Outcome} (held by {Scope})", characterId, result.Outcome, result.PreviousScope ?? "no one");
            }
            else
            {
                logger?.LogWarning("character {Character} login refused: claim outcome {Outcome} (held by {Scope})", characterId, result.Outcome, result.PreviousScope ?? "no one");
            }

            return (result, ticket);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The scope of another worker that holds <paramref name="characterId"/> live (see <see cref="EfTransferDecisionStore.FindLiveOtherOwnerAsync"/>), or null. Read-only: nothing is claimed
    /// or written. Null without a worker-shard token or a store (no claim exists in those modes). For a caller that must not touch anything of a character another worker runs.
    /// </summary>
    public async Task<string?> FindLiveOtherOwnerAsync(int characterId, CharacterOwnershipToken? token, CancellationToken cancellationToken = default)
    {
        if (token is null || !CharacterOwnershipScopes.IsWorker(token.Scope) || services.GetService<EfTransferDecisionStore>() is not { } store)
            return null;
        return await store.FindLiveOtherOwnerAsync((ulong)characterId, token, BuildReapPolicy(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases this worker's claim of <paramref name="characterId"/> once the character's persistence is durable (every settlement barrier and write queue drained, the same barrier a login
    /// waits on). Call it after the logout snapshot was queued (the world thread, after <c>RemovePlayer</c>), and also for a login that claimed but never entered the world. Returns at once;
    /// the flush and the release run in the background. A no-op without a worker-shard token or a store. <paramref name="isOnline"/> (thread-safe) is asked just before the delete: a character that
    /// is online again keeps its claim (a second login attempt of the same character that failed must not strip the first one's claim).
    /// <paramref name="ticket"/> is the releasing login's claim ticket (<see cref="ClaimWithTicketAsync"/>): the release is skipped unless it is still the newest live ticket of the
    /// character, so a login that claimed after this one (before or after this call) keeps its claim. Zero means an unticketed release (only the marker supersession applies).
    /// </summary>
    public void ScheduleRelease(int characterId, CharacterOwnershipToken? token, Func<bool>? isOnline = null, long ticket = 0)
    {
        if (Volatile.Read(ref _disposed) != 0 || token is null || !CharacterOwnershipScopes.IsWorker(token.Scope)
            || services.GetService<EfTransferDecisionStore>() is not { } store)
            return;

        object marker = new();
        lock (_pendingGate)
        {
            if (ticket != 0 && !IsNewestLive(characterId, ticket))
            {
                RemoveTicketLocked(characterId, ticket);
                return; // a newer login claimed first: its claim is not this logout's to release (and its own pending release, if any, stays registered)
            }

            _pendingReleases[characterId] = marker;
        }

        StartRelease(store, characterId, token, marker, isOnline, ticket);
    }

    /// <summary>
    /// Releases a claim that reached this worker through a committed transfer whose character nobody plays here (the target's activation without the client): no logout will
    /// ever release it, and while it stays, logins of the character on other workers are refused as <see cref="CharacterClaimOutcome.RefusedLiveOwner"/>. Takes a ticket for the
    /// received claim (<see cref="IssueTicketForReceivedClaim"/>), so a release of an older login cannot delete it and a login that claims after this call keeps it, then releases
    /// like <see cref="ScheduleRelease"/> once the transfer that brought the claim is marked completed (the claim row holds no transfer; until then the release would delete nothing).
    /// While the transfer is still in flight the release keeps polling, until the claim settles, a login of the character supersedes it, or the service is disposed (then the claim
    /// stays; this incarnation's death releases it), so a slow commit cannot strand the claim. Call it after the arrival
    /// was written. A no-op without a worker-shard token or a store. <paramref name="isOnline"/> (thread-safe) keeps the claim of a character that is online here by then.
    /// </summary>
    public void ScheduleReleaseOfReceivedClaim(int characterId, CharacterOwnershipToken? token, Func<bool>? isOnline = null)
    {
        if (Volatile.Read(ref _disposed) != 0 || token is null || !CharacterOwnershipScopes.IsWorker(token.Scope)
            || services.GetService<EfTransferDecisionStore>() is not { } store)
            return;

        long ticket = IssueTicket(characterId);
        object marker = new();
        lock (_pendingGate)
        {
            _pendingReleases[characterId] = marker; // the ticket was just issued, so it is the newest live one
        }

        StartRelease(store, characterId, token, marker, isOnline, ticket, afterTransferSettles: true);
    }

    private void StartRelease(EfTransferDecisionStore store, int characterId, CharacterOwnershipToken token, object marker, Func<bool>? isOnline, long ticket, bool afterTransferSettles = false)
    {
        Task release = Task.Run(() => ReleaseAsync(store, characterId, token, marker, isOnline, ticket, afterTransferSettles));
        _inFlight[release] = 0;
        _ = release.ContinueWith(done => _inFlight.TryRemove(done, out _), TaskScheduler.Default);
    }

    /// <summary>
    /// A claim ticket for a character whose claim reached this worker through a committed transfer (the target's activation): it is newer than every ticket handed out before, so a release of an
    /// older login is invalid from here on, and the activated player's own logout release carries it (<see cref="BindTicket"/>). Zero without a worker-shard token (no claim exists in this mode).
    /// Call <see cref="AbandonTicket"/> when the activation fails. Any thread.
    /// </summary>
    public long IssueTicketForReceivedClaim(int characterId, CharacterOwnershipToken? token)
        => token is null || !CharacterOwnershipScopes.IsWorker(token.Scope) ? 0 : IssueTicket(characterId);

    private long IssueTicket(int characterId)
    {
        long ticket = Interlocked.Increment(ref _generation);
        lock (_pendingGate)
        {
            _pendingReleases.Remove(characterId); // this login owns the claim now: a release still waiting for its flush must not delete it
            if (!_liveTickets.TryGetValue(characterId, out List<long>? live))
                _liveTickets[characterId] = live = [];
            live.Add(ticket); // newer than every ticket handed out before: the releases of older logins are invalid from here on
        }

        return ticket;
    }

    /// <summary>Completes when every release scheduled so far has finished (shutdown and tests).</summary>
    public Task DrainAsync() => Task.WhenAll(_inFlight.Keys);

    /// <summary>Binds a login's claim ticket to its player object so the logout path can find it (<see cref="TicketOf"/>). Ignored for ticket zero.</summary>
    public void BindTicket(object owner, long ticket)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (ticket != 0)
            _bound.AddOrUpdate(owner, new System.Runtime.CompilerServices.StrongBox<long>(ticket));
    }

    /// <summary>The claim ticket bound to <paramref name="owner"/>, or zero when it has none.</summary>
    public long TicketOf(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return _bound.TryGetValue(owner, out System.Runtime.CompilerServices.StrongBox<long>? box) ? box.Value : 0;
    }

    /// <summary>
    /// A login that claimed but was refused entry because the character is online under an older login (which keeps the claim): its ticket stops counting,
    /// so the older login's release is valid again. When <paramref name="token"/> is given (a worker-shard token) and no live ticket of the character is left (the older login already logged out and its
    /// release was skipped because this ticket was newer), a release is scheduled for the claim this abandoned login took: otherwise this worker would hold it until it dies and logins of the character
    /// on other workers would be refused as <see cref="CharacterClaimOutcome.RefusedLiveOwner"/>. <paramref name="isOnline"/> (thread-safe) keeps the claim of a character that is online.
    /// </summary>
    public void AbandonTicket(int characterId, long ticket, CharacterOwnershipToken? token = null, Func<bool>? isOnline = null)
    {
        if (ticket == 0)
            return;
        EfTransferDecisionStore? store = null;
        object? marker = null;
        lock (_pendingGate)
        {
            RemoveTicketLocked(characterId, ticket);
            // The ticket-0 release marker is registered in the same critical section that observed "no live ticket left": a login that issues its ticket
            // right after this block clears the marker (IssueTicket), so the release finds itself superseded instead of deleting the new login's claim.
            if (token is not null && !_liveTickets.ContainsKey(characterId)
                && Volatile.Read(ref _disposed) == 0 && CharacterOwnershipScopes.IsWorker(token.Scope)
                && services.GetService<EfTransferDecisionStore>() is { } found)
            {
                store = found;
                marker = new object();
                _pendingReleases[characterId] = marker;
            }
        }

        if (store is not null && marker is not null && token is not null)
            StartRelease(store, characterId, token, marker, isOnline, 0);
    }

    /// <summary>Caller holds <c>_pendingGate</c>.</summary>
    private void RemoveTicketLocked(int characterId, long ticket)
    {
        if (ticket != 0 && _liveTickets.TryGetValue(characterId, out List<long>? live) && live.Remove(ticket) && live.Count == 0)
            _liveTickets.Remove(characterId);
    }

    /// <summary>Caller holds <c>_pendingGate</c>.</summary>
    private bool IsNewestLive(int characterId, long ticket)
        => _liveTickets.TryGetValue(characterId, out List<long>? live) && live.Count > 0 && live.Max() == ticket;

    private async Task ReleaseAsync(EfTransferDecisionStore store, int characterId, CharacterOwnershipToken token, object marker, Func<bool>? isOnline, long ticket, bool afterTransferSettles)
    {
        try
        {
            CancellationToken stop = _stop.Token;
            if (afterTransferSettles && !await WaitForTransferSettledAsync(store, characterId, token, marker, stop).ConfigureAwait(false))
                return;

            CharacterPersistenceBarrier? barrier = services.GetService<CharacterPersistenceBarrier>();
            for (int attempt = 1; ; attempt++)
            {
                CharacterPersistenceFlushResult? flushed = barrier is null ? null : await barrier.TryFlushCharacterAsync(characterId, cancellationToken: stop).ConfigureAwait(false);
                if (flushed is null || flushed.IsDurable)
                    break;
                if (attempt >= ReleaseAttempts)
                {
                    logger?.LogWarning("character {Character} claim kept after logout: queues not durable ({Queues}); the next login of the character on this shard refreshes it", characterId, string.Join(", ", flushed.FailedQueues));
                    ForgetPending(characterId, marker);
                    return;
                }

                await Task.Delay(ReleaseRetryDelay * attempt, stop).ConfigureAwait(false);
            }

            SemaphoreSlim gate = GateOf(characterId);
            await gate.WaitAsync(stop).ConfigureAwait(false);
            try
            {
                lock (_pendingGate)
                {
                    if (!_pendingReleases.TryGetValue(characterId, out object? current) || !ReferenceEquals(current, marker))
                        return; // a login claimed the character meanwhile: the claim is that session's now
                    if (ticket != 0 && !IsNewestLive(characterId, ticket))
                    {
                        _pendingReleases.Remove(characterId);
                        return; // a newer login holds a newer ticket: the claim is that login's
                    }

                    if (ticket == 0 && _liveTickets.ContainsKey(characterId))
                    {
                        _pendingReleases.Remove(characterId);
                        return; // a release without a ticket (an abandoned login's) never deletes a claim a live login holds
                    }

                    _pendingReleases.Remove(characterId);
                }

                if (isOnline?.Invoke() == true)
                    return; // the character is in the world again: its claim stays

                await store.ReleaseCharacterClaimAsync((ulong)characterId, token, stop).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            /* shutdown: the claim stays, this incarnation's death releases it. A cancellation that is not the stop (a store timeout) falls to the handler below, which forgets the marker and records the fault. */
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ForgetPending(characterId, marker);
            if (logger is not null)
                logger.LogFault("persist.character-claim-release", ex, "releasing the claim of character {Character} failed; it stays until the next login refreshes it", characterId);
            else
                FaultLedger.Shared.Record("persist.character-claim-release", ex);
        }
        finally
        {
            if (ticket != 0)
            {
                lock (_pendingGate)
                {
                    RemoveTicketLocked(characterId, ticket);
                }
            }
        }
    }

    /// <summary>
    /// Waits until the claim row of <paramref name="characterId"/> held by <paramref name="token"/> carries no transfer, with no time limit (disposal cancels <paramref name="stop"/>).
    /// False when the release must not go on: a login superseded it (its marker is gone).
    /// </summary>
    private async Task<bool> WaitForTransferSettledAsync(EfTransferDecisionStore store, int characterId, CharacterOwnershipToken token, object marker, CancellationToken stop)
    {
        long started = Environment.TickCount64;
        bool slowReported = false;
        int failures = 0; // consecutive failed polls: the delay between polls grows with them, so a broken store is not hammered four times a second
        while (true)
        {
            lock (_pendingGate)
            {
                if (!_pendingReleases.TryGetValue(characterId, out object? current) || !ReferenceEquals(current, marker))
                    return false; // a login claimed the character meanwhile: the claim is that session's now
            }

            try
            {
                if (!await store.IsCharacterClaimInTransferAsync((ulong)characterId, token, stop).ConfigureAwait(false))
                    return true;

                failures = 0;
            }
            catch (Exception ex) when (!stop.IsCancellationRequested && ex is not OutOfMemoryException)
            {
                // A failed poll says nothing about the transfer: keep polling, or the claim would stay and other workers refuse the character's logins. A cancellation that is not the service's stop
                // (a command timeout surfaces as one) is a failed poll too; the stop itself is not caught here and ends the wait.
                if (failures++ == 0)
                {
                    logger?.LogWarning(ex, "polling the transfer of character {Character} failed; retrying", characterId);
                }
            }

            if (!slowReported && Environment.TickCount64 - started >= 60_000)
            {
                slowReported = true;
                logger?.LogWarning("the transfer that brought the claim of character {Character} has not settled after 60 s; the claim release keeps polling", characterId);
            }

            await Task.Delay(SettlePollDelay(failures), stop).ConfigureAwait(false);
        }
    }

    /// <summary>The wait before the next settle poll: <see cref="TransferSettlePoll"/> after a good poll, doubled for each consecutive failed one up to 30 s.</summary>
    private TimeSpan SettlePollDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
            return TransferSettlePoll;
        long grown = consecutiveFailures >= 8 ? TimeSpan.FromSeconds(30).Ticks : Math.Min(TransferSettlePoll.Ticks << consecutiveFailures, TimeSpan.FromSeconds(30).Ticks);
        return TimeSpan.FromTicks(Math.Max(TransferSettlePoll.Ticks, grown));
    }

    private void ForgetPending(int characterId, object marker)
    {
        lock (_pendingGate)
        {
            if (_pendingReleases.TryGetValue(characterId, out object? current) && ReferenceEquals(current, marker))
                _pendingReleases.Remove(characterId);
        }
    }

    private SemaphoreSlim GateOf(int characterId) => _gates[(int)((uint)characterId % GateStripes)];

    /// <summary>Cancels releases still waiting (their claims stay; the process is ending) and waits briefly for them.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        try
        {
            Task.WhenAll(_inFlight.Keys).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            /* releases handle their own failures */
        }

        _stop.Dispose();
        foreach (SemaphoreSlim gate in _gates)
            gate.Dispose();
    }
}
