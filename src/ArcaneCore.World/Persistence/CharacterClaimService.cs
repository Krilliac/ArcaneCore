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
    private int _disposed;

    /// <summary>Delay before a release is retried after a flush that was not durable, times the attempt number (test seam).</summary>
    internal TimeSpan ReleaseRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Attempts of a release whose flush is not durable (test seam).</summary>
    internal int ReleaseAttempts { get; init; } = 3;

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
    {
        if (token is null || !CharacterOwnershipScopes.IsWorker(token.Scope) || services.GetService<EfTransferDecisionStore>() is not { } store)
            return new CharacterClaimResult(CharacterClaimOutcome.NotWorkerScope, null, null);

        SemaphoreSlim gate = GateOf(characterId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CharacterClaimResult result = await store.TryClaimCharacterAsync((ulong)characterId, token, BuildReapPolicy(), cancellationToken).ConfigureAwait(false);
            if (result.IsClaimed)
            {
                lock (_pendingGate)
                {
                    _pendingReleases.Remove(characterId); // this login owns the claim now: a release still waiting for its flush must not delete it
                }
            }
            else
            {
                logger?.LogWarning("character {Character} login refused: claim outcome {Outcome} (held by {Scope})", characterId, result.Outcome, result.PreviousScope ?? "no one");
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Releases this worker's claim of <paramref name="characterId"/> once the character's persistence is durable (every settlement barrier and write queue drained, the same barrier a login
    /// waits on). Call it after the logout snapshot was queued (the world thread, after <c>RemovePlayer</c>), and also for a login that claimed but never entered the world. Returns at once;
    /// the flush and the release run in the background. A no-op without a worker-shard token or a store. <paramref name="isOnline"/> (thread-safe) is asked just before the delete: a character that
    /// is online again keeps its claim (a second login attempt of the same character that failed must not strip the first one's claim).
    /// </summary>
    public void ScheduleRelease(int characterId, CharacterOwnershipToken? token, Func<bool>? isOnline = null)
    {
        if (Volatile.Read(ref _disposed) != 0 || token is null || !CharacterOwnershipScopes.IsWorker(token.Scope)
            || services.GetService<EfTransferDecisionStore>() is not { } store)
            return;

        object marker = new();
        lock (_pendingGate)
        {
            _pendingReleases[characterId] = marker;
        }

        Task release = Task.Run(() => ReleaseAsync(store, characterId, token, marker, isOnline));
        _inFlight[release] = 0;
        _ = release.ContinueWith(done => _inFlight.TryRemove(done, out _), TaskScheduler.Default);
    }

    /// <summary>Completes when every release scheduled so far has finished (shutdown and tests).</summary>
    public Task DrainAsync() => Task.WhenAll(_inFlight.Keys);

    private async Task ReleaseAsync(EfTransferDecisionStore store, int characterId, CharacterOwnershipToken token, object marker, Func<bool>? isOnline)
    {
        try
        {
            CancellationToken stop = _stop.Token;
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
        catch (OperationCanceledException)
        {
            /* shutdown: the claim stays, this incarnation's death releases it */
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ForgetPending(characterId, marker);
            if (logger is not null)
                logger.LogFault("persist.character-claim-release", ex, "releasing the claim of character {Character} failed; it stays until the next login refreshes it", characterId);
            else
                FaultLedger.Shared.Record("persist.character-claim-release", ex);
        }
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
