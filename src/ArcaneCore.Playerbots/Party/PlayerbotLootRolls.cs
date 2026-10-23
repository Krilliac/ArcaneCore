using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Playerbots.Progression;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Bots.Progression;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// A grouped bot's loot rolls (mangoszero LootRollAction.cpp): every SMSG_LOOT_START_ROLL is answered at once with the configured
/// vote (<see cref="PlayerbotPartyOptions.LootRoll"/>, <see cref="PlayerbotLootRoll.Need"/> by default: Need on usable upgrades,
/// bags, recipes, spells and mounts, Greed on everything else), so a roll never waits out its timer for the bot.
/// </summary>
internal static class PlayerbotLootRolls
{
    /// <summary>One roll the server opened for the bot.</summary>
    internal readonly record struct StartRoll(ObjectGuid Source, uint Slot, uint ItemId);

    /// <summary>SMSG_LOOT_START_ROLL: u64 loot source, u32 slot, u32 item, u32 random suffix, u32 random property, u32 countdown (GroupLootPackets.StartRoll).</summary>
    internal static bool TryRead(byte[] payload, out StartRoll roll)
    {
        roll = default;
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt64(out ulong source) || !reader.TryReadUInt32(out uint slot) || !reader.TryReadUInt32(out uint item)) return false;
        roll = new StartRoll(new ObjectGuid(source), slot, item);
        return true;
    }

    /// <summary>A roll the server settled: who won it and with which vote.</summary>
    internal readonly record struct RollResult(ObjectGuid Source, uint Slot, uint ItemId, ObjectGuid Winner, RollVote Vote);

    /// <summary>SMSG_LOOT_ROLL_WON: u64 source, u32 slot, u32 item, u32 0, u32 0, u64 winner, u8 roll, u8 vote (GroupLootPackets.RollWon).</summary>
    internal static bool TryReadWon(byte[] payload, out RollResult result)
    {
        result = default;
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt64(out ulong source) || !reader.TryReadUInt32(out uint slot) || !reader.TryReadUInt32(out uint item)
            || !reader.TryReadUInt32(out _) || !reader.TryReadUInt32(out _) || !reader.TryReadUInt64(out ulong winner)
            || !reader.TryReadByte(out _) || !reader.TryReadByte(out byte vote) || vote > (byte)RollVote.Greed) return false;
        result = new RollResult(new ObjectGuid(source), slot, item, new ObjectGuid(winner), (RollVote)vote);
        return true;
    }

    /// <summary>
    /// Whether the winner of a roll took something it had no use for: it won on a Need vote, the bot had voted Need too (so the item was an upgrade or a
    /// use for the bot), and the winner could not use the item (<see cref="WantsItem"/> false).
    /// </summary>
    internal static bool IsNinja(RollVote botVote, RollVote winnerVote, bool winnerCouldUse)
        => botVote == RollVote.Need && winnerVote == RollVote.Need && !winnerCouldUse;

    /// <summary>Whether <paramref name="player"/> could use <paramref name="itemId"/>: a wearable upgrade over its gear, a bag upgrade, or a learnable spell, recipe or mount item.</summary>
    internal static bool WantsItem(Player player, uint itemId, SpellStore? store = null, SpellbookCache? spellbook = null)
    {
        if (player.Inventory.Templates.Find(itemId) is not { } template) return false;
        PlayerbotStatWeights weights = PlayerbotTalentBuilds.Choose(player.Class, player.Guid.Low).Weights;
        return PlayerbotItemScore.UpgradeGain(player, template, weights) is > PlayerbotItemScore.MinimumGain
            || PlayerbotEquipment.IsBagUpgrade(player, template)
            || PlayerbotEquipment.CanLearnFromItem(player, template, store, spellbook);
    }

    /// <summary>The direct vote for <paramref name="choice"/> when no item inspection is available.</summary>
    internal static RollVote VoteFor(PlayerbotLootRoll choice) => choice switch
    {
        PlayerbotLootRoll.Need => RollVote.Need,
        PlayerbotLootRoll.Greed => RollVote.Greed,
        _ => RollVote.Pass,
    };

    /// <summary>
    /// The vote for <paramref name="itemId"/> under <paramref name="choice"/>: when <see cref="PlayerbotLootRoll.Need"/> is active,
    /// the bot rolls <see cref="RollVote.Need"/> if the item is a wearable upgrade over its current gear, a bag upgrade, or a
    /// learnable spell/recipe/mount item, and <see cref="RollVote.Greed"/> otherwise.
    /// </summary>
    internal static RollVote VoteFor(Player player, uint itemId, PlayerbotLootRoll choice, SpellStore? store = null, SpellbookCache? spellbook = null)
    {
        if (choice == PlayerbotLootRoll.Pass)
            return RollVote.Pass;
        if (choice == PlayerbotLootRoll.Greed)
            return RollVote.Greed;

        return WantsItem(player, itemId, store, spellbook) ? RollVote.Need : RollVote.Greed;
    }

    /// <summary>CMSG_LOOT_ROLL: u64 loot source, u32 slot, u8 vote (GroupLootPackets.TryParseLootRoll).</summary>
    internal static byte[] Vote(StartRoll roll, RollVote vote)
    {
        var writer = new PacketWriter(13);
        writer.WriteUInt64(roll.Source.Value);
        writer.WriteUInt32(roll.Slot);
        writer.WriteByte((byte)vote);
        return writer.ToArray();
    }
}
