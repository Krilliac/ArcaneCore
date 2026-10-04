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
/// (mangos-zero GMTicketHandler.cpp:77-300; the handler of the ticket status poll moved here from
/// <see cref="PlayerHandlers"/>, with the same "no ticket" answer when there is none). The ticket belongs to the
/// character; the position stored is the one the SERVER knows for the player, the position fields of the packet are
/// skipped.
/// <para>
/// UNVERIFIED against a retail 1.12.1 client: the layouts below are mangos-zero's (<c>u8 category, u32 map, 3 x f32,
/// cstring text, cstring reserved</c> for create, and the status-6 ticket answer <c>u32 6, cstring text, u8 7, 3 x f32 0,
/// 2 x u8 0</c>), a core that targets 1.12 but whose source is the only evidence here. A create packet shorter than
/// the fixed part is answered with the create-error code and stores nothing. The response codes (1 exists, 2 created, 3
/// error, 4 updated, 5 update error, 9 deleted) are mangos-zero's GMTicketMgr.h:39-46 and likewise unverified on the wire.
/// </para>
/// <para>
/// The three mutations are rate limited per account (<see cref="GmOptions.TicketMutationsPerMinute"/>, ArcaneCore's own,
/// fail-closed): a packet over the limit is refused before it is read and the player is told. Staff are told of a create
/// and of a text that actually changed, so the limit also bounds those notices.
/// </para>
/// </summary>
public sealed class GmTicketHandlers : IOpcodeHandlerGroup
{
    /// <summary>SMSG_GMTICKET_GETTICKET status: the player has an open ticket (mangos-zero SendGMTicketGetTicket, status 0x06).</summary>
    public const uint StatusHasTicket = 0x06;

    public const uint ResponseAlreadyExists = 1;
    public const uint ResponseCreated = 2;
    public const uint ResponseCreateError = 3;
    public const uint ResponseUpdated = 4;
    public const uint ResponseUpdateError = 5;
    public const uint ResponseDeleted = 9;

    /// <summary>Bytes before the ticket text in CMSG_GMTICKET_CREATE: u8 category, u32 map, 3 x f32.</summary>
    private const int CreateFixedBytes = 1 + 4 + 12;

    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGmticketGetticket, HandleGetTicket);
        table.OnWorld(WorldOpcode.CmsgGmticketCreate, HandleCreate);
        table.OnWorld(WorldOpcode.CmsgGmticketUpdatetext, HandleUpdateText);
        table.OnWorld(WorldOpcode.CmsgGmticketDeleteticket, HandleDeleteTicket);
    }

    /// <summary>SMSG_GMTICKET_GETTICKET for an open ticket (mangos-zero SendGMTicketGetTicket, status 6).</summary>
    public static byte[] BuildTicketStatus(string text)
    {
        var writer = new PacketWriter(32 + text.Length);
        writer.WriteUInt32(StatusHasTicket);
        writer.WriteCString(text);
        writer.WriteByte(0x7);       // ticket category as mangos-zero sends it
        writer.WriteSingle(0);       // tickets in queue
        writer.WriteSingle(0);
        writer.WriteSingle(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        return writer.ToArray();
    }

    private static byte[] BuildResponse(uint code)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32(code);
        return writer.ToArray();
    }

    private static GmAuditFeature Audit(WorldSession session) => session.Services.GetRequiredService<GmAuditFeature>();

    private static void HandleGetTicket(WorldSession session, Player player, byte[] payload)
    {
        session.Send(WorldOpcode.SmsgQueryTimeResponse, QueryPackets.BuildQueryTimeResponse(DateTimeOffset.UtcNow));
        session.Send(WorldOpcode.SmsgGmticketGetticket, Audit(session).OpenTicketOf((int)player.Guid.Low) is { } ticket
            ? BuildTicketStatus(ticket.Text)
            : MiscPackets.BuildNoGmTicket());
    }

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
            session.Send(WorldOpcode.SmsgGmticketCreate, BuildResponse(ResponseAlreadyExists));
            return;
        }

        session.Send(WorldOpcode.SmsgQueryTimeResponse, QueryPackets.BuildQueryTimeResponse(DateTimeOffset.UtcNow));
        session.Send(WorldOpcode.SmsgGmticketCreate, BuildResponse(ResponseCreated));
        NotifyStaff(session, GmAuditStrings.TicketNew(GmStrings.PlayerLink(player.Name), ticket.Id));
    }

    private static void HandleUpdateText(WorldSession session, Player player, byte[] payload)
    {
        GmAuditFeature audit = Audit(session);
        if (!audit.TryAdmitTicketMutation(player.AccountId))
        {
            session.Send(WorldOpcode.SmsgGmticketUpdatetext, BuildResponse(ResponseUpdateError));
            player.SendSystemMessage(GmAuditStrings.TicketTooFast);
            return;
        }

        var reader = new PacketReader(payload);
        string text = CleanText(reader.ReadCString());
        bool changed = false;
        GmTicketRecord? ticket = text.Length == 0 ? null : audit.UpdateTicketText((int)player.Guid.Low, text, out changed);
        session.Send(WorldOpcode.SmsgGmticketUpdatetext, BuildResponse(ticket is null ? ResponseUpdateError : ResponseUpdated));
        if (ticket is not null && changed)
        {
            // Staff hear of a change, not of the same text sent again.
            NotifyStaff(session, GmAuditStrings.TicketUpdated(GmStrings.PlayerLink(player.Name), ticket.Id));
        }
    }

    private static void HandleDeleteTicket(WorldSession session, Player player, byte[] payload)
    {
        GmAuditFeature audit = Audit(session);
        if (!audit.TryAdmitTicketMutation(player.AccountId))
        {
            // Nothing was deleted, so the deleted code is not sent; the client is given the ticket's real state instead
            // (the same status answer that follows a delete). UNVERIFIED on a retail client, as the layouts above.
            session.Send(WorldOpcode.SmsgGmticketGetticket, audit.OpenTicketOf((int)player.Guid.Low) is { } open ? BuildTicketStatus(open.Text) : MiscPackets.BuildNoGmTicket());
            player.SendSystemMessage(GmAuditStrings.TicketTooFast);
            return;
        }

        audit.DeleteTicketOf((int)player.Guid.Low);
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
