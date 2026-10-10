using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Economy;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Economy;

/// <summary>Where an economy operation ended up durably.</summary>
public enum EconomyOutcome
{
    /// <summary>Nothing was attempted (shutdown, a failed pre-commit save).</summary>
    NotStarted,

    /// <summary>The transaction did not commit; the pre-operation state stands.</summary>
    Before,

    /// <summary>The transaction committed.</summary>
    After,

    /// <summary>The store could not be read back; affected characters stay quarantined until a fresh login.</summary>
    Unknown,
}

/// <summary>One online character whose money and inventory an operation changes.</summary>
public sealed class EconomyActor
{
    internal EconomyActor(WorldSession session, Player player, EconomyInventoryStage stage, uint moneyAfter, CharacterState before,
        CharacterLife? lifeAfter = null)
    {
        Session = session;
        Player = player;
        Stage = stage;
        MoneyAfter = moneyAfter;
        Before = before;
        AppliesLife = lifeAfter is not null;
        After = before with { Money = moneyAfter, Inventory = stage.After,
            Life = lifeAfter is null ? before.Life : lifeAfter with { Powers = Array.AsReadOnly(lifeAfter.Powers.ToArray()) } };
    }

    public WorldSession Session { get; }
    public Player Player { get; }
    public EconomyInventoryStage Stage { get; }
    public uint MoneyAfter { get; }
    public CharacterState Before { get; }
    public CharacterState After { get; }
    public bool AppliesLife { get; }
    public int Id => Before.Id;
}

/// <summary>
/// Runs economy transactions off the world thread. Every actor is frozen on the world thread
/// (the shared settlement hold on <see cref="Player"/>: packets, saves, money and inventory
/// mutation are refused), its pre-operation snapshot is saved through the ordered save queue,
/// then the whole operation commits in one serializable transaction with an idempotency key.
/// Publication happens back on the world thread only for actors still in the world; a
/// disconnected actor's next login waits for the operation (<see cref="WaitForCharacterAsync"/>)
/// and loads the committed state. An unreadable outcome keeps the actors quarantined and
/// kicks them, so no stale snapshot can overwrite the durable result.
/// </summary>
public sealed class EconomySettlements(IServiceScopeFactory scopes, ILogger logger, Func<TimeSpan>? budgetSource = null)
{
    public const int MaxConcurrentOperations = 16;

    /// <summary>The shipped settlement budget (<c>Economy:SettlementBudgetSeconds</c>).</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>Wall time one operation (its saves and its commit) may take before it is abandoned; read at each operation.</summary>
    public TimeSpan Budget => budgetSource?.Invoke() ?? DefaultBudget;

    private readonly Lock _gate = new();
    private readonly HashSet<Operation> _operations = [];
    private readonly CancellationTokenSource _stop = new();
    private WorldRuntime? _world;
    private CharacterSaveQueue? _saves;
    private TeleportFeature? _teleports;
    private bool _stopping;
    private Task? _stopped;

    /// <summary>Test seam: runs in the worker after pre-commit saves, before the commit.</summary>
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

    public void Attach(WorldRuntime world, CharacterSaveQueue? saves, TeleportFeature? teleports)
    {
        _world = world;
        _saves = saves;
        _teleports = teleports;
    }

    /// <summary>World thread: whether the player may enter an economy operation now.</summary>
    public bool CanAct(WorldSession session, Player player)
    {
        int id = checked((int)player.Guid.Low);
        return _world is { IsWorldThread: true } world && _saves is { } saves
            && ReferenceEquals(world.FindOnlinePlayer(player.Guid), player)
            && ReferenceEquals(session.Player, player) && session.State == SessionState.InWorld
            && !(_teleports?.Teleports.IsBeingTeleported(player) ?? false)
            && !player.IsQuestSettlementPending && !player.IsLoggingOut && !saves.IsHeld(id) && !saves.IsQuarantined(id);
    }

    /// <summary>
    /// The item GUIDs that leave <paramref name="actor"/> and exist nowhere afterwards: its consumed reagents, plus the items it
    /// handed to another actor whose stage merged them whole into existing stacks (a traded stack; vmangos _StoreItem deletes it).
    /// </summary>
    private static IReadOnlyList<uint> Ended(EconomyActor actor, IReadOnlyList<EconomyActor> actors)
    {
        IReadOnlyList<uint> consumed = actor.Stage.ConsumedItemGuids;
        uint[] merged = [.. actors.Where(other => !ReferenceEquals(other, actor)).SelectMany(other => other.Stage.MergedItemGuids)
            .Where(guid => actor.Before.Inventory!.Items.Any(row => row.Item.Guid == guid)
                && !actor.After.Inventory!.Items.Any(row => row.Item.Guid == guid))];
        return merged.Length == 0 ? consumed : Array.AsReadOnly(consumed.Union(merged).ToArray());
    }

    /// <summary>World thread: freeze the actor's planned money/inventory change.</summary>
    public EconomyActor? CreateActor(WorldSession session, Player player, EconomyInventoryStage stage, uint moneyAfter,
        CharacterLife? lifeAfter = null)
    {
        if (_world is not { } world || !CanAct(session, player))
        {
            return null;
        }

        CharacterState before = player.CreateSnapshot(world.NowMs) with { Inventory = stage.Before };
        return new EconomyActor(session, player, stage, moneyAfter, before, lifeAfter);
    }

    /// <summary>
    /// World thread: start an operation. <paramref name="finished"/> runs on the world thread after
    /// live publication (live = true), or during shutdown without world access (live = false).
    /// Returns false (nothing started, nothing frozen) when capacity, shutdown or an actor refuses.
    /// </summary>
    /// <param name="operationId">The idempotency key; a fresh one when null. A caller that can retry the same logical operation
    /// (the auction house bot's custody ledger) passes a stable key so a replay is AlreadyCommitted, never a second copy.</param>
    public bool TryStart(IReadOnlyList<EconomyActor> actors, IReadOnlyList<EconomyChange> changes, Action<EconomyOutcome, bool> finished,
        Guid? operationId = null)
    {
        ArgumentNullException.ThrowIfNull(actors);
        ArgumentNullException.ThrowIfNull(changes);
        if (_world is not { IsWorldThread: true } || _saves is not { } saves
            || actors.Select(a => a.Id).Distinct().Count() != actors.Count
            || actors.Any(a => !CanAct(a.Session, a.Player)))
        {
            return false;
        }

        Guid id = operationId is { } given && given != Guid.Empty ? given : Guid.NewGuid();
        var request = new EconomyCommitRequest(id,
            actors.Select(a => new EconomyParticipant(a.Before, a.After, Ended(a, actors))).ToArray(), changes.ToArray());
        var operation = new Operation(id, actors, request, finished);
        lock (_gate)
        {
            if (_stopping || _operations.Count >= MaxConcurrentOperations)
            {
                logger.LogWarning("economy settlement capacity reached or stopping; operation refused");
                return false;
            }

            var frozen = new List<EconomyActor>();
            foreach (EconomyActor actor in actors)
            {
                saves.HoldCharacter(actor.Id);
                if (!actor.Player.BeginQuestSettlement(id))
                {
                    saves.ResumeCharacter(actor.Id);
                    foreach (EconomyActor undo in frozen)
                    {
                        undo.Player.EndQuestSettlement(id);
                        saves.ResumeCharacter(undo.Id);
                    }

                    return false;
                }

                frozen.Add(actor);
            }

            _operations.Add(operation);
            operation.Worker = Task.Run(() => SettleAsync(operation));
        }

        return true;
    }

    /// <summary>Completes when no operation involving the character is in flight.</summary>
    public Task WaitForCharacterAsync(int characterId, CancellationToken cancellationToken = default)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = _operations.Where(o => o.Actors.Any(a => a.Id == characterId)).Select(o => o.Completion.Task).ToArray();
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
        EconomyOutcome outcome = EconomyOutcome.NotStarted;
        bool transactionStarted = false;
        TimeSpan allowed = Budget;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            budget.CancelAfter(allowed);
            // The pre-operation snapshot must be durable first: the commit compares the stored
            // money and inventory against it, so a lost earlier save is a conflict, not a dupe.
            await Task.WhenAll(operation.Actors.Select(a => _saves!.SaveForSettlementAsync(a.Before, budget.Token))).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            foreach (EconomyActor actor in operation.Actors)
            {
                _saves!.QuarantineCharacter(actor.Id);
            }

            if (BeforeCommit is { } hook)
            {
                await hook(operation.Id).ConfigureAwait(false);
            }

            transactionStarted = true;
            outcome = EconomyOutcome.Unknown;
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            EconomyCommitResult result = await scope.ServiceProvider.GetRequiredService<IEconomyStore>()
                .CommitAsync(operation.Request, budget.Token).ConfigureAwait(false);
            outcome = result is EconomyCommitResult.Committed or EconomyCommitResult.AlreadyCommitted
                ? EconomyOutcome.After : EconomyOutcome.Before;
            if (outcome == EconomyOutcome.Before)
            {
                logger.LogInformation("economy operation {Operation} was refused by storage ({Result})", operation.Id, result);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is OperationCanceledException && !_stop.IsCancellationRequested)
            {
                // The caller answers the client with its ordinary failure reply (or the actors are kicked when the outcome is unknown).
                logger.LogWarning("economy operation {Operation} ran out of its {Budget}s budget after {Elapsed} ms (transaction started: {Started})",
                    operation.Id, allowed.TotalSeconds, clock.ElapsedMilliseconds, transactionStarted);
            }
            else
            {
                logger.LogError(ex, "economy operation {Operation} failed", operation.Id);
            }
            if (transactionStarted && outcome != EconomyOutcome.After)
            {
                outcome = EconomyOutcome.Unknown;
                try
                {
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    budget.CancelAfter(Budget);
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    outcome = await scope.ServiceProvider.GetRequiredService<IEconomyStore>()
                        .IsCommittedAsync(operation.Id, budget.Token).ConfigureAwait(false) ? EconomyOutcome.After : EconomyOutcome.Before;
                }
                catch (Exception reconciliation) when (reconciliation is not OutOfMemoryException)
                {
                    logger.LogError(reconciliation, "could not reconcile economy operation {Operation}", operation.Id);
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

        try
        {
            foreach (EconomyActor actor in operation.Actors)
            {
                FinalizeActor(operation, actor, publishLive);
            }

            operation.Finished(operation.Outcome, publishLive);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "economy operation {Operation} callback failed", operation.Id);
        }
        finally
        {
            lock (_gate)
            {
                _operations.Remove(operation);
                operation.Completion.TrySetResult();
            }
        }
    }

    private void FinalizeActor(Operation operation, EconomyActor actor, bool publishLive)
    {
        Player player = actor.Player;
        bool current = publishLive && _world is { } world
            && ReferenceEquals(world.FindOnlinePlayer(player.Guid), player)
            && player.QuestSettlementOperationId == operation.Id;
        try
        {
            switch (operation.Outcome)
            {
                case EconomyOutcome.After when current:
                    using (player.BeginQuestSettlementPublication(operation.Id))
                    {
                        player.Money = actor.MoneyAfter;
                        if (actor.AppliesLife && actor.After.Life is { } life)
                        {
                            player.Health = life.Health;
                            for (int i = 0; i < life.Powers.Count; i++)
                                player.SetUInt32(UpdateFields.UnitFieldPower1 + i, life.Powers[i]);
                        }
                        player.Inventory.ApplyEconomyTransfer(actor.Stage);
                    }

                    player.EndQuestSettlement(operation.Id);
                    _saves!.ResumeCharacter(actor.Id);
                    if (actor.Before.Money != actor.MoneyAfter)
                    {
                        // Cash objectives use the durable wallet after the live freeze ends.
                        actor.Session.Services.GetService<QuestNpcFeature>()?.Services.MoneyChanged(player);
                    }

                    break;
                case EconomyOutcome.Before or EconomyOutcome.NotStarted when current:
                    player.EndQuestSettlement(operation.Id);
                    _saves!.ResumeCharacter(actor.Id);
                    break;
                case EconomyOutcome.Unknown when current:
                    actor.Session.Kick();
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The durable state is authoritative; the live copy is not trustworthy any more.
            _saves!.QuarantineCharacter(actor.Id);
            logger.LogError(ex, "economy publication failed for character {Character}; fresh login is required", actor.Id);
            if (current)
            {
                actor.Session.Kick();
            }
        }
    }

    private sealed class Operation(Guid id, IReadOnlyList<EconomyActor> actors, EconomyCommitRequest request, Action<EconomyOutcome, bool> finished)
    {
        public Guid Id { get; } = id;
        public IReadOnlyList<EconomyActor> Actors { get; } = actors;
        public EconomyCommitRequest Request { get; } = request;
        public Action<EconomyOutcome, bool> Finished { get; } = finished;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker { get; set; } = Task.CompletedTask;
        public EconomyOutcome Outcome { get; set; }
        public bool Finalizing { get; set; }
    }
}
