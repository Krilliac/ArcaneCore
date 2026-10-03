using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Guilds;

/// <summary>
/// Petition packets, layouts from vmangos Server/Packets/Petition.cpp:69-186 cross-checked with
/// gtker wow_messages (smsg_petition_showlist, smsg_petition_show_signatures,
/// smsg_petition_sign_results, smsg_petition_query_response, smsg_turn_in_petition_results,
/// msg_petition_rename, msg_petition_decline; client version 1.12).
/// </summary>
public static class PetitionPackets
{
    /// <summary>SMSG_PETITION_SHOWLIST: u64 npc, u8 count, then per entry u32 index, entry, display id, cost, flags (vmangos entryFlags = 1, "must be &amp;1 to show it in the UI").</summary>
    public static byte[] BuildShowList(ObjectGuid npc)
    {
        var writer = new PacketWriter(29);
        writer.WriteUInt64(npc.Value);
        writer.WriteByte(1);
        writer.WriteUInt32(1);
        writer.WriteUInt32(PetitionConstants.CharterEntry);
        writer.WriteUInt32(PetitionConstants.CharterDisplayId);
        writer.WriteUInt32(PetitionConstants.CharterCost);
        writer.WriteUInt32(1);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_PETITION_SHOW_SIGNATURES: u64 item, u64 owner (the viewing or offering player in vmangos),
    /// u32 petition id, u8 count, then per signature u64 signer and u32 0 (GuildMgr.cpp BuildSignatureData).
    /// </summary>
    public static byte[] BuildShowSignatures(ObjectGuid item, ObjectGuid owner, int petitionId, IReadOnlyList<PetitionSignature> signatures)
    {
        var writer = new PacketWriter(21 + (signatures.Count * 12));
        writer.WriteUInt64(item.Value);
        writer.WriteUInt64(owner.Value);
        writer.WriteUInt32((uint)petitionId);
        writer.WriteByte((byte)signatures.Count);
        foreach (PetitionSignature signature in signatures)
        {
            writer.WriteUInt64(signature.Guid.Value);
            writer.WriteUInt32(0);
        }

        return writer.ToArray();
    }

    /// <summary>SMSG_PETITION_SIGN_RESULTS: u64 item, u64 signer, u32 result.</summary>
    public static byte[] BuildSignResults(ObjectGuid item, ObjectGuid signer, PetitionResult result)
    {
        var writer = new PacketWriter(20);
        writer.WriteUInt64(item.Value);
        writer.WriteUInt64(signer.Value);
        writer.WriteUInt32((uint)result);
        return writer.ToArray();
    }

    /// <summary>SMSG_TURN_IN_PETITION_RESULTS: u32 result.</summary>
    public static byte[] BuildTurnInResult(PetitionResult result)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32((uint)result);
        return writer.ToArray();
    }

    /// <summary>
    /// SMSG_PETITION_QUERY_RESPONSE: u32 id, u64 owner, CString name, CString body (empty), u32 flags (1),
    /// u32 minimum (9), u32 maximum (9) — vmangos sends 9 whatever MinPetitionSigns is (PetitionsHandler.cpp:182-183) —
    /// u32 deadline, issue date, guild id, class mask, race mask (0), u16 genders (0), u32 min level, max level (0),
    /// u32 choice count (0), u32 default choice (0).
    /// </summary>
    public static byte[] BuildQueryResponse(Petition petition)
    {
        var writer = new PacketWriter(80 + petition.Name.Length);
        writer.WriteUInt32((uint)petition.Id);
        writer.WriteUInt64(petition.OwnerGuid.Value);
        writer.WriteCString(petition.Name);
        writer.WriteCString(string.Empty);
        writer.WriteUInt32(1);
        writer.WriteUInt32(PetitionConstants.ClientMaxSignatures);
        writer.WriteUInt32(PetitionConstants.ClientMaxSignatures);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt16(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        return writer.ToArray();
    }

    /// <summary>MSG_PETITION_RENAME (server to client): u64 item, CString new name.</summary>
    public static byte[] BuildRenameResult(ObjectGuid item, string name)
    {
        var writer = new PacketWriter(9 + name.Length);
        writer.WriteUInt64(item.Value);
        writer.WriteCString(name);
        return writer.ToArray();
    }

    /// <summary>MSG_PETITION_DECLINE (server to client): u64 decliner.</summary>
    public static byte[] BuildDeclineResult(ObjectGuid decliner)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(decliner.Value);
        return writer.ToArray();
    }
}
