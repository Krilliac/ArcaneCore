using System.Data;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Quests;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Quests;

/// <summary>
/// A single local database transaction for an ordinary quest reward. A nonrepeatable reward
/// keeps status COMPLETE and is refused once its durable row is rewarded; a repeatable reward
/// (requested with status NONE, vmangos RewardQuest) may follow earlier rewards but still only
/// claims the exact completed row it observed, so a duplicate claim conflicts.
/// Serializable isolation makes concurrent claims contend on the same quest history;
/// a provider serialization failure rolls back and a retry checks the durable guard.
/// Character and quest queues must be drained by the caller before invoking this store.
/// </summary>
public sealed class EfCharacterQuestRewardStore(CharacterDbContext db) : ICharacterQuestRewardStore
{
    public async Task<QuestRewardCommitResult> CommitAsync(
        CharacterQuestRewardRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (db.ChangeTracker.Entries().Any()
            || db.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            throw new InvalidOperationException("Quest rewards require a dedicated context without tracked caller state or a caller transaction.");
        }

        // Copy before the first await: callers may subsequently reuse their lists.
        request = request with
        {
            Before = Copy(request.Before),
            After = Copy(request.After),
            // Detached copies: the caller may reuse its lists, and the loops below must not see them change.
            LearnedSpells = request.LearnedSpells is { } spells ? [.. spells] : [],
            ReputationAfter = request.ReputationAfter is { } rows ? [.. rows] : [],
        };
        Validate(request);
        if ((request.LearnedSpells!.Count > 0 && db.Model.FindEntityType(typeof(CharacterSpellRow)) is null)
            || (request.ReputationAfter!.Count > 0 && db.Model.FindEntityType(typeof(CharacterReputationEntity)) is null))
        {
            throw new InvalidOperationException("The reward carries spell or reputation grants but the character model has no such table.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            int id = request.Before.Id;
            CharacterRecord? character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
                .ConfigureAwait(false);
            if (character is null)
            {
                return QuestRewardCommitResult.CharacterMissing;
            }

            CharacterQuestStatusRow? row = await db.Set<CharacterQuestStatusRow>()
                .FirstOrDefaultAsync(r => r.CharacterId == id && r.Quest == request.ExpectedQuest.Quest, cancellationToken)
                .ConfigureAwait(false);
            bool repeatable = request.RewardedQuest.Status == 0;
            if (row?.Rewarded == true && (!repeatable || row.Status != 1))
            {
                return QuestRewardCommitResult.AlreadyRewarded;
            }

            if (row is null || ToStatus(row) != request.ExpectedQuest || character.Money != request.Before.Money)
            {
                return QuestRewardCommitResult.Conflict;
            }

            IReadOnlyList<InventoryItemData> stored = await new EfItemStore(db).GetInventoryAsync(id, cancellationToken)
                .ConfigureAwait(false);
            if (!SameInventory(stored, request.Before.Inventory!.Items))
            {
                return QuestRewardCommitResult.Conflict;
            }

            List<uint> guids = request.After.Inventory!.Items.Select(i => i.Item.Guid).ToList();
            // General inventory persistence permits transfers. Quest rewards must never
            // reassign a GUID owned or placed under another character.
            if (await db.Set<ItemInstanceRow>().AsNoTracking()
                    .AnyAsync(r => guids.Contains(r.Guid) && r.OwnerGuid != id, cancellationToken).ConfigureAwait(false)
                || await db.Set<CharacterInventoryRow>().AsNoTracking()
                    .AnyAsync(r => guids.Contains(r.ItemGuid) && r.Guid != id, cancellationToken).ConfigureAwait(false))
            {
                return QuestRewardCommitResult.Conflict;
            }

            // The spell and faction rows are read and written through the context's sets, never through
            // their module stores: those call SaveChanges and clear the tracker, which would flush or
            // drop the staged character, inventory and quest changes before the single save below.
            List<uint> learned = [.. request.LearnedSpells!];
            Dictionary<uint, CharacterReputationEntity> factions = [];
            HashSet<uint> known = [];
            if (learned.Count > 0)
            {
                known = [.. await db.Set<CharacterSpellRow>().AsNoTracking()
                    .Where(r => r.CharacterId == id && learned.Contains(r.Spell))
                    .Select(r => r.Spell).ToListAsync(cancellationToken).ConfigureAwait(false)];
            }

            List<uint> touched = [.. request.ReputationAfter!.Select(r => r.Faction)];
            if (touched.Count > 0)
            {
                factions = await db.Set<CharacterReputationEntity>()
                    .Where(r => r.CharacterId == id && touched.Contains(r.Faction))
                    .ToDictionaryAsync(r => r.Faction, cancellationToken).ConfigureAwait(false);
            }

            await new EfCharacterStore(db).StageStateAsync(request.After, cancellationToken).ConfigureAwait(false);
            CopyStatus(row, request.RewardedQuest);
            foreach (uint spell in learned.Where(spell => !known.Contains(spell)))
            {
                db.Set<CharacterSpellRow>().Add(new CharacterSpellRow { CharacterId = id, Spell = spell });
            }

            foreach (CharacterReputationRow faction in request.ReputationAfter!)
            {
                if (factions.TryGetValue(faction.Faction, out CharacterReputationEntity? entity))
                {
                    entity.Standing = faction.Standing;
                    entity.Flags = faction.Flags;
                }
                else
                {
                    db.Set<CharacterReputationEntity>().Add(new CharacterReputationEntity
                    {
                        CharacterId = id, Faction = faction.Faction, Standing = faction.Standing, Flags = faction.Flags,
                    });
                }
            }

            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return QuestRewardCommitResult.Committed;
        }
        catch (Exception rewardError)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Quest reward and rollback failed; discard the context.", rewardError, rollbackError);
            }

            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static CharacterState Copy(CharacterState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with
        {
            ActionButtons = state.ActionButtons?.ToArray(),
            Inventory = state.Inventory is { } inventory
                ? new InventorySnapshot(inventory.Items.Select(i => i with
                {
                    Item = i.Item with { Charges = i.Item.Charges.ToArray(), Enchantments = i.Item.Enchantments.ToArray() },
                }).ToArray(), inventory.AmmoId)
                : null,
        };
    }

    private static void Validate(CharacterQuestRewardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.ExpectedQuest);
        ArgumentNullException.ThrowIfNull(request.RewardedQuest);
        int id = request.Before.Id;
        // Status 0 (NONE) marks a repeatable reward, which may already carry an older reward.
        bool repeatable = request.RewardedQuest.Status == 0;
        if (id <= 0 || request.After.Id != id || request.ExpectedQuest.CharacterId != id
            || request.RewardedQuest.CharacterId != id || request.ExpectedQuest.Quest == 0
            || request.ExpectedQuest.Status != 1 || (request.ExpectedQuest.Rewarded && !repeatable)
            || request.RewardedQuest.Status is not (0 or 1) || !request.RewardedQuest.Rewarded || request.RewardedQuest.Timer != 0
            || (request.RewardedQuest with
            {
                Status = 1, Rewarded = request.ExpectedQuest.Rewarded, Timer = request.ExpectedQuest.Timer,
                RewardChoice = request.ExpectedQuest.RewardChoice,
            }) != request.ExpectedQuest)
        {
            throw new ArgumentException("A reward must preserve a completed quest's identity and counters and set its rewarded history.", nameof(request));
        }

        ValidateInventory(request.Before.Inventory);
        ValidateInventory(request.After.Inventory);
        IReadOnlyList<uint> spells = request.LearnedSpells ?? [];
        IReadOnlyList<CharacterReputationRow> factions = request.ReputationAfter ?? [];
        if (spells.Any(s => s == 0) || spells.Distinct().Count() != spells.Count
            || factions.Any(f => f is null || f.CharacterId != id || f.Faction == 0)
            || factions.Select(f => f.Faction).Distinct().Count() != factions.Count)
        {
            throw new ArgumentException("Learned spells must be positive and distinct; faction rows must belong to the character, one per faction.", nameof(request));
        }
    }

    private static void ValidateInventory(InventorySnapshot? inventory)
    {
        if (inventory is null || inventory.Items.Any(i => i.Item.Guid == 0 || i.Item.Entry == 0 || i.Item.Count == 0)
            || inventory.Items.Select(i => i.Item.Guid).Distinct().Count() != inventory.Items.Count
            || inventory.Items.Select(i => (i.ContainerGuid, i.Slot)).Distinct().Count() != inventory.Items.Count)
        {
            throw new ArgumentException("A reward requires complete inventories with unique item GUIDs and positions and positive item counts.");
        }
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

    private static CharacterQuestStatus ToStatus(CharacterQuestStatusRow row) => new(
        row.CharacterId, row.Quest, row.Status, row.Rewarded, row.Explored, row.Timer,
        row.MobCount1, row.MobCount2, row.MobCount3, row.MobCount4,
        row.ItemCount1, row.ItemCount2, row.ItemCount3, row.ItemCount4, row.RewardChoice);

    private static void CopyStatus(CharacterQuestStatusRow row, CharacterQuestStatus status)
    {
        row.Status = status.Status;
        row.Rewarded = status.Rewarded;
        row.Explored = status.Explored;
        row.Timer = status.Timer;
        row.MobCount1 = status.MobCount1;
        row.MobCount2 = status.MobCount2;
        row.MobCount3 = status.MobCount3;
        row.MobCount4 = status.MobCount4;
        row.ItemCount1 = status.ItemCount1;
        row.ItemCount2 = status.ItemCount2;
        row.ItemCount3 = status.ItemCount3;
        row.ItemCount4 = status.ItemCount4;
        row.RewardChoice = status.RewardChoice;
    }
}
