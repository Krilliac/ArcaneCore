using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Reputation;
using ArcaneCore.World.Spells;
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
            // A quest level-up is part of the reward transaction (its played-time-at-level restarts).
            var request = new CharacterQuestRewardRequest(before,
                before with
                {
                    Money = plan.MoneyAfter, Inventory = plan.InventoryAfter, Level = plan.LevelAfter,
                    LevelPlayedTime = plan.LevelAfter != plan.LevelBefore ? 0 : before.LevelPlayedTime,
                },
                plan.ExpectedQuest, plan.RewardedQuest, plan.SpellGrant.LearnedSpells.ToArray(), plan.Reputation.After.ToArray());
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
            // Drain the owners whose rows the reward transaction also writes. Anything queued for this
            // character is behind us, and the hold guards keep new writes out. A failed spellbook
            // retry throws here: no transaction starts and the journal stays intact.
            if (operation.Request.LearnedSpells is { Count: > 0 } && _services.GetService<SpellFeature>() is { } spellFeature)
            {
                await spellFeature.Spellbook.FlushCharacterAsync(id).WaitAsync(budget.Token).ConfigureAwait(false);
            }

            if (operation.Request.ReputationAfter is { Count: > 0 } && _services.GetService<ReputationFeature>() is { } reputationFeature)
            {
                // The write queue retains failed writes (docs/integration/reputation.md). Retry this character's
                // once more before the transaction, so a retained older row cannot later overwrite the rows the
                // reward writes; it throws while they are still not durable: no transaction, journal intact.
                await reputationFeature.FlushCharacterAsync(id).WaitAsync(budget.Token).ConfigureAwait(false);
            }

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
                if (operation.Plan.SpellGrant.LearnedSpells is { Count: > 0 } learned
                    && _services.GetService<SpellFeature>() is { } spellFeature)
                {
                    // The rows are committed: the cached book follows without queueing a write. A replacement
                    // Player (or a later login) loads the same rows from storage, so nothing is replayed to it.
                    spellFeature.Spellbook.AdoptCommitted(id, learned.ToArray());
                }

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
                    // Reward spells and reputation need the released character.
                    Services.PublishRewardEffects(operation.Plan);
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

        // The spell and faction rows commit with the journal in one transaction, so Before needs no
        // check of them; After additionally requires every row the reward writes to be present as written.
        bool grantsPresent = true;
        if (request.LearnedSpells is { Count: > 0 } learned)
        {
            IReadOnlyList<uint> spells = await scope.ServiceProvider.GetRequiredService<ICharacterSpellStore>()
                .GetAsync(id, budget.Token).ConfigureAwait(false);
            grantsPresent = learned.All(spells.Contains);
        }

        if (grantsPresent && request.ReputationAfter is { Count: > 0 } factions)
        {
            CharacterReputationData stored = await scope.ServiceProvider.GetRequiredService<ICharacterReputationStore>()
                .LoadAsync(id, budget.Token).ConfigureAwait(false);
            grantsPresent = factions.All(expected => stored.Factions.Any(row => row.Faction == expected.Faction
                && row.Standing == expected.Standing && row.Flags == expected.Flags));
        }

        if (quest == request.ExpectedQuest && character.Money == request.Before.Money && character.Level == request.Before.Level
            && SameInventory(inventory, request.Before.Inventory!.Items))
        {
            return RewardOutcome.Before;
        }

        return grantsPresent && quest == request.RewardedQuest && character.Money == request.After.Money && character.Level == request.After.Level
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
