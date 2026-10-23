using ArcaneCore.Game;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// The client packets a bot sends for its guild life and the server packets it reads back. Every layout is the one the server's own
/// handlers read (GuildHandlers, PetitionHandlers, ChatHandlers), which cite vmangos <c>Server/Packets/Guild.cpp:3-41,91-95</c>,
/// <c>Server/Packets/Petition.cpp:13-67</c> and <c>Server/Packets/Misc.cpp:58-63</c>.
/// </summary>
internal static class PlayerbotSocialPackets
{
    /// <summary>TEXTEMOTE_WAVE (vmangos SharedDefines.h TEXTEMOTE_WAVE = 101).</summary>
    internal const uint TextEmoteWave = 101;

    /// <summary>The server packets the social feature takes from a bot's capture queue (never an unfiltered drain; the party AI's own list is separate).</summary>
    internal static readonly WorldOpcode[] IntakeOpcodes =
    [
        WorldOpcode.SmsgGuildInvite, WorldOpcode.SmsgPetitionShowSignatures, WorldOpcode.SmsgBattlefieldWin,
    ];

    /// <summary>CMSG_GUILD_CREATE: CString guild name.</summary>
    internal static byte[] GuildCreate(string name) => OneString(name);

    /// <summary>CMSG_GUILD_INVITE: CString character name.</summary>
    internal static byte[] GuildInvite(string name) => OneString(name);

    /// <summary>CMSG_GUILD_PROMOTE: CString character name.</summary>
    internal static byte[] GuildPromote(string name) => OneString(name);

    /// <summary>CMSG_GUILD_MOTD: CString message.</summary>
    internal static byte[] GuildMotd(string text) => OneString(text);

    /// <summary>CMSG_PETITION_BUY: u64 npc, u32, u64, CString name, ten u32, u16, u8, u32 index, u32 (all but the npc and the name unused, Petition.cpp:30-49).</summary>
    internal static byte[] PetitionBuy(ObjectGuid npc, string name)
    {
        var writer = new PacketWriter(96 + name.Length);
        writer.WriteUInt64(npc.Value);
        writer.WriteUInt32(0);
        writer.WriteUInt64(0);
        writer.WriteCString(name);
        for (int i = 0; i < 10; i++) writer.WriteUInt32(0);
        writer.WriteUInt16(0);
        writer.WriteByte(0);
        writer.WriteUInt32(0);
        writer.WriteUInt32(0);
        return writer.ToArray();
    }

    /// <summary>CMSG_OFFER_PETITION: u64 charter item guid, u64 target player guid (Petition.cpp:41-45).</summary>
    internal static byte[] PetitionOffer(ObjectGuid charter, ObjectGuid target)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt64(charter.Value);
        writer.WriteUInt64(target.Value);
        return writer.ToArray();
    }

    /// <summary>CMSG_PETITION_SIGN: u64 charter item guid, u8 (unused, Petition.cpp:35-39).</summary>
    internal static byte[] PetitionSign(ObjectGuid charter)
    {
        var writer = new PacketWriter(9);
        writer.WriteUInt64(charter.Value);
        writer.WriteByte(0);
        return writer.ToArray();
    }

    /// <summary>CMSG_TURN_IN_PETITION: u64 charter item guid.</summary>
    internal static byte[] TurnIn(ObjectGuid charter) => GuidOnly(charter);

    /// <summary>CMSG_TEXT_EMOTE: u32 text emote, u32 emote number (0), u64 target (Misc.cpp:58-63).</summary>
    internal static byte[] TextEmote(uint textEmote, ObjectGuid target)
    {
        var writer = new PacketWriter(16);
        writer.WriteUInt32(textEmote);
        writer.WriteUInt32(0);
        writer.WriteUInt64(target.Value);
        return writer.ToArray();
    }

    /// <summary>SMSG_GUILD_INVITE: CString inviter, CString guild (GuildPackets.BuildInvite). False for a body that does not hold both.</summary>
    internal static bool TryReadGuildInvite(byte[] payload, out string inviter, out string guild)
    {
        inviter = guild = string.Empty;
        if (payload.Length < 2) return false;
        try
        {
            var reader = new PacketReader(payload);
            inviter = reader.ReadCString();
            guild = reader.ReadCString();
            return inviter.Length > 0;
        }
        catch (ArgumentOutOfRangeException error)
        {
            FaultLedger.Shared.Record("playerbot.social", error);
            AnomalyLedger.Shared.Report("bot-social", "packet-decode-failed", "guild-invite", error.Message);
            return false; // no terminator (PacketReader's malformed-packet exception)
        }
    }

    /// <summary>SMSG_PETITION_SHOW_SIGNATURES: u64 item, u64 owner, u32 petition id, then the signatures (PetitionPackets.BuildShowSignatures).</summary>
    internal static bool TryReadShowSignatures(byte[] payload, out ObjectGuid item, out ObjectGuid owner, out uint petitionId)
    {
        item = owner = ObjectGuid.Empty;
        petitionId = 0;
        var reader = new PacketReader(payload);
        if (!reader.TryReadUInt64(out ulong itemValue) || !reader.TryReadUInt64(out ulong ownerValue) || !reader.TryReadUInt32(out petitionId)) return false;
        item = new ObjectGuid(itemValue);
        owner = new ObjectGuid(ownerValue);
        return true;
    }

    /// <summary>
    /// Run one client opcode through its world handler as the bot's own reply: the shared per-tick action budget does not apply (an
    /// answer to an invitation, a signature, a chat line is a client's own, as <see cref="Party.PlayerbotPartyAI"/> sends them). False when
    /// the session refused it (not in the world, a handler absent, a kick).
    /// </summary>
    internal static bool Send(WorldSession session, WorldOpcode opcode, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(session);
        ManagedActionBudget? budget = session.ManagedBudget;
        session.ManagedBudget = null;
        try
        {
            return session.TryManagedAction(opcode, payload);
        }
        finally
        {
            session.ManagedBudget = budget;
        }
    }

    /// <summary>A payload of one CString (a guild name, a MOTD, a channel to leave).</summary>
    internal static byte[] OneString(string text)
    {
        var writer = new PacketWriter(text.Length + 1);
        writer.WriteCString(text);
        return writer.ToArray();
    }

    internal static byte[] GuidOnly(ObjectGuid guid)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt64(guid.Value);
        return writer.ToArray();
    }
}
