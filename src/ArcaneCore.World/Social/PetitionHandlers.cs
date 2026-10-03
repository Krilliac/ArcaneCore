using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Social;

/// <summary>
/// Guild charter requests (vmangos PetitionsHandler.cpp; payload layouts from vmangos
/// Server/Packets/Petition.cpp:3-67 and gtker wow_messages). A payload shorter than its layout
/// disconnects the client like every other malformed request; trailing bytes are ignored, as
/// vmangos' ByteBuffer ignores them.
/// </summary>
public sealed class PetitionHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgPetitionShowlist, (s, p, d) => Petitions(s).ShowList(p, ReadGuid(d)));
        table.OnWorld(WorldOpcode.CmsgPetitionBuy, HandleBuy);
        table.OnWorld(WorldOpcode.CmsgPetitionShowSignatures, (s, p, d) => Petitions(s).ShowSignatures(p, ReadGuid(d)));
        table.OnWorld(WorldOpcode.CmsgPetitionQuery, HandleQuery);
        table.OnWorld(WorldOpcode.MsgPetitionRename, HandleRename);
        table.OnWorld(WorldOpcode.CmsgPetitionSign, HandleSign);
        table.OnWorld(WorldOpcode.MsgPetitionDecline, (s, p, d) => Petitions(s).Decline(p, ReadGuid(d)));
        table.OnWorld(WorldOpcode.CmsgOfferPetition, HandleOffer);
        table.OnWorld(WorldOpcode.CmsgTurnInPetition, (s, p, d) => Petitions(s).TurnIn(p, ReadGuid(d)));
    }

    private static PetitionManager Petitions(WorldSession session) => SocialHandlers.Social(session).Petitions;

    private static ObjectGuid ReadGuid(byte[] payload) => new(new PacketReader(payload).ReadUInt64());

    /// <summary>CMSG_PETITION_BUY: u64 npc, u32, u64, CString name, then ten u32, u16, u8, u32 index, u32 (all unused, Petition.cpp:30-49).</summary>
    private static void HandleBuy(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var npc = new ObjectGuid(reader.ReadUInt64());
        reader.ReadUInt32();
        reader.ReadUInt64();
        string name = reader.ReadCString();
        for (int i = 0; i < 10; i++)
        {
            reader.ReadUInt32();
        }

        reader.ReadUInt16();
        reader.ReadByte();
        reader.ReadUInt32();
        reader.ReadUInt32();
        Petitions(session).Buy(player, npc, name);
    }

    /// <summary>CMSG_PETITION_QUERY: u32 petition id, u64 charter item guid (Petition.cpp:13-17).</summary>
    private static void HandleQuery(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint petitionId = reader.ReadUInt32();
        reader.ReadUInt64();
        Petitions(session).Query(player, petitionId);
    }

    /// <summary>MSG_PETITION_RENAME (client to server): u64 charter item guid, CString new name (Petition.cpp:29-33).</summary>
    private static void HandleRename(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var item = new ObjectGuid(reader.ReadUInt64());
        Petitions(session).Rename(player, item, reader.ReadCString());
    }

    /// <summary>CMSG_PETITION_SIGN: u64 charter item guid, u8 (unused, Petition.cpp:35-39).</summary>
    private static void HandleSign(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var item = new ObjectGuid(reader.ReadUInt64());
        reader.ReadByte();
        Petitions(session).Sign(player, item);
    }

    /// <summary>CMSG_OFFER_PETITION: u64 charter item guid, u64 target player guid (Petition.cpp:41-45).</summary>
    private static void HandleOffer(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var item = new ObjectGuid(reader.ReadUInt64());
        Petitions(session).Offer(player, item, new ObjectGuid(reader.ReadUInt64()));
    }
}
