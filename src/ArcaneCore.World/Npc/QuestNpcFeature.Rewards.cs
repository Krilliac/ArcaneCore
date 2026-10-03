using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.World.Net;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Npc;

public sealed partial class QuestNpcFeature
{
    /// <summary>
    /// A deliberately local settlement: the world thread excludes all live mutations until
    /// the transaction and its outcome are observed. Cancellation is cooperative; never
    /// abandon a commit task and resume simulation while it might still commit.
    /// </summary>
    public void ChooseReward(WorldSession session, Player player, ObjectGuid guid,
        uint questId, uint choice)
    {
        CharacterSaveQueue? saves = _services.GetService<CharacterSaveQueue>();
        int id = checked((int)player.Guid.Low);
        if (_world is null || !_world.IsWorldThread || saves is null || saves.IsQuarantined(id)
            || !Services.TryPrepareReward(player, guid, questId, choice, out QuestRewardPlan? plan))
        {
            return;
        }

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        CharacterState before = player.CreateSnapshot(_world.NowMs) with { Inventory = plan.BeforeInventory };
        var request = new CharacterQuestRewardRequest(before,
            before with { Money = plan.MoneyAfter, Inventory = plan.InventoryAfter },
            plan.ExpectedQuest, plan.RewardedQuest);
        bool settlementStarted = false;
        bool durable = false;
        try
        {
            saves.Enqueue(before);
            Persistence.FlushCharacterAsync(id).WaitAsync(budget.Token).GetAwaiter().GetResult();
            saves.FlushCharacterAsync(id, budget.Token).GetAwaiter().GetResult();
            budget.Token.ThrowIfCancellationRequested();
            // Already queued snapshots have drained. Block shutdown/disconnect snapshots
            // if the commit acknowledgement or subsequent live publication fails.
            saves.QuarantineCharacter(id);
            settlementStarted = true;
            using IServiceScope scope = _scopes.CreateScope();
            QuestRewardCommitResult result = scope.ServiceProvider.GetRequiredService<ICharacterQuestRewardStore>()
                .CommitAsync(request, budget.Token).GetAwaiter().GetResult();
            if (result == QuestRewardCommitResult.Committed)
            {
                durable = true;
            }
            else
            {
                RewardOutcome outcome = ReadRewardOutcomeAsync(request).GetAwaiter().GetResult();
                if (outcome == RewardOutcome.Before)
                {
                    saves.ResumeCharacter(id);
                    return;
                }

                durable = outcome == RewardOutcome.After;
                if (!durable)
                {
                    session.Kick();
                    return;
                }
            }

            Persistence.AdoptRewarded(plan.RewardedQuest);
            Services.ApplyReward(plan);
            saves.ResumeCharacter(id);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "quest reward settlement failed for character {Character}, quest {Quest}", id, questId);
            if (!settlementStarted)
            {
                return; // No transaction started and the live plan has not been applied.
            }

            if (!durable)
            {
                try
                {
                    RewardOutcome outcome = ReadRewardOutcomeAsync(request).GetAwaiter().GetResult();
                    if (outcome == RewardOutcome.Before)
                    {
                        saves.ResumeCharacter(id);
                        return;
                    }

                    if (outcome == RewardOutcome.After)
                    {
                        Persistence.AdoptRewarded(plan.RewardedQuest);
                        Services.ApplyReward(plan);
                        saves.ResumeCharacter(id);
                        return;
                    }
                }
                catch (Exception reconciliationError) when (reconciliationError is not OutOfMemoryException)
                {
                    _logger.LogError(reconciliationError, "could not reconcile quest reward for character {Character}", id);
                }
            }

            // A fresh login loads every authoritative feature before resuming core saves.
            // Never publish this potentially partial live character back into storage.
            session.Kick();
        }
    }

    private async Task<RewardOutcome> ReadRewardOutcomeAsync(CharacterQuestRewardRequest request)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    private enum RewardOutcome { Before, After, Unknown }
}
