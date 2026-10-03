using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Social;

/// <summary>Guild requests (vmangos GuildHandler.cpp; payloads per gtker cmsg_guild_*).</summary>
public sealed class GuildHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgGuildQuery, (s, p, d) => Guilds(s).Query(p, new PacketReader(d).ReadUInt32()));
        table.OnWorld(WorldOpcode.CmsgGuildCreate, HandleCreate);
        table.OnWorld(WorldOpcode.CmsgGuildInvite, (s, p, d) => Guilds(s).Invite(p, Name(d)));
        table.OnWorld(WorldOpcode.CmsgGuildAccept, (s, p, _) => Guilds(s).Accept(p));
        table.OnWorld(WorldOpcode.CmsgGuildDecline, (s, p, _) => Guilds(s).Decline(p));
        table.OnWorld(WorldOpcode.CmsgGuildInfo, (s, p, _) => Guilds(s).Info(p));
        table.OnWorld(WorldOpcode.CmsgGuildRoster, (s, p, _) => Guilds(s).Roster(p));
        table.OnWorld(WorldOpcode.CmsgGuildPromote, (s, p, d) => Guilds(s).Promote(p, Name(d)));
        table.OnWorld(WorldOpcode.CmsgGuildDemote, (s, p, d) => Guilds(s).Demote(p, Name(d)));
        table.OnWorld(WorldOpcode.CmsgGuildLeave, (s, p, _) => Guilds(s).Leave(p));
        table.OnWorld(WorldOpcode.CmsgGuildRemove, (s, p, d) => Guilds(s).Remove(p, Name(d)));
        table.OnWorld(WorldOpcode.CmsgGuildDisband, (s, p, _) => Guilds(s).Disband(p));
        table.OnWorld(WorldOpcode.CmsgGuildLeader, (s, p, d) => Guilds(s).SetLeader(p, Name(d)));
        table.OnWorld(WorldOpcode.CmsgGuildMotd, (s, p, d) => Guilds(s).SetMotd(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgGuildInfoText, (s, p, d) => Guilds(s).SetInfo(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgGuildSetPublicNote, (s, p, d) => SetNote(s, p, d, officer: false));
        table.OnWorld(WorldOpcode.CmsgGuildSetOfficerNote, (s, p, d) => SetNote(s, p, d, officer: true));
        table.OnWorld(WorldOpcode.CmsgGuildRank, HandleRank);
        table.OnWorld(WorldOpcode.CmsgGuildAddRank, (s, p, d) => Guilds(s).AddRank(p, Text(d)));
        table.OnWorld(WorldOpcode.CmsgGuildDelRank, (s, p, _) => Guilds(s).DeleteRank(p));
    }

    private static GuildManager Guilds(WorldSession session) => SocialHandlers.Social(session).Guilds;

    private static string Name(byte[] payload) => CharacterNames.Normalize(new PacketReader(payload).ReadCString());

    private static string Text(byte[] payload) => new PacketReader(payload).ReadCString();

    /// <summary>
    /// CMSG_GUILD_CREATE: CString name (vmangos HandleGuildCreateOpcode: ignored when already
    /// guilded; a failed Guild::Create sends nothing). Charters are the normal way in.
    /// </summary>
    private static void HandleCreate(WorldSession session, Player player, byte[] payload)
    {
        string name = Text(payload);
        GuildManager guilds = Guilds(session);
        if (guilds.GetGuildOf(player) is null)
        {
            guilds.Create(player.Guid.Low, name, out _);
        }
    }

    /// <summary>CMSG_GUILD_SET_PUBLIC_NOTE / CMSG_GUILD_SET_OFFICER_NOTE: CString name, CString note.</summary>
    private static void SetNote(WorldSession session, Player player, byte[] payload, bool officer)
    {
        var reader = new PacketReader(payload);
        string name = CharacterNames.Normalize(reader.ReadCString());
        string note = reader.ReadCString();
        if (officer)
        {
            Guilds(session).SetOfficerNote(player, name, note);
        }
        else
        {
            Guilds(session).SetPublicNote(player, name, note);
        }
    }

    /// <summary>CMSG_GUILD_RANK: u32 rank id, u32 rights, CString rank name.</summary>
    private static void HandleRank(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint rankId = reader.ReadUInt32();
        uint rights = reader.ReadUInt32();
        string name = reader.ReadCString();
        Guilds(session).SetRank(player, rankId, rights, name);
    }
}
