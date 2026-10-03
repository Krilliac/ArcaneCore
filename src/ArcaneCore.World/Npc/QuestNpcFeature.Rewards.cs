using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

public sealed partial class QuestNpcFeature
{
    public const int MaxConcurrentSettlements = 8;

    private readonly Lock _settlementGate = new();
    private readonly Dictionary<int, QuestSettlement> _settlements = [];
    private readonly CancellationTokenSource _settlementStop = new();
    private bool _settlementsStopping;
    private Task? _settlementsStopped;

    public int PendingSettlementCount
    {
        get
        {
            lock (_settlementGate)
            {
                return _settlements.Count;
            }
        }
    }

    public Guid? PendingOperationId(int characterId)
    {
        lock (_settlementGate)
        {
            return _settlements.GetValueOrDefault(characterId)?.OperationId;
        }
    }

    /// <summary>
    /// Wait for storage and its world-thread publication. Shutdown finalizes the cache after
    /// observing storage, without requiring another tick from the stopped world.
    /// </summary>
    public Task WaitForSettlementAsync(int characterId, CancellationToken cancellationToken = default)
    {
        Task done;
        lock (_settlementGate)
        {
            done = _settlements.GetValueOrDefault(characterId)?.Completion.Task ?? Task.CompletedTask;
        }

        return done.WaitAsync(cancellationToken);
    }

    /// <summary>Capture and freeze one character on the world thread; every storage operation runs in the background.</summary>
    public void ChooseReward(WorldSession session, Player player, ObjectGuid guid, uint questId, uint choice)
    {
        CharacterSaveQueue? saves = _services.GetService<CharacterSaveQueue>();
        int id = checked((int)player.Guid.Low);
        if (_world is not { } world || !world.IsWorldThread || saves is null
            || !ReferenceEquals(world.FindOnlinePlayer(player.Guid), player)
            || !ReferenceEquals(session.Player, player) || session.State != SessionState.InWorld
            || (_services.GetService<TeleportFeature>()?.Teleports.IsBeingTeleported(player) ?? false)
            || player.IsQuestSettlementPending || saves.IsHeld(id) || saves.IsQuarantined(id))
        {
            return;
        }

        lock (_settlementGate)
        {
            if (_settlementsStopping || _settlements.ContainsKey(id))
            {
                return;
            }

            if (_settlements.Count >= MaxConcurrentSettlements)
            {
                _logger.LogWarning("quest reward settlement capacity reached; character {Character} remains active", id);
                return;
            }

            if (!Services.TryPrepareReward(player, guid, questId, choice, out QuestRewardPlan? plan))
            {
                return;
            }

            Guid operationId = Guid.NewGuid();
            CharacterState before = player.CreateSnapshot(world.NowMs) with { Inventory = plan.BeforeInventory };
            var request = new CharacterQuestRewardRequest(before,
                before with { Money = plan.MoneyAfter, Inventory = plan.InventoryAfter },
                plan.ExpectedQuest, plan.RewardedQuest);
            saves.HoldCharacter(id);
            if (!player.BeginQuestSettlement(operationId))
            {
                saves.ResumeCharacter(id);
                return;
            }

            var operation = new QuestSettlement(operationId, session, player, plan, request, saves);
            _settlements.Add(id, operation);
            operation.Worker = Task.Run(() => SettleRewardAsync(operation));
        }
    }

    private async Task SettleRewardAsync(QuestSettlement operation)
    {
        int id = operation.Request.Before.Id;
        RewardOutcome outcome = RewardOutcome.NotStarted;
        bool transactionStarted = false;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_settlementStop.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            // These tasks remain observed even if a store ignores cooperative cancellation.
            await operation.Saves.SaveForSettlementAsync(operation.Request.Before, budget.Token).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            await Persistence.FlushCharacterAsync(id).ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            operation.Saves.QuarantineCharacter(id);
            Persistence.QuarantineCharacter(id);
            transactionStarted = true;
            outcome = RewardOutcome.Unknown;
            await using (AsyncServiceScope scope = _scopes.CreateAsyncScope())
            {
                QuestRewardCommitResult result = await scope.ServiceProvider.GetRequiredService<ICharacterQuestRewardStore>()
                    .CommitAsync(operation.Request, budget.Token).ConfigureAwait(false);
                outcome = result == QuestRewardCommitResult.Committed
                    ? RewardOutcome.After : await ReadRewardOutcomeAsync(operation.Request).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "quest reward settlement failed for character {Character}, quest {Quest}", id, operation.Plan.QuestId);
            if (transactionStarted && outcome != RewardOutcome.After)
            {
                outcome = RewardOutcome.Unknown;
                try
                {
                    outcome = await ReadRewardOutcomeAsync(operation.Request).ConfigureAwait(false);
                }
                catch (Exception reconciliationError) when (reconciliationError is not OutOfMemoryException)
                {
                    _logger.LogError(reconciliationError, "could not reconcile quest reward for character {Character}", id);
                }
            }
        }
        finally
        {
            lock (_settlementGate)
            {
                operation.Outcome = outcome;
                if (!_settlementsStopping)
                {
                    _world!.Post(() => FinalizeSettlement(operation, publishLive: true));
                }
            }
        }
    }

    private void FinalizeSettlement(QuestSettlement operation, bool publishLive)
    {
        int id = operation.Request.Before.Id;
        lock (_settlementGate)
        {
            if (!ReferenceEquals(_settlements.GetValueOrDefault(id), operation) || operation.Finalizing)
            {
                return;
            }

            operation.Finalizing = true;
        }

        Player player = operation.Player;
        bool current = publishLive && _world is { } world
            && ReferenceEquals(world.FindOnlinePlayer(player.Guid), player)
            && player.QuestSettlementOperationId == operation.OperationId;
        try
        {
            if (operation.Outcome == RewardOutcome.After)
            {
                // Durable history is adopted even after disconnect; the detached old Player
                // must never be applied to a replacement or written back during shutdown.
                Persistence.AdoptRewarded(operation.Plan.RewardedQuest);
                if (current)
                {
                    using (player.BeginQuestSettlementPublication(operation.OperationId))
                    {
                        // The cached rewarded row is already authoritative. Ordinary
                        // objective deltas produced by publication may now persist.
                        Persistence.ResumeCharacter(id);
                        Services.ApplyReward(operation.Plan);
                    }

                    player.EndQuestSettlement(operation.OperationId);
                    Persistence.ResumeCharacter(id);
                    operation.Saves.ResumeCharacter(id);
                }
            }
            else if (operation.Outcome is RewardOutcome.Before or RewardOutcome.NotStarted)
            {
                if (current)
                {
                    player.EndQuestSettlement(operation.OperationId);
                    Persistence.ResumeCharacter(id);
                    operation.Saves.ResumeCharacter(id);
                }
            }
            else if (current)
            {
                operation.Session.Kick();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            operation.Saves.QuarantineCharacter(id);
            Persistence.QuarantineCharacter(id);
            _logger.LogError(ex, "quest reward publication failed for character {Character}; fresh login is required", id);
            if (current)
            {
                operation.Session.Kick();
            }
        }
        finally
        {
            lock (_settlementGate)
            {
                _settlements.Remove(id);
                operation.Completion.TrySetResult();
            }
        }
    }

    private Task StopSettlementsAsync()
    {
        lock (_settlementGate)
        {
            _settlementsStopping = true;
            return _settlementsStopped ??= StopSettlementsCoreAsync();
        }
    }

    private async Task StopSettlementsCoreAsync()
    {
        await Task.Yield();
        _settlementStop.Cancel();
        QuestSettlement[] operations;
        lock (_settlementGate)
        {
            operations = _settlements.Values.ToArray();
        }

        try
        {
            await Task.WhenAll(operations.Select(o => o.Worker)).ConfigureAwait(false);
        }
        finally
        {
            foreach (QuestSettlement operation in operations)
            {
                FinalizeSettlement(operation, publishLive: false);
            }

            _settlementStop.Dispose();
        }
    }

    private async Task<RewardOutcome> ReadRewardOutcomeAsync(CharacterQuestRewardRequest request)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_settlementStop.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        int id = request.Before.Id;
        CharacterRecord? character = await scope.ServiceProvider.GetRequiredService<ICharacterStore>()
            .GetByIdAsync(id, budget.Token).ConfigureAwait(false);
        CharacterQuestData journal = await scope.ServiceProvider.GetRequiredService<ICharacterQuestStore>()
            .LoadAsync(id, budget.Token).ConfigureAwait(false);
        IReadOnlyList<InventoryItemData> inventory = await scope.ServiceProvider.GetRequiredService<IItemStore>()
            .GetInventoryAsync(id, budget.Token).ConfigureAwait(false);
        CharacterQuestStatus? quest = journal.Quests.SingleOrDefault(q => q.Quest == request.ExpectedQuest.Quest);
        if (character is null)
        {
            return RewardOutcome.Unknown;
        }

        if (quest == request.ExpectedQuest && character.Money == request.Before.Money
            && SameInventory(inventory, request.Before.Inventory!.Items))
        {
            return RewardOutcome.Before;
        }

        return quest == request.RewardedQuest && character.Money == request.After.Money
            && SameInventory(inventory, request.After.Inventory!.Items) ? RewardOutcome.After : RewardOutcome.Unknown;
    }

    private static bool SameInventory(IReadOnlyList<InventoryItemData> first, IReadOnlyList<InventoryItemData> second)
    {
        InventoryItemData[] left = first.OrderBy(i => i.Item.Guid).ToArray();
        InventoryItemData[] right = second.OrderBy(i => i.Item.Guid).ToArray();
        return left.Length == right.Length && left.Zip(right).All(pair =>
            pair.First.ContainerGuid == pair.Second.ContainerGuid && pair.First.Slot == pair.Second.Slot
            && (pair.First.Item with { Charges = pair.Second.Item.Charges, Enchantments = pair.Second.Item.Enchantments }) == pair.Second.Item
            && pair.First.Item.Charges.SequenceEqual(pair.Second.Item.Charges)
            && pair.First.Item.Enchantments.SequenceEqual(pair.Second.Item.Enchantments));
    }

    private enum RewardOutcome { NotStarted, Before, After, Unknown }

    private sealed class QuestSettlement(Guid operationId, WorldSession session, Player player,
        QuestRewardPlan plan, CharacterQuestRewardRequest request, CharacterSaveQueue saves)
    {
        public Guid OperationId { get; } = operationId;
        public WorldSession Session { get; } = session;
        public Player Player { get; } = player;
        public QuestRewardPlan Plan { get; } = plan;
        public CharacterQuestRewardRequest Request { get; } = request;
        public CharacterSaveQueue Saves { get; } = saves;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker { get; set; } = Task.CompletedTask;
        public RewardOutcome Outcome { get; set; }
        public bool Finalizing { get; set; }
    }
}
