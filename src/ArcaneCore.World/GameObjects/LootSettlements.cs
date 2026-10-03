using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Loot;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The world daemon's <see cref="ILootStateCoordinator"/>: owns the committed cache of the chest
/// loot of dungeon instances and settles each loot operation off the world thread, in the style of
/// <see cref="Economy.EconomySettlements"/>. An actor (the taking character) is frozen on the world
/// thread (the shared settlement hold on <see cref="Player"/>: packets, saves, money and inventory
/// mutation are refused); its pre-operation snapshot is saved through the ordered save queue; the
/// instance write queue is drained up to the operation's start (so the logical instance row exists);
/// then the chest state and the award commit in one serializable transaction with an idempotency key.
/// Publication happens back on the world thread: the cache only ever holds committed state, and
/// nothing is rebuilt, opened or taken for a key while its operation is in flight. An unreadable
/// outcome blocks the key until restart and kicks the actor, so no stale snapshot can overwrite the result.
/// </summary>
public sealed class LootSettlements(IServiceScopeFactory scopes, ILogger logger) : ILootStateCoordinator
{
    public const int MaxConcurrentOperations = 16;
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly HashSet<Operation> _operations = [];
    private readonly Dictionary<LootStateKey, Operation> _pending = [];
    private readonly Dictionary<LootStateKey, LootStateRecord> _cache = [];
    private readonly HashSet<LootStateKey> _blocked = [];
    private readonly CancellationTokenSource _stop = new();
    private WorldRuntime? _world;
    private CharacterSaveQueue? _saves;
    private TeleportFeature? _teleports;
    private Func<uint, bool> _scopeAlive = static _ => false;
    private Func<long> _watermark = static () => 0;
    private Func<long, CancellationToken, Task> _barrier = static (_, _) => Task.CompletedTask;
    private bool _stopping;
    private Task? _stopped;

    /// <summary>Test seam: runs in the worker after the barrier, immediately before the commit.</summary>
    internal Func<Guid, Task>? BeforeCommit { get; set; }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _operations.Count;
            }
        }
    }

    public long UnixNow => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Seed the committed cache with the stored chests (startup, before the world thread runs).</summary>
    public void Load(IEnumerable<LootStateRecord> records)
    {
        foreach (LootStateRecord record in records)
        {
            _cache[record.Key] = record;
        }
    }

    /// <summary>
    /// Wire the world collaborators (world thread, at install): the save queue and teleports for
    /// the actors, whether a logical instance save is still alive, and the instance write queue's
    /// watermark and bounded wait.
    /// </summary>
    public void Attach(WorldRuntime world, CharacterSaveQueue? saves, TeleportFeature? teleports,
        Func<uint, bool> scopeAlive, Func<long> watermark, Func<long, CancellationToken, Task> barrier)
    {
        _world = world;
        _saves = saves;
        _teleports = teleports;
        _scopeAlive = scopeAlive;
        _watermark = watermark;
        _barrier = barrier;
    }

    /// <summary>The logical instance save was deleted (reset or removal): its chests go with it (world thread).</summary>
    public void OnInstanceDeleted(uint instanceId)
    {
        foreach (LootStateKey key in _cache.Keys.Where(k => k.InstanceId == instanceId).ToArray())
        {
            _cache.Remove(key);
        }

        _blocked.RemoveWhere(k => k.InstanceId == instanceId);
    }

    public bool CanPersist(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.InstanceId != 0 && _saves is not null && _scopeAlive(map.InstanceId);
    }

    public LootStateRecord? Find(LootStateKey key) => _cache.GetValueOrDefault(key);

    public bool IsPending(LootStateKey key)
    {
        lock (_gate)
        {
            return _pending.ContainsKey(key);
        }
    }

    public bool IsBlocked(LootStateKey key)
    {
        lock (_gate)
        {
            return _pending.ContainsKey(key) || _blocked.Contains(key);
        }
    }

    public LootActor? CreateActor(Player player, InventorySnapshot before, InventorySnapshot after)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (_world is not { } world || player.Session is not WorldSession session || !CanAct(session, player))
        {
            return null;
        }

        var beforeState = player.CreateSnapshot(world.NowMs) with { Inventory = before };
        return new LootActor(player, beforeState, beforeState with { Inventory = after });
    }

    public bool TryStart(LootOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_world is not { IsWorldThread: true } world || _saves is not { } saves
            || (operation.Actor is { } actor && !CanAct(actor.Player.Session as WorldSession, actor.Player)))
        {
            return false;
        }

        Guid operationId = Guid.NewGuid();
        EconomyParticipant[] participants = operation.Actor is { } participant
            ? [new EconomyParticipant(participant.Before, participant.After)] : [];
        var request = new LootCommitRequest(operationId, operation.Key, operation.Expected, operation.Updated, participants, operation.Awards);
        var settlement = new Operation(operationId, operation, request, _watermark());
        lock (_gate)
        {
            if (_stopping || _operations.Count >= MaxConcurrentOperations || _pending.ContainsKey(operation.Key)
                || _blocked.Contains(operation.Key) || !Equals(_cache.GetValueOrDefault(operation.Key), operation.Expected))
            {
                return false;
            }

            if (operation.Actor is { } toFreeze)
            {
                int id = toFreeze.Before.Id;
                saves.HoldCharacter(id);
                if (!toFreeze.Player.BeginQuestSettlement(operationId))
                {
                    saves.ResumeCharacter(id);
                    return false;
                }
            }

            _operations.Add(settlement);
            _pending[operation.Key] = settlement;
            settlement.Worker = Task.Run(() => SettleAsync(settlement));
        }

        return true;
    }

    /// <summary>Completes when no operation involving the character is in flight (storage and publication).</summary>
    public Task WaitForCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _operations.Where(o => o.Source.Actor?.Before.Id == characterId).Select(o => o.Completion.Task)];
        }

        return Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    /// <summary>Cancel the workers, wait for them and finalize without world publication.</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return _stopped ??= StopCoreAsync();
        }
    }

    /// <summary>World thread: whether the player may enter a settlement now (the economy rules).</summary>
    private bool CanAct(WorldSession? session, Player player)
    {
        int id = checked((int)player.Guid.Low);
        return session is not null && _world is { IsWorldThread: true } world && _saves is { } saves
            && ReferenceEquals(world.FindOnlinePlayer(player.Guid), player)
            && ReferenceEquals(session.Player, player) && session.State == SessionState.InWorld
            && !(_teleports?.Teleports.IsBeingTeleported(player) ?? false)
            && !player.IsQuestSettlementPending && !player.IsLoggingOut && !saves.IsHeld(id) && !saves.IsQuarantined(id);
    }

    private async Task StopCoreAsync()
    {
        await Task.Yield();
        await _stop.CancelAsync().ConfigureAwait(false);
        Operation[] operations;
        lock (_gate)
        {
            operations = [.. _operations];
        }

        try
        {
            await Task.WhenAll(operations.Select(o => o.Worker)).ConfigureAwait(false);
        }
        finally
        {
            foreach (Operation operation in operations)
            {
                Finalize(operation, publishLive: false);
            }
        }
    }

    private async Task SettleAsync(Operation operation)
    {
        LootOutcome outcome = LootOutcome.NotStarted;
        bool transactionStarted = false;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            budget.CancelAfter(Budget);
            if (operation.Source.Actor is { } actor)
            {
                // The pre-operation snapshot must be durable first: the commit compares the stored
                // money and inventory against it, so a lost earlier save is a conflict, not a dupe.
                await _saves!.SaveForSettlementAsync(actor.Before, budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                _saves.QuarantineCharacter(actor.Before.Id);
            }

            // A queued InstanceSaved must land before the commit looks for the instance row; a
            // bounded wait for the writes queued up to now, not for the whole (never idle) queue.
            await _barrier(operation.Watermark, budget.Token).ConfigureAwait(false);
            if (BeforeCommit is { } hook)
            {
                await hook(operation.Id).ConfigureAwait(false);
            }

            transactionStarted = true;
            outcome = LootOutcome.Unknown;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            LootCommitResult result = await scope.ServiceProvider.GetRequiredService<ILootStateStore>()
                .CommitAsync(operation.Request, budget.Token).ConfigureAwait(false);
            outcome = result is LootCommitResult.Committed or LootCommitResult.AlreadyCommitted ? LootOutcome.After : LootOutcome.Before;
            if (outcome == LootOutcome.Before)
            {
                logger.LogInformation("loot operation {Operation} was refused by storage ({Result})", operation.Id, result);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "loot operation {Operation} failed", operation.Id);
            if (transactionStarted && outcome != LootOutcome.After)
            {
                outcome = LootOutcome.Unknown;
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    budget.CancelAfter(Budget);
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    outcome = await scope.ServiceProvider.GetRequiredService<ILootStateStore>()
                        .IsCommittedAsync(operation.Id, budget.Token).ConfigureAwait(false) ? LootOutcome.After : LootOutcome.Before;
                }
                catch (Exception reconciliation) when (reconciliation is not OutOfMemoryException)
                {
                    logger.LogError(reconciliation, "could not reconcile loot operation {Operation}", operation.Id);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                operation.Outcome = outcome;
                if (!_stopping)
                {
                    _world!.Post(() => Finalize(operation, publishLive: true));
                }
            }
        }
    }

    private void Finalize(Operation operation, bool publishLive)
    {
        lock (_gate)
        {
            if (!_operations.Contains(operation) || operation.Finalizing)
            {
                return;
            }

            operation.Finalizing = true;
        }

        bool actorCurrent = false;
        try
        {
            if (publishLive)
            {
                // The committed state is authoritative; it is cached unless its logical save was
                // deleted meanwhile (a reset): that instance's chests are gone.
                lock (_gate)
                {
                    if (operation.Outcome == LootOutcome.After && _scopeAlive(operation.Source.Key.InstanceId))
                    {
                        _cache[operation.Source.Key] = operation.Source.Updated;
                    }

                    if (operation.Outcome == LootOutcome.Unknown)
                    {
                        _blocked.Add(operation.Source.Key);
                    }

                    _pending.Remove(operation.Source.Key);
                }

                if (operation.Source.Actor is { } actor)
                {
                    actorCurrent = FinalizeActor(operation, actor);
                }
            }

            operation.Source.Finished(operation.Outcome, publishLive, actorCurrent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "loot operation {Operation} callback failed", operation.Id);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(operation.Source.Key);
                _operations.Remove(operation);
                operation.Completion.TrySetResult();
            }
        }
    }

    /// <summary>Release or publish the actor; true when it is still the current online character.</summary>
    private bool FinalizeActor(Operation operation, LootActor actor)
    {
        Player player = actor.Player;
        int id = actor.Before.Id;
        bool current = _world is { } world && ReferenceEquals(world.FindOnlinePlayer(player.Guid), player)
            && player.QuestSettlementOperationId == operation.Id;
        try
        {
            switch (operation.Outcome)
            {
                case LootOutcome.After when current:
                    using (player.BeginQuestSettlementPublication(operation.Id))
                    {
                        operation.Source.PublishActor?.Invoke();
                    }

                    player.EndQuestSettlement(operation.Id);
                    _saves!.ResumeCharacter(id);
                    break;
                case LootOutcome.Before or LootOutcome.NotStarted when current:
                    player.EndQuestSettlement(operation.Id);
                    _saves!.ResumeCharacter(id);
                    break;
                case LootOutcome.Unknown when current:
                    (player.Session as WorldSession)?.Kick();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The durable state is authoritative; the live copy is not trustworthy any more.
            _saves!.QuarantineCharacter(id);
            logger.LogError(ex, "loot publication failed for character {Character}; fresh login is required", id);
            if (current)
            {
                (player.Session as WorldSession)?.Kick();
            }

            return false;
        }

        return current && operation.Outcome != LootOutcome.Unknown;
    }

    private sealed class Operation(Guid id, LootOperation source, LootCommitRequest request, long watermark)
    {
        public Guid Id { get; } = id;
        public LootOperation Source { get; } = source;
        public LootCommitRequest Request { get; } = request;
        public long Watermark { get; } = watermark;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker { get; set; } = Task.CompletedTask;
        public LootOutcome Outcome { get; set; }
        public bool Finalizing { get; set; }
    }
}
