using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Gm;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Gm.Audit;

/// <summary>
/// The player side of GM tickets: <c>CMSG_GMTICKET_GETTICKET</c>, <c>_CREATE</c>, <c>_UPDATETEXT</c> and <c>_DELETETICKET</c>
/// (vmangos Handlers/GMTicketHandler.cpp; the handler of the ticket status poll moved here from <see cref="PlayerHandlers"/>).
/// The ticket belongs to the character; the position stored is the one the SERVER knows for the player, the position
/// fields of the packet are skipped.
/// <para>
/// Layouts are the 1.12 ones of vmangos Server/Packets/GmTicket.cpp and wow_messages <c>gamemaster/*.wowm</c>, which agree:
/// create is <c>u8 type, u32 map, 3 x f32, cstring text, cstring reserved</c> (a harassment report may append chat data,
/// which is not read); update is <c>u8 type, cstring text</c>; the status of an open ticket is <c>u32 6, cstring text,
/// u8 type, f32 days since its last change, f32 days since the oldest open ticket's last change, f32 days since the queue
/// last changed, u8 escalation (0), u8 read by a GM (0)</c>; responses are one u32 (2 created, 3 create error, 4 updated,
/// 5 update error, 9 deleted). The rules are vmangos': an unknown type (11 or more) makes create silent, a second ticket is
/// a create error, withdrawing without a ticket is answered with nothing. Not yet confirmed against a retail 1.12.1 client.
/// </para>
/// <para>
/// The three mutations are rate limited per account (<see cref="GmOptions.TicketMutationsPerMinute"/>, ArcaneCore's own,
/// fail-closed): a packet over the limit is refused before it is read and the player is told. Staff are told of a create
/// and of a text that actually changed, so the limit also bounds those notices.
/// </para>
/// </summary>
public sealed class GmTicketHandlers : IOpcodeHandlerGroup
{
    /// <summary>SMSG_GMTICKET_GETTICKET status: the player has an open ticket (vmangos GMTICKET_STATUS_HASTEXT, 0x06).</summary>
    public const uint StatusHasTicket = 0x06;

    /// <summary>wow_messages GmTicketResponse ALREADY_EXIST; vmangos' 1.12 handler never sends it (a second ticket is a create error).</summary>
    public const uint ResponseAlreadyExists = 1;
    public const uint ResponseCreated = 2;
    public const uint ResponseCreateError = 3;
    public const uint ResponseUpdated = 4;
    public const uint ResponseUpdateError = 5;
    public const uint ResponseDeleted = 9;

    /// <summary>vmangos SharedDefines.h TicketType GMTICKET_MAX: types run 1 (stuck) to 10 (character).</summary>
    public const byte TicketTypeLimit = 11;

    /// <summary>Bytes before the ticket text in CMSG_GMTICKET_CREATE: u8 category, u32 map, 3 x f32.</summary>
    private const int CreateFixedBytes = 1 + 4 + 12;

    private const float SecondsPerDay = 86400f;

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGmticketGetticket, HandleGetTicket);
        table.OnWorld(WorldOpcode.CmsgGmticketCreate, HandleCreate);
        table.OnWorld(WorldOpcode.CmsgGmticketUpdatetext, HandleUpdateText);
        table.OnWorld(WorldOpcode.CmsgGmticketDeleteticket, HandleDeleteTicket);
    }

    /// <summary>
    /// SMSG_GMTICKET_GETTICKET for an open ticket (vmangos GmTicket::FillPacket and GmTicketGetTicket::AppendBodyTo). The ages
    /// are days, vmangos GetAge: <c>float(time(nullptr) - t) / float(DAY)</c>.
    /// </summary>
    public static byte[] BuildTicketStatus(GmTicketRecord ticket, long now, long? oldestOpenUpdatedAt, long lastQueueChange)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var writer = new PacketWriter(32 + ticket.Text.Length);
        writer.WriteUInt32(StatusHasTicket);
        writer.WriteCString(ticket.Text);
        writer.WriteByte(ticket.Category);
        writer.WriteSingle(Age(now, ticket.UpdatedAt));
        writer.WriteSingle(oldestOpenUpdatedAt is { } oldest ? Age(now, oldest) : 0f);
        writer.WriteSingle(Age(now, lastQueueChange));
        writer.WriteByte(0); // escalation: GMTICKET_ASSIGNEDTOGM_STATUS_NOT_ASSIGNED (no escalation queue here)
        writer.WriteByte(0); // read by a GM: GMTICKET_OPENEDBYGM_STATUS_NOT_OPENED (viewing is not tracked)
        return writer.ToArray();
    }

    private static float Age(long now, long then) => Math.Max(0, now - then) / SecondsPerDay;

    private static byte[] BuildResponse(uint code)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(code);
        return writer.ToArray();
    }

    private static GmAuditFeature Audit(WorldSession session) => session.Services.GetRequiredService<GmAuditFeature>();

    private static byte[] StatusOf(GmAuditFeature audit, int characterId)
        => audit.OpenTicketOf(characterId) is { } ticket
            ? BuildTicketStatus(ticket, audit.NowUnixSeconds, audit.OldestOpenTicketUpdatedAt(), audit.LastTicketChange)
            : MiscPackets.BuildNoGmTicket();

    /// <summary>vmangos HandleGMTicketGetTicketOpcode: the time response, then the ticket status.</summary>
    private static void HandleGetTicket(WorldSession session, Player player, byte[] payload)
    {
        session.Send(WorldOpcode.SmsgQueryTimeResponse, QueryPackets.BuildQueryTimeResponse(DateTimeOffset.UtcNow));
        session.Send(WorldOpcode.SmsgGmticketGetticket, StatusOf(Audit(session), (int)player.Guid.Low));
    }

    /// <summary>
    /// vmangos HandleGMTicketCreateOpcode: an unknown type is ignored without an answer; a character that already has an open
    /// ticket gets CREATE_ERROR (the response it starts with); a new ticket is CREATE_SUCCESS and staff are told.
    /// </summary>
    private static void HandleCreate(WorldSession session, Player player, byte[] payload)
    {
        GmAuditFeature audit = Audit(session);
        if (!audit.TryAdmitTicketMutation(player.AccountId))
        {
            session.Send(WorldOpcode.SmsgGmticketCreate, BuildResponse(ResponseCreateError));
            player.SendSystemMessage(GmAuditStrings.TicketTooFast);
            return;
        }

        if (payload.Length < CreateFixedBytes)
        {
            session.Send(WorldOpcode.SmsgGmticketCreate, BuildResponse(ResponseCreateError));
            return;
        }

        var reader = new PacketReader(payload);
        byte category = reader.ReadByte();
        if (category >= TicketTypeLimit)
        {
            return; // vmangos: "if (packet.ticketType >= GMTICKET_MAX) return;"
        }

        reader.Skip(4 + 12); // map and position: the server's own are stored
        string text = CleanText(reader.ReadCString());
        if (text.Length == 0)
        {
            session.Send(WorldOpcode.SmsgGmticketCreate, BuildResponse(ResponseCreateError));
            return;
        }

        GmTicketRecord? ticket = audit.CreateTicket((int)player.Guid.Low, text, category, player.MapId, player.X, player.Y, player.Z);
        if (ticket is null)
        {
            session.Send(WorldOpcode.SmsgGmticketCreate, BuildResponse(ResponseCreateError));
            return;
        }

        session.Send(WorldOpcode.SmsgGmticketCreate, BuildResponse(ResponseCreated));
        NotifyStaff(session, GmAuditStrings.TicketNew(GmStrings.PlayerLink(player.Name), ticket.Id));
    }

    /// <summary>vmangos HandleGMTicketUpdateTextOpcode: <c>u8 type, cstring text</c>; both replace the ticket's (SetMessage, SetTicketType).</summary>
    private static void HandleUpdateText(WorldSession session, Player player, byte[] payload)
    {
        GmAuditFeature audit = Audit(session);
        if (!audit.TryAdmitTicketMutation(player.AccountId))
        {
            session.Send(WorldOpcode.SmsgGmticketUpdatetext, BuildResponse(ResponseUpdateError));
            player.SendSystemMessage(GmAuditStrings.TicketTooFast);
            return;
        }

        if (payload.Length < 1)
        {
            session.Send(WorldOpcode.SmsgGmticketUpdatetext, BuildResponse(ResponseUpdateError));
            return;
        }

        var reader = new PacketReader(payload);
        byte type = reader.ReadByte();
        string text = CleanText(reader.ReadCString());
        bool changed = false;
        // vmangos stores the type as sent; a value outside 1-10 is kept out here, so the stored type stays one the client knows.
        byte? category = type is >= 1 and < TicketTypeLimit ? type : null;
        GmTicketRecord? ticket = text.Length == 0 ? null : audit.UpdateTicketText((int)player.Guid.Low, text, category, out changed);
        session.Send(WorldOpcode.SmsgGmticketUpdatetext, BuildResponse(ticket is null ? ResponseUpdateError : ResponseUpdated));
        if (ticket is not null && changed)
        {
            // Staff hear of a change, not of the same text sent again.
            NotifyStaff(session, GmAuditStrings.TicketUpdated(GmStrings.PlayerLink(player.Name), ticket.Id));
        }
    }

    /// <summary>vmangos HandleGMTicketDeleteTicketOpcode: with a ticket, TICKET_DELETED then the no-ticket status; without one, nothing.</summary>
    private static void HandleDeleteTicket(WorldSession session, Player player, byte[] payload)
    {
        GmAuditFeature audit = Audit(session);
        if (!audit.TryAdmitTicketMutation(player.AccountId))
        {
            // Nothing was deleted, so the deleted code is not sent; the client is given the ticket's real state instead
            // (the same status answer that follows a delete).
            session.Send(WorldOpcode.SmsgGmticketGetticket, StatusOf(audit, (int)player.Guid.Low));
            player.SendSystemMessage(GmAuditStrings.TicketTooFast);
            return;
        }

        if (!audit.DeleteTicketOf((int)player.Guid.Low))
        {
            return;
        }

        session.Send(WorldOpcode.SmsgGmticketDeleteticket, BuildResponse(ResponseDeleted));
        session.Send(WorldOpcode.SmsgGmticketGetticket, MiscPackets.BuildNoGmTicket());
    }

    /// <summary>
    /// The ticket text as stored: control characters out (the client puts a '\a' in front of an updated text,
    /// mangos-zero GMTicketHandler.cpp stripLineInvisibleChars), surrounding spaces trimmed, at most
    /// <see cref="GmAuditLimits.MaxTextLength"/> characters.
    /// </summary>
    public static string CleanText(string text)
    {
        string clean = new([.. text.Where(c => !char.IsControl(c))]);
        clean = clean.Trim();
        return clean.Length > GmAuditLimits.MaxTextLength ? clean[..GmAuditLimits.MaxTextLength] : clean;
    }

    private static void NotifyStaff(WorldSession session, string text)
    {
        foreach (Player staff in session.World.OnlinePlayers.Where(p => p.Security >= AccountSecurity.GameMaster))
        {
            staff.SendSystemMessage(text);
        }
    }
}
