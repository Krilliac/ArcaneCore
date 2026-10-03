using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Reputation;

public static partial class ReputationPackets
{
    /// <summary>
    /// SMSG_SET_FORCED_REACTIONS as vmangos writes it (Server/Packets/Misc.cpp:522-530): u32 count, then count × (u32 faction id,
    /// u32 rank). Open question: gtker/wow_messages types the faction as a u16 (smsg_set_forced_reactions.wowm, the same
    /// shortcut as the list-slot packets), so the wire width is unconfirmed by a real client; the server only sends it when
    /// <c>Reputation:SendForcedReactions</c> is on.
    /// </summary>
    public static byte[] SetForcedReactions(IReadOnlyDictionary<uint, ReputationRank> reactions)
    {
        ArgumentNullException.ThrowIfNull(reactions);
        var writer = new PacketWriter(4 + (reactions.Count * 8));
        writer.WriteUInt32((uint)reactions.Count);
        foreach ((uint faction, ReputationRank rank) in reactions.OrderBy(r => r.Key))
        {
            writer.WriteUInt32(faction);
            writer.WriteUInt32((uint)rank);
        }

        return writer.ToArray();
    }
}
