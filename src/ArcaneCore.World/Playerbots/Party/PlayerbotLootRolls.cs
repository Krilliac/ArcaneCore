using ArcaneCore.Game;
using ArcaneCore.Game.Loot;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Party;

/// <summary>
/// A grouped bot's loot rolls (mangoszero LootRollAction.cpp): every SMSG_LOOT_START_ROLL is answered at once with the configured
/// vote (<see cref="PlayerbotPartyOptions.LootRoll"/>, pass by default), so a roll never waits out its timer for the bot.
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

    /// <summary>The vote for <paramref name="choice"/>: a bot never needs (the items are for the players).</summary>
    internal static RollVote VoteFor(PlayerbotLootRoll choice) => choice == PlayerbotLootRoll.Greed ? RollVote.Greed : RollVote.Pass;

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
