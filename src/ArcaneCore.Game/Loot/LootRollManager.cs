using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Loot;

/// <summary>
/// The need/greed rolls of one map's loot: group loot and need before greed (vmangos Group::StartLootRoll, CountRollVote,
/// CountTheRoll, EndRoll; Group.cpp:1057-1100, 1175-1500). Re-implemented from the behaviour, not copied.
/// <para>
/// When the first player opens a bag generated under <see cref="LootPermission.Roll"/> (<see cref="LootService.Show"/>), every
/// shared item whose quality reaches the group's loot threshold gets a roll among the group members who are recipients of that
/// loot and within reward distance of its source (need before greed: only those who can use the item). A roll with fewer than
/// two such members is not held: the item stays free to take. Rolled items are view-only until the roll resolves
/// (<see cref="LootItem.RollActive"/>). Each vote is announced to the participants; the roll resolves when all have voted or
/// <see cref="LootOptions.RollTimeoutMs"/> elapses, and whoever did not vote counts as passed. Need beats greed beats pass;
/// within a vote the highest 1..100 roll wins and a tie goes to the member earlier in the group's order. A winner with full
/// bags keeps the only claim to the item (<see cref="LootItem.Winner"/>); with everyone passed the item is free to take again.
/// </para>
/// <para>
/// State ownership: this manager owns the live rolls; the bag owns the items. Thread affinity: world thread only (map update,
/// opcode handlers). Per-tick cost: one pass over the live rolls, nothing allocated while none is running; rolls are rare
/// (one per item above the threshold per kill) and allocate once when started.
/// </para>
/// </summary>
public sealed class LootRollManager(LootService service, Random random) : IMapUpdater
{
    private readonly List<LootRoll> _rolls = [];

    /// <summary>One group member in a roll; <see cref="Vote"/> is null until the member voted.</summary>
    private sealed class Participant(ObjectGuid guid)
    {
        public ObjectGuid Guid { get; } = guid;

        public RollVote? Vote { get; set; }
    }

    private sealed class LootRoll(LootBag bag, LootItem item, WorldObject source, Participant[] participants, long remainingMs)
    {
        public LootBag Bag { get; } = bag;

        public LootItem Item { get; } = item;

        public WorldObject Source { get; } = source;

        public Participant[] Participants { get; } = participants;

        public long RemainingMs { get; set; } = remainingMs;
    }

    /// <summary>Rolls running now.</summary>
    public int ActiveRollCount => _rolls.Count;

    /// <summary>The map update: run the roll timers (world thread).</summary>
    public void Update(Map map, uint diffMs)
    {
        // Resolving removes the roll, so walk backwards; the common case (no roll) costs one comparison.
        for (int i = _rolls.Count - 1; i >= 0; i--)
        {
            if (i >= _rolls.Count)
            {
                continue; // a resolution removed more than this one (its bag closed other rolls)
            }

            LootRoll roll = _rolls[i];
            roll.RemainingMs -= diffMs;
            if (roll.RemainingMs <= 0)
            {
                Resolve(roll);
            }
        }
    }

    /// <summary>A member who leaves the map passes on every roll he has not voted on; the roll may complete by it.</summary>
    public void OnPlayerRemoved(Map map, Player player)
    {
        for (int i = _rolls.Count - 1; i >= 0; i--)
        {
            if (i >= _rolls.Count)
            {
                continue;
            }

            LootRoll roll = _rolls[i];
            Participant? who = FindParticipant(roll, player.Guid);
            if (who is { Vote: null })
            {
                who.Vote = RollVote.Pass;
                if (AllVoted(roll))
                {
                    Resolve(roll);
                }
            }
        }
    }

    /// <summary>
    /// Start the rolls of a freshly opened bag (vmangos Group::GroupLoot / NeedBeforeGreed): once per bag. Called by the loot
    /// service while the opener's window is being shown, before the window is sent.
    /// </summary>
    internal void Start(Player opener, LootBag bag)
    {
        bag.RollsStarted = true;
        if (service.SourceOf(bag) is not { } source || service.Groups?.GroupOf(opener) is not { } group
            || group.LootMethod is not (LootMethod.GroupLoot or LootMethod.NeedBeforeGreed) || source.Map is not { } map)
        {
            return;
        }

        bool needBeforeGreed = group.LootMethod == LootMethod.NeedBeforeGreed;
        foreach (LootItem item in bag.Items)
        {
            if (item.IsUnderThreshold || item.IsLooted || item.IsQuestItem || item.IsPerPlayer)
            {
                continue;
            }

            var participants = new List<Participant>();
            foreach (GroupMemberSlot member in group.Members)
            {
                if (bag.Recipients.Contains(member.Guid) && map.FindPlayer(member.Guid) is { } player
                    && GroupRewardRange.IsAtGroupRewardDistance(player, source, service.Options.RewardRange)
                    && (!needBeforeGreed || CanUse(player, item)))
                {
                    participants.Add(new Participant(member.Guid));
                }
            }

            if (participants.Count < 2)
            {
                continue; // vmangos: a single looter auto-needs, the item is not held and anyone may take it
            }

            var roll = new LootRoll(bag, item, source, [.. participants], service.Options.RollTimeoutMs);
            item.RollActive = true;
            _rolls.Add(roll);
            Broadcast(roll, WorldOpcode.SmsgLootStartRoll,
                GroupLootPackets.StartRoll(bag.Source, item.Slot, item.ItemId, service.Options.RollTimeoutMs));
        }
    }

    /// <summary>
    /// CMSG_LOOT_ROLL (vmangos HandleLootRollOpcode → Group::CountRollVote): <paramref name="player"/> votes on the roll for
    /// <paramref name="slot"/> of the loot of <paramref name="source"/>. A vote with no such roll, from a member who is not in
    /// it or who already voted, is ignored (vmangos would count a repeat again). Returns whether the vote counted.
    /// </summary>
    public bool Vote(Player player, ObjectGuid source, uint slot, RollVote vote)
    {
        ArgumentNullException.ThrowIfNull(player);
        LootRoll? roll = null;
        foreach (LootRoll candidate in _rolls)
        {
            if (candidate.Bag.Source == source && candidate.Item.Slot == slot)
            {
                roll = candidate;
                break;
            }
        }

        if (roll is null || FindParticipant(roll, player.Guid) is not { Vote: null } who)
        {
            return false;
        }

        if (service.SourceOf(roll.Bag) is null || roll.Item.IsLooted)
        {
            Cancel(roll);
            return false;
        }

        who.Vote = vote;
        Broadcast(roll, WorldOpcode.SmsgLootRoll, GroupLootPackets.VoteAnnouncement(source, roll.Item.Slot, player.Guid, roll.Item.ItemId, vote));
        if (AllVoted(roll))
        {
            Resolve(roll);
        }

        return true;
    }

    private static bool CanUse(Player player, LootItem item)
        => player.Inventory.Templates?.Find(item.ItemId) is { } template && player.Inventory.CanUseItem(template) == InventoryResult.Ok;

    private static Participant? FindParticipant(LootRoll roll, ObjectGuid guid)
    {
        foreach (Participant p in roll.Participants)
        {
            if (p.Guid == guid)
            {
                return p;
            }
        }

        return null;
    }

    private static bool AllVoted(LootRoll roll)
    {
        foreach (Participant p in roll.Participants)
        {
            if (p.Vote is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Drop a roll whose loot is gone (the corpse decayed, the chest despawned): nothing is announced (vmangos erases an invalid roll).</summary>
    private void Cancel(LootRoll roll)
    {
        roll.Item.RollActive = false;
        _rolls.Remove(roll);
    }

    /// <summary>Send one packet to every participant who is still in the source's map (vmangos sends to each voter with a session).</summary>
    private static void Broadcast(LootRoll roll, WorldOpcode opcode, byte[] payload)
    {
        if (roll.Source.Map is not { } map)
        {
            return;
        }

        foreach (Participant p in roll.Participants)
        {
            if (map.FindPlayer(p.Guid) is { } player)
            {
                player.Session.Send(opcode, payload);
            }
        }
    }

    /// <summary>
    /// vmangos Group::CountTheRoll: need beats greed beats pass. Each voter of the winning vote rolls 1..100 (announced to all);
    /// the highest wins, a tie goes to the earlier member. A member who left the map since voting does not take part. A winner
    /// who cannot store the item keeps the claim; with nobody left the item is announced as passed and is free to take again.
    /// </summary>
    private void Resolve(LootRoll roll)
    {
        _rolls.Remove(roll);
        LootBag bag = roll.Bag;
        LootItem item = roll.Item;
        item.RollActive = false;
        if (service.SourceOf(bag) is null || item.IsLooted || roll.Source.Map is not { } map)
        {
            return;
        }

        (Player Player, byte Number)? winner = null;
        RollVote winningVote = RollVote.Need;
        foreach (RollVote vote in (ReadOnlySpan<RollVote>)[RollVote.Need, RollVote.Greed])
        {
            winner = Draw(roll, map, vote);
            if (winner is not null)
            {
                winningVote = vote;
                break;
            }
        }

        if (winner is not { } won)
        {
            Broadcast(roll, WorldOpcode.SmsgLootAllPassed, GroupLootPackets.AllPassed(bag.Source, item.Slot, item.ItemId));
            return;
        }

        Broadcast(roll, WorldOpcode.SmsgLootRollWon,
            GroupLootPackets.RollWon(bag.Source, item.Slot, item.ItemId, won.Player.Guid, won.Number, winningVote));
        InventoryResult stored = service.AwardItem(won.Player, bag, item);
        if (stored == InventoryResult.Ok)
        {
            service.SettleUnviewed(bag);
            return;
        }

        // vmangos: the item is released again with the winner stamped on it; only he may take it, and he is told why not.
        item.Winner = won.Player.Guid;
        won.Player.Inventory.SendEquipError(stored, null, null, 0, item.ItemId);
        byte[] removed = LootPackets.Removed(item.Slot);
        foreach (Player viewer in bag.Viewers)
        {
            if (viewer.Guid != won.Player.Guid)
            {
                viewer.Session.Send(WorldOpcode.SmsgLootRemoved, removed);
            }
        }
    }

    /// <summary>Roll 1..100 for every present voter of <paramref name="vote"/>, announce each number and return the highest.</summary>
    private (Player Player, byte Number)? Draw(LootRoll roll, Map map, RollVote vote)
    {
        (Player Player, byte Number)? best = null;
        foreach (Participant p in roll.Participants)
        {
            if (p.Vote != vote || map.FindPlayer(p.Guid) is not { } player)
            {
                continue;
            }

            byte number = (byte)random.Next(1, 101);
            Broadcast(roll, WorldOpcode.SmsgLootRoll, GroupLootPackets.ResolvedRoll(roll.Bag.Source, roll.Item.Slot, p.Guid, roll.Item.ItemId, number, vote));
            if (best is not { } current || number > current.Number)
            {
                best = (player, number);
            }
        }

        return best;
    }
}
