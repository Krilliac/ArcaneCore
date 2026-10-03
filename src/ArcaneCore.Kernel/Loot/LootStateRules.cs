using ArcaneCore.Kernel.Economy;

namespace ArcaneCore.Kernel.Loot;

/// <summary>
/// The one implementation of how a stored chest may change. The game computes the Updated
/// record of an operation with <see cref="Replay"/> / <see cref="Take"/>, and the store
/// accepts a commit only when <see cref="IsLegalSuccessor"/> (the same functions) reproduces
/// Updated from Expected and the awards. A stale or forged Updated can therefore never
/// re-take a slot, grant an item twice or move state backwards. Pure functions.
/// </summary>
public static class LootStateRules
{
    /// <summary>The most stacks a chest holds (MAX_NR_LOOT_ITEMS + MAX_NR_QUEST_ITEMS).</summary>
    public const int MaxItems = 48;

    public static bool SameSet(IReadOnlyList<int> first, IReadOnlyList<int> second)
        => new HashSet<int>(first).SetEquals(second);

    /// <summary>
    /// Whether the character may take <paramref name="item"/> now (the rule of LootBag.SlotFor):
    /// a recipient (an empty list means anyone), the stack is not taken, a quest stack needs the
    /// character in its allowed list, per-player and quest stacks are taken once per character,
    /// and a shared stack belongs to the round-robin owner when there is one.
    /// </summary>
    public static bool IsAvailableTo(LootStateRecord record, LootStateItem item, int characterId)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(item);
        if ((record.Recipients.Count != 0 && !record.Recipients.Contains(characterId)) || item.IsLooted)
        {
            return false;
        }

        if (item.IsQuest && !item.AllowedLooters.Contains(characterId))
        {
            return false;
        }

        if (item.IsPerPlayer || item.IsQuest)
        {
            return !item.LootedBy.Contains(characterId);
        }

        return record.LootOwnerCharacterId == 0 || record.LootOwnerCharacterId == characterId;
    }

    /// <summary>
    /// The record after <paramref name="characterId"/> takes the stack in <paramref name="slot"/>
    /// (LootBag.MarkTaken): a quest stack is looted once every allowed character took it, a
    /// per-player stack once every recipient did, any other stack at once. When every stack is
    /// taken the record becomes consumed and respawns at <paramref name="respawnAtUnix"/>.
    /// Null when the stack does not exist or is not available to the character.
    /// </summary>
    public static LootStateRecord? Take(LootStateRecord record, int characterId, byte slot, long respawnAtUnix)
    {
        ArgumentNullException.ThrowIfNull(record);
        LootStateItem? item = record.Items.FirstOrDefault(i => i.Slot == slot);
        if (item is null || record.Consumed || !IsAvailableTo(record, item, characterId))
        {
            return null;
        }

        LootStateItem taken;
        if (item.IsQuest)
        {
            int[] by = [.. item.LootedBy, characterId];
            taken = item with { LootedBy = by, IsLooted = item.AllowedLooters.All(by.Contains) };
        }
        else if (item.IsPerPlayer)
        {
            int[] by = [.. item.LootedBy, characterId];
            taken = item with { LootedBy = by, IsLooted = record.Recipients.Count > 0 && record.Recipients.All(by.Contains) };
        }
        else
        {
            taken = item with { IsLooted = true };
        }

        LootStateItem[] items = [.. record.Items.Select(i => i.Slot == slot ? taken : i)];
        bool consumed = items.All(i => i.IsLooted);
        return record with { Items = items, Consumed = consumed, RespawnAtUnix = consumed ? respawnAtUnix : record.RespawnAtUnix };
    }

    /// <summary>
    /// The state after <paramref name="awards"/> applied in order to a live (not consumed)
    /// <paramref name="expected"/>: the recipients first grow to <paramref name="recipients"/>
    /// (only characters that take something join, as LootService.OpenGameObject adds a late
    /// opener) and the round-robin owner may be cleared (released) but never set; then each
    /// award takes its stack, which must match the stored item and count. Null when illegal.
    /// </summary>
    public static LootStateRecord? Replay(LootStateRecord expected, IReadOnlyList<int> recipients, int lootOwner,
        IReadOnlyList<LootAward> awards, long respawnAtUnix)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(awards);
        if (expected.Consumed || (lootOwner != expected.LootOwnerCharacterId && lootOwner != 0))
        {
            return null;
        }

        var wanted = new HashSet<int>(recipients);
        if (!wanted.IsSupersetOf(expected.Recipients))
        {
            return null;
        }

        wanted.ExceptWith(expected.Recipients);
        if (!wanted.IsSubsetOf(awards.Select(a => a.CharacterId)))
        {
            return null;
        }

        LootStateRecord? state = expected with { Recipients = [.. recipients.Distinct().Order()], LootOwnerCharacterId = lootOwner };
        foreach (LootAward award in awards)
        {
            LootStateItem? item = state.Items.FirstOrDefault(i => i.Slot == award.Slot);
            if (item is null || item.ItemId != award.ItemId || item.Count != award.Count)
            {
                return null;
            }

            state = Take(state, award.CharacterId, award.Slot, respawnAtUnix);
            if (state is null)
            {
                return null;
            }
        }

        return state;
    }

    /// <summary>
    /// Whether <paramref name="updated"/> may replace <paramref name="expected"/> given
    /// <paramref name="awards"/>: a fresh generation (the first one, or the next one after the
    /// chest was consumed) with no awards, or the exact result of <see cref="Replay"/> within the
    /// same generation.
    /// </summary>
    public static bool IsLegalSuccessor(LootStateRecord? expected, LootStateRecord updated, IReadOnlyList<LootAward> awards)
    {
        ArgumentNullException.ThrowIfNull(updated);
        ArgumentNullException.ThrowIfNull(awards);
        if (updated.Key.InstanceId == 0 || (updated.Consumed && updated.RespawnAtUnix <= 0))
        {
            return false;
        }

        if (expected is null || expected.Consumed)
        {
            return updated.Generation == (expected?.Generation ?? 0) + 1 && (expected is null || expected.Key == updated.Key)
                && awards.Count == 0 && IsFreshGeneration(updated);
        }

        if (expected.Key != updated.Key || updated.Generation != expected.Generation || updated.SourceEntry != expected.SourceEntry)
        {
            return false;
        }

        LootStateRecord? replayed = Replay(expected, updated.Recipients, updated.LootOwnerCharacterId, awards, updated.RespawnAtUnix);
        return replayed is not null && replayed.Equals(updated);
    }

    /// <summary>A just-generated chest: nothing taken, every stack well formed, consumed only when empty.</summary>
    public static bool IsFreshGeneration(LootStateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Items.Count > MaxItems || record.Items.Select(i => i.Slot).Distinct().Count() != record.Items.Count
            || record.Consumed != (record.Items.Count == 0) || record.Consumed != (record.RespawnAtUnix > 0)
            || (record.LootOwnerCharacterId != 0 && !record.Recipients.Contains(record.LootOwnerCharacterId)))
        {
            return false;
        }

        return record.Items.All(i => i.ItemId != 0 && i.Count != 0 && !i.IsLooted && i.LootedBy.Count == 0
            && (!i.IsQuest || i.AllowedLooters.Count > 0) && (i.IsQuest || i.AllowedLooters.Count == 0));
    }

    /// <summary>
    /// The participants' inventory and money differences are exactly the awards: every awarded
    /// character participates, each item entry's count grows by the awarded total and nothing
    /// else changes, and no participant's money changes (chests hold no money).
    /// </summary>
    public static bool AwardsMatchParticipants(IReadOnlyList<LootAward> awards, IReadOnlyList<EconomyParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(awards);
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Select(p => p.Before.Id).Distinct().Count() != participants.Count
            || awards.Any(a => participants.All(p => p.Before.Id != a.CharacterId)))
        {
            return false;
        }

        foreach (EconomyParticipant participant in participants)
        {
            if (participant.Before.Inventory is null || participant.After.Inventory is null
                || participant.Before.Money != participant.After.Money)
            {
                return false;
            }

            Dictionary<uint, long> delta = [];
            foreach (var row in participant.After.Inventory.Items)
            {
                delta[row.Item.Entry] = delta.GetValueOrDefault(row.Item.Entry) + row.Item.Count;
            }

            foreach (var row in participant.Before.Inventory.Items)
            {
                delta[row.Item.Entry] = delta.GetValueOrDefault(row.Item.Entry) - row.Item.Count;
            }

            Dictionary<uint, long> awarded = [];
            foreach (LootAward award in awards.Where(a => a.CharacterId == participant.Before.Id))
            {
                awarded[award.ItemId] = awarded.GetValueOrDefault(award.ItemId) + award.Count;
            }

            if (awarded.Count == 0
                || delta.Where(d => d.Value != 0).Any(d => awarded.GetValueOrDefault(d.Key) != d.Value)
                || awarded.Any(a => delta.GetValueOrDefault(a.Key) != a.Value))
            {
                return false;
            }
        }

        return true;
    }
}
