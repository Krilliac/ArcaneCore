using ArcaneCore.Kernel.Reputation;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Reputation;

/// <summary>
/// Build-5875 reputation wire layouts. Verified against vmangos/core
/// 4b3d241cffe245a1f68da11380bce96c23db48c0 ReputationMgr.cpp (SendInitialReputations,
/// SendState, SendVisible) and Server/Packets/Misc.cpp (SetFactionAtWar u32 + u8,
/// SetFactionInactive u32 + u8, SetWatchedFaction i32), and gtker/wow_messages
/// wowm/world/faction/*.wowm (MIT) for the server messages. gtker types the client list index
/// as a u16 Faction; vmangos and TrinityCore read a u32 reputation-list index, which this
/// server follows (docs/integration/reputation.md).
/// </summary>
public static class ReputationPackets
{
    /// <summary>SMSG_INITIALIZE_FACTIONS: u32 64, then 64 × (u8 flags, u32 standing relative to the base) by list slot.</summary>
    public static byte[] InitializeFactions(PlayerReputation? reputation)
    {
        var writer = new PacketWriter(4 + (FactionCatalog.ReputationListSize * 5));
        writer.WriteUInt32(FactionCatalog.ReputationListSize);
        for (int slot = 0; slot < FactionCatalog.ReputationListSize; slot++)
        {
            FactionState? state = reputation?.StateByListId(slot);
            writer.WriteByte((byte)(state?.Flags ?? FactionStateFlags.None));
            writer.WriteInt32(state?.Standing ?? 0);
        }

        reputation?.MarkAllSent();
        return writer.ToArray();
    }

    /// <summary>SMSG_SET_FACTION_STANDING: u32 count, then count × (u32 list slot, u32 standing).</summary>
    public static byte[] SetFactionStanding(IReadOnlyList<(int ListId, int Standing)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var writer = new PacketWriter(4 + (entries.Count * 8));
        writer.WriteUInt32((uint)entries.Count);
        foreach ((int listId, int standing) in entries)
        {
            writer.WriteUInt32((uint)listId);
            writer.WriteInt32(standing);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_SET_FACTION_VISIBLE: u32 list slot.</summary>
    public static byte[] SetFactionVisible(int listId)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32((uint)listId);
        return writer.ToArray();
    }

    /// <summary>CMSG_SET_FACTION_ATWAR: exactly u32 list slot + u8 flag (nonzero declares war).</summary>
    public static bool TryReadSetAtWar(ReadOnlySpan<byte> payload, out int listId, out bool atWar)
        => TryReadSlotAndByte(payload, out listId, out atWar);

    /// <summary>CMSG_SET_FACTION_INACTIVE: exactly u32 list slot + u8 bool.</summary>
    public static bool TryReadSetInactive(ReadOnlySpan<byte> payload, out int listId, out bool inactive)
        => TryReadSlotAndByte(payload, out listId, out inactive);

    /// <summary>CMSG_SET_WATCHED_FACTION: exactly i32 list slot (-1 clears).</summary>
    public static bool TryReadSetWatched(ReadOnlySpan<byte> payload, out int listId)
    {
        listId = 0;
        if (payload.Length != 4)
        {
            return false;
        }

        listId = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload);
        return true;
    }

    private static bool TryReadSlotAndByte(ReadOnlySpan<byte> payload, out int listId, out bool flag)
    {
        listId = 0;
        flag = false;
        if (payload.Length != 5)
        {
            return false;
        }

        uint slot = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (slot >= FactionCatalog.ReputationListSize)
        {
            return false;
        }

        listId = (int)slot;
        flag = payload[4] != 0;
        return true;
    }
}
