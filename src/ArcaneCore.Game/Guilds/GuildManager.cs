using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Guilds;

/// <summary>Outcome of a GM guild command.</summary>
public enum GuildAdminResult
{
    Ok,
    NameInvalid,
    NameExists,
    AlreadyInGuild,
    GuildNotFound,
    NotInGuild,
    RankInvalid,
    CharacterNotFound,

    /// <summary>The stored guilds have not been loaded yet, so a new id could collide.</summary>
    NotLoaded,
}

/// <summary>
/// Guilds: membership, ranks and rights, MOTD, info text, notes, roster, queries, guild and
/// officer chat, and the GM create/delete/invite/uninvite/rank commands. The rules follow
/// vmangos GuildHandler.cpp and Guild.cpp (citations per method). World thread.
/// </summary>
public sealed partial class GuildManager(SocialContext context)
{
    /// <summary>
    /// Raised after every membership addition (accept, GM invite, founding, petition turn-in):
    /// guild, character and the petition id that is being completed (0 for none). vmangos
    /// Guild::AddMember strips the joiner's other petitions and signatures here (Guild.cpp:213,
    /// Player::RemovePetitionsAndSigns, Player.cpp:17796-17806).
    /// </summary>
    public event Action<Guild, uint, int>? MemberJoined;

    /// <summary>Raised after a member leaves a guild by any path (leave, kick, GM uninvite, disband): guild and character.</summary>
    public event Action<Guild, uint>? MemberLeft;

    /// <summary>Guild, charter and petition rules (World:Guild); defaults are the retail values.</summary>
    public GuildOptions Options { get; set; } = new();

    private readonly Dictionary<int, Guild> _guilds = [];
    private readonly Dictionary<uint, Guild> _memberOf = [];
    private readonly Dictionary<ObjectGuid, (int GuildId, ObjectGuid Inviter)> _invites = [];
    private int _nextId = 1;

    public IReadOnlyCollection<Guild> All => _guilds.Values;

    /// <summary>Whether <see cref="Load"/> has run (guild creation waits for it so ids never collide with stored guilds).</summary>
    public bool IsLoaded { get; private set; }

    public Guild? Get(int id) => _guilds.GetValueOrDefault(id);

    /// <summary>vmangos GuildMgr::GetGuildByName (case-insensitive).</summary>
    public Guild? GetByName(string name) => _guilds.Values.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public Guild? GetGuildOf(uint characterId) => _memberOf.GetValueOrDefault(characterId);

    public Guild? GetGuildOf(Player player) => GetGuildOf(player.Guid.Low);

    /// <summary>The guild <paramref name="player"/> has been invited to, if any (vmangos Player::GetGuildIdInvited).</summary>
    public int InvitedTo(Player player) => _invites.TryGetValue(player.Guid, out var invite) ? invite.GuildId : 0;

    /// <summary>Install the guilds read from storage (vmangos GuildMgr::LoadGuilds). Members whose character is gone are dropped.</summary>
    public void Load(IEnumerable<GuildData> guilds)
    {
        foreach (GuildData data in guilds)
        {
            Guild guild = Guild.FromData(data);
            if (guild.Ranks.Count < Guild.MinRanks)
            {
                // vmangos Guild::LoadRanksFromDB: too few ranks → recreate the defaults.
                guild.CreateDefaultRanks();
            }

            foreach (GuildMember member in guild.Members.ToList())
            {
                if (context.Characters.Find(member.CharacterId) is null)
                {
                    guild.RemoveMember(member.CharacterId);
                }
                else
                {
                    member.Rank = Math.Min(member.Rank, guild.LowestRank);
                }
            }

            if (guild.MemberCount == 0)
            {
                context.Persistence.DeleteGuild(guild.Id);
                continue;
            }

            if (guild.Find(guild.LeaderId) is null)
            {
                // vmangos Guild::LoadMembersFromDB: a missing leader is replaced by the best ranked member.
                GuildMember best = guild.Members.OrderBy(m => m.Rank).First();
                guild.LeaderId = best.CharacterId;
                best.Rank = Guild.GuildMasterRank;
                Save(guild);
            }

            _guilds[guild.Id] = guild;
            foreach (GuildMember member in guild.Members)
            {
                _memberOf[member.CharacterId] = guild;
            }

            _nextId = Math.Max(_nextId, guild.Id + 1);
        }

        IsLoaded = true;
    }

    // --- client requests -------------------------------------------------------------------------

    /// <summary>CMSG_GUILD_QUERY (vmangos HandleGuildQueryOpcode).</summary>
    public void Query(Player player, uint guildId)
    {
        if (Get((int)guildId) is { } guild)
        {
            player.Session.Send(WorldOpcode.SmsgGuildQueryResponse, GuildPackets.BuildQueryResponse(guild));
            return;
        }

        SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
    }

    /// <summary>
    /// CMSG_GUILD_INVITE (vmangos HandleGuildInviteOpcode): target online; inviter guilded;
    /// target not ignoring the inviter; same faction unless two-side guilds; target not guilded
    /// or invited; INVITE right. The inviter gets no reply on success.
    /// </summary>
    public void Invite(Player player, string name)
    {
        Player? target = name.Length == 0 ? null : context.World.FindOnlinePlayer(name);
        if (target is null)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.PlayerNotFoundS);
            return;
        }

        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (context.IsIgnoring(target, player.Guid))
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.IgnoringYouS);
            return;
        }

        if (!context.Options.AllowTwoSideGuild && target.Team != player.Team)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.NotAllied);
            return;
        }

        if (GetGuildOf(target) is not null)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.AlreadyInGuildS);
            return;
        }

        if (InvitedTo(target) != 0)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.AlreadyInvitedToGuildS);
            return;
        }

        if (!guild.HasRight(RankOf(guild, player), GuildRights.Invite))
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return;
        }

        _invites[target.Guid] = (guild.Id, player.Guid);
        target.Session.Send(WorldOpcode.SmsgGuildInvite, GuildPackets.BuildInvite(player.Name, guild.Name));
    }

    /// <summary>
    /// CMSG_GUILD_ACCEPT (vmangos HandleGuildAcceptOpcode): join at the lowest rank when the
    /// guild exists, the player is unguilded and (without two-side guilds) shares the leader's
    /// faction; GE_JOINED to the guild.
    /// </summary>
    public void Accept(Player player)
    {
        int invitedTo = InvitedTo(player);
        _invites.Remove(player.Guid);
        if (Get(invitedTo) is not { } guild || GetGuildOf(player) is not null)
        {
            return;
        }

        if (!context.Options.AllowTwoSideGuild && context.Characters.Find(guild.LeaderId)?.Team is { } leaderTeam && leaderTeam != player.Team)
        {
            return;
        }

        AddMember(guild, player.Guid.Low, guild.LowestRank);
        Save(guild);
        BroadcastEvent(guild, GuildEvent.Joined, player.Guid, player.Name);
    }

    /// <summary>CMSG_GUILD_DECLINE (vmangos HandleGuildDeclineOpcode): SMSG_GUILD_DECLINE to the inviter.</summary>
    public void Decline(Player player)
    {
        if (GetGuildOf(player) is not null || !_invites.Remove(player.Guid, out var invite))
        {
            return;
        }

        context.World.FindOnlinePlayer(invite.Inviter)?.Session.Send(WorldOpcode.SmsgGuildDecline, GuildPackets.BuildDecline(player.Name));
    }

    /// <summary>CMSG_GUILD_INFO (vmangos HandleGuildInfoOpcode).</summary>
    public void Info(Player player)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        int accounts = guild.Members.Select(m => context.Characters.Find(m.CharacterId)?.AccountId ?? -1).Distinct().Count();
        player.Session.Send(WorldOpcode.SmsgGuildInfo, GuildPackets.BuildInfo(guild, accounts));
    }

    /// <summary>CMSG_GUILD_ROSTER (vmangos HandleGuildRosterOpcode): silent when unguilded.</summary>
    public void Roster(Player player)
    {
        if (GetGuildOf(player) is { } guild)
        {
            SendRoster(guild, player);
        }
    }

    /// <summary>CMSG_GUILD_PROMOTE (vmangos HandleGuildPromoteOpcode).</summary>
    public void Promote(Player player, string name)
    {
        if (!TryGetRankTarget(player, name, GuildRights.Promote, out Guild? guild, out GuildMember? member))
        {
            return;
        }

        if (RankOf(guild, player) + 1 >= member.Rank)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.RankTooHighS);
            return;
        }

        byte newRank = (byte)(member.Rank - 1);
        ChangeRank(guild, member, newRank);
        Save(guild);
        BroadcastEvent(guild, GuildEvent.Promotion, ObjectGuid.Empty, player.Name, NameOf(member), guild.RankName(newRank));
    }

    /// <summary>CMSG_GUILD_DEMOTE (vmangos HandleGuildDemoteOpcode).</summary>
    public void Demote(Player player, string name)
    {
        if (!TryGetRankTarget(player, name, GuildRights.Demote, out Guild? guild, out GuildMember? member))
        {
            return;
        }

        if (RankOf(guild, player) >= member.Rank)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.RankTooHighS);
            return;
        }

        if (member.Rank >= guild.LowestRank)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.RankTooLowS);
            return;
        }

        byte newRank = (byte)(member.Rank + 1);
        ChangeRank(guild, member, newRank);
        Save(guild);
        BroadcastEvent(guild, GuildEvent.Demotion, ObjectGuid.Empty, player.Name, NameOf(member), guild.RankName(newRank));
    }

    /// <summary>
    /// CMSG_GUILD_REMOVE (vmangos HandleGuildRemoveOpcode): REMOVE right; the target is a
    /// member below the remover's rank and not the leader; GE_REMOVED to the remaining members.
    /// </summary>
    public void Remove(Player player, string name)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (!guild.HasRight(RankOf(guild, player), GuildRights.Remove))
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return;
        }

        if (FindMember(guild, name) is not { } member)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.PlayerNotInGuildS);
            return;
        }

        if (member.Rank == Guild.GuildMasterRank)
        {
            SendResult(player, GuildCommand.Quit, string.Empty, GuildCommandError.Permissions);
            return;
        }

        if (RankOf(guild, player) >= member.Rank)
        {
            SendResult(player, GuildCommand.Quit, name, GuildCommandError.RankTooHighS);
            return;
        }

        string memberName = NameOf(member);
        if (DeleteMember(guild, member.CharacterId))
        {
            Disband(guild);
            return;
        }

        Save(guild);
        BroadcastEvent(guild, GuildEvent.Removed, ObjectGuid.Empty, memberName, player.Name);
    }

    /// <summary>
    /// CMSG_GUILD_LEAVE (vmangos HandleGuildLeaveOpcode): the leader of a guild with other
    /// members must hand over first; a lone leader disbands; others get result(QUIT, guild, 0)
    /// and the guild GE_LEFT.
    /// </summary>
    public void Leave(Player player)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (guild.LeaderId == player.Guid.Low && guild.MemberCount > 1)
        {
            SendResult(player, GuildCommand.Quit, string.Empty, GuildCommandError.Permissions);
            return;
        }

        if (guild.LeaderId == player.Guid.Low)
        {
            Disband(guild);
            return;
        }

        SendResult(player, GuildCommand.Quit, guild.Name, GuildCommandError.PlayerNoMoreInGuild);
        if (DeleteMember(guild, player.Guid.Low))
        {
            Disband(guild);
            return;
        }

        Save(guild);
        BroadcastEvent(guild, GuildEvent.Left, player.Guid, player.Name);
    }

    /// <summary>CMSG_GUILD_DISBAND (vmangos HandleGuildDisbandOpcode): leader only.</summary>
    public void Disband(Player player)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (guild.LeaderId != player.Guid.Low)
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return;
        }

        Disband(guild);
    }

    /// <summary>CMSG_GUILD_LEADER (vmangos HandleGuildLeaderOpcode): the old leader becomes an officer.</summary>
    public void SetLeader(Player player, string name)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (guild.LeaderId != player.Guid.Low || guild.Find(player.Guid.Low) is not { } oldSlot)
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return;
        }

        if (FindMember(guild, name) is not { } member)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.PlayerNotInGuildS);
            return;
        }

        SetLeader(guild, member);
        ChangeRank(guild, oldSlot, Guild.OfficerRank);
        Save(guild);
        BroadcastEvent(guild, GuildEvent.LeaderChanged, ObjectGuid.Empty, player.Name, NameOf(member));
    }

    /// <summary>CMSG_GUILD_MOTD (vmangos HandleGuildMOTDOpcode): SETMOTD right; GE_MOTD to the guild.</summary>
    public void SetMotd(Player player, string motd)
    {
        if (motd.Length > Guild.MaxMotdLength)
        {
            // vmangos kicks (anticheat); the client never sends this.
            return;
        }

        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (!guild.HasRight(RankOf(guild, player), GuildRights.SetMotd))
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return;
        }

        guild.Motd = motd;
        Save(guild);
        BroadcastEvent(guild, GuildEvent.Motd, ObjectGuid.Empty, motd);
    }

    /// <summary>CMSG_GUILD_SET_PUBLIC_NOTE (vmangos HandleGuildSetPublicNoteOpcode): EPNOTE right; roster to the setter.</summary>
    public void SetPublicNote(Player player, string name, string note) => SetNote(player, name, note, officer: false);

    /// <summary>CMSG_GUILD_SET_OFFICER_NOTE (vmangos HandleGuildSetOfficerNoteOpcode): EOFFNOTE right; roster to the setter.</summary>
    public void SetOfficerNote(Player player, string name, string note) => SetNote(player, name, note, officer: true);

    /// <summary>
    /// CMSG_GUILD_RANK (vmangos HandleGuildRankOpcode): leader only; the guild master rank
    /// always keeps every right; query response to the leader and roster to the guild.
    /// </summary>
    public void SetRank(Player player, uint rankId, uint rights, string name)
    {
        if (!TryGetLedGuild(player, out Guild? guild) || name.Length > Guild.MaxRankNameLength)
        {
            return;
        }

        if (rankId < guild.Ranks.Count)
        {
            guild.Ranks[(int)rankId].Name = name;
            guild.Ranks[(int)rankId].Rights = rankId == Guild.GuildMasterRank ? GuildRights.All : rights;
            Save(guild);
        }

        AfterRankChange(guild, player);
    }

    /// <summary>CMSG_GUILD_ADD_RANK (vmangos HandleGuildAddRankOpcode): leader only, at most 10 ranks.</summary>
    public void AddRank(Player player, string name)
    {
        if (name.Length > Guild.MaxRankNameLength || !TryGetLedGuild(player, out Guild? guild) || guild.Ranks.Count >= Guild.MaxRanks)
        {
            return;
        }

        guild.AddRank(name, GuildRights.GuildChatListen | GuildRights.GuildChatSpeak);
        Save(guild);
        AfterRankChange(guild, player);
    }

    /// <summary>
    /// CMSG_GUILD_DEL_RANK (vmangos HandleGuildDelRankOpcode → Guild::DelRank): leader only;
    /// the lowest rank goes unless only the minimum (5) remain. vmangos leaves members of the
    /// deleted rank with an out-of-range rank id; here they move to the new lowest rank.
    /// </summary>
    public void DeleteRank(Player player)
    {
        if (!TryGetLedGuild(player, out Guild? guild))
        {
            return;
        }

        if (guild.Ranks.Count > Guild.MinRanks)
        {
            guild.RemoveLowestRank();
            foreach (GuildMember member in guild.Members.Where(m => m.Rank > guild.LowestRank).ToList())
            {
                ChangeRank(guild, member, guild.LowestRank);
            }

            Save(guild);
        }

        AfterRankChange(guild, player);
    }

    /// <summary>CMSG_GUILD_INFO_TEXT (vmangos HandleGuildChangeInfoTextOpcode): MODIFY_GUILD_INFO right; no reply.</summary>
    public void SetInfo(Player player, string info)
    {
        if (info.Length > Guild.MaxInfoLength)
        {
            return;
        }

        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (!guild.HasRight(RankOf(guild, player), GuildRights.ModifyGuildInfo))
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.Permissions);
            return;
        }

        guild.Info = info;
        Save(guild);
    }

    /// <summary>
    /// Guild or officer chat (vmangos Guild::BroadcastToGuild / BroadcastChatMsgToOfficers):
    /// the speaker needs the speak right; members with the listen right who do not ignore the
    /// speaker receive it. False when the speaker is unguilded.
    /// </summary>
    public bool BroadcastChat(Player player, bool officer, ReadOnlySpan<byte> packet)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            return false;
        }

        if (!guild.HasRight(RankOf(guild, player), officer ? GuildRights.OfficerChatSpeak : GuildRights.GuildChatSpeak))
        {
            return true;
        }

        uint listen = officer ? GuildRights.OfficerChatListen : GuildRights.GuildChatListen;
        foreach (GuildMember member in guild.Members)
        {
            if (!guild.HasRight(member.Rank, listen))
            {
                continue;
            }

            if (context.World.FindOnlinePlayer(member.Guid) is { } target && !context.IsIgnoring(target, player.Guid))
            {
                target.Session.Send(WorldOpcode.SmsgMessagechat, packet);
            }
        }

        return true;
    }

    // --- world events ----------------------------------------------------------------------------

    /// <summary>
    /// A character entered the world (vmangos HandlePlayerLogin): guild fields, the guild MOTD
    /// event to the player and GE_SIGNED_ON to the guild (the player included). A character
    /// no longer in its guild gets cleared fields.
    /// </summary>
    public void OnLoggedIn(Player player)
    {
        if (GetGuildOf(player) is not { } guild || guild.Find(player.Guid.Low) is not { } member)
        {
            SetFields(player, 0, 0);
            return;
        }

        member.Level = player.Level;
        member.ZoneId = player.ZoneId;
        SetFields(player, guild.Id, member.Rank);
        player.Session.Send(WorldOpcode.SmsgGuildEvent, GuildPackets.BuildEvent(GuildEvent.Motd, ObjectGuid.Empty, guild.Motd));
        BroadcastEvent(guild, GuildEvent.SignedOn, player.Guid, player.Name);
    }

    /// <summary>
    /// A character is leaving the world (vmangos WorldSession::LogoutPlayer): the member's
    /// level, zone and logout time are stored and GE_SIGNED_OFF goes to the guild; a pending
    /// invite is dropped.
    /// </summary>
    public void OnLoggingOut(Player player)
    {
        _invites.Remove(player.Guid);
        if (GetGuildOf(player) is not { } guild || guild.Find(player.Guid.Low) is not { } member)
        {
            return;
        }

        member.Level = player.Level;
        member.ZoneId = player.ZoneId;
        member.LogoutTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Save(guild);
        BroadcastEvent(guild, GuildEvent.SignedOff, player.Guid, player.Name);
    }

    // --- GM commands (vmangos Level2/Level3 .guild commands) --------------------------------------

    /// <summary>.guild create (vmangos HandleGuildCreateCommand → Guild::Create): the leader founds a guild with default ranks.</summary>
    public GuildAdminResult Create(uint leaderId, string name, out Guild? guild)
    {
        guild = null;
        if (!IsLoaded)
        {
            return GuildAdminResult.NotLoaded;
        }

        name = name.Trim();
        if (name.Length == 0 || name.Length > Guild.MaxNameLength)
        {
            return GuildAdminResult.NameInvalid;
        }

        if (GetByName(name) is not null)
        {
            return GuildAdminResult.NameExists;
        }

        if (context.Characters.Find(leaderId) is null)
        {
            return GuildAdminResult.CharacterNotFound;
        }

        if (GetGuildOf(leaderId) is not null)
        {
            return GuildAdminResult.AlreadyInGuild;
        }

        guild = new Guild(_nextId++, name, leaderId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        guild.CreateDefaultRanks();
        _guilds[guild.Id] = guild;
        AddMember(guild, leaderId, Guild.GuildMasterRank);
        Save(guild);
        return GuildAdminResult.Ok;
    }

    /// <summary>.guild delete (vmangos HandleGuildDeleteCommand → Guild::Disband).</summary>
    public GuildAdminResult Delete(string name)
    {
        if (GetByName(name) is not { } guild)
        {
            return GuildAdminResult.GuildNotFound;
        }

        Disband(guild);
        return GuildAdminResult.Ok;
    }

    /// <summary>.guild invite (vmangos HandleGuildInviteCommand): add a character at the lowest rank.</summary>
    public GuildAdminResult AdminInvite(uint characterId, string guildName)
    {
        if (GetByName(guildName) is not { } guild)
        {
            return GuildAdminResult.GuildNotFound;
        }

        if (context.Characters.Find(characterId) is not { } character)
        {
            return GuildAdminResult.CharacterNotFound;
        }

        if (GetGuildOf(characterId) is not null)
        {
            return GuildAdminResult.AlreadyInGuild;
        }

        AddMember(guild, characterId, guild.LowestRank);
        Save(guild);
        BroadcastEvent(guild, GuildEvent.Joined, character.Guid, character.Name);
        return GuildAdminResult.Ok;
    }

    /// <summary>.guild uninvite (vmangos HandleGuildUninviteCommand → Guild::DelMember).</summary>
    public GuildAdminResult AdminUninvite(uint characterId)
    {
        if (GetGuildOf(characterId) is not { } guild || guild.Find(characterId) is not { } member)
        {
            return GuildAdminResult.NotInGuild;
        }

        string name = NameOf(member);
        if (DeleteMember(guild, characterId))
        {
            Disband(guild);
            return GuildAdminResult.Ok;
        }

        Save(guild);
        BroadcastEvent(guild, GuildEvent.Left, ObjectGuid.Player(characterId), name);
        return GuildAdminResult.Ok;
    }

    /// <summary>.guild rank (vmangos HandleGuildRankCommand → MemberSlot::ChangeRank).</summary>
    public GuildAdminResult AdminSetRank(uint characterId, byte rank)
    {
        if (GetGuildOf(characterId) is not { } guild || guild.Find(characterId) is not { } member)
        {
            return GuildAdminResult.NotInGuild;
        }

        if (rank > guild.LowestRank)
        {
            return GuildAdminResult.RankInvalid;
        }

        ChangeRank(guild, member, rank);
        Save(guild);
        return GuildAdminResult.Ok;
    }

    // --- internals --------------------------------------------------------------------------------

    /// <summary>Send a guild event to the online members (vmangos Guild::BroadcastEvent).</summary>
    public void BroadcastEvent(Guild guild, GuildEvent guildEvent, ObjectGuid guid, params string[] strings)
        => Broadcast(guild, WorldOpcode.SmsgGuildEvent, GuildPackets.BuildEvent(guildEvent, guid, strings));

    private void Broadcast(Guild guild, WorldOpcode opcode, byte[] payload)
    {
        foreach (GuildMember member in guild.Members)
        {
            context.World.FindOnlinePlayer(member.Guid)?.Session.Send(opcode, payload);
        }
    }

    /// <summary>
    /// SMSG_GUILD_ROSTER (vmangos Guild::SendGuildRoster): to one viewer (officer notes when it
    /// has VIEWOFFNOTE) or, with no viewer, to the whole guild without officer notes.
    /// </summary>
    private void SendRoster(Guild guild, Player? viewer)
    {
        bool officerNotes = viewer is not null && guild.HasRight(RankOf(guild, viewer), GuildRights.ViewOfficerNote);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entries = new List<GuildRosterEntry>(guild.MemberCount);
        foreach (GuildMember m in guild.Members)
        {
            CharacterInfo? info = context.Characters.Find(m.CharacterId);
            string name = info?.Name ?? string.Empty;
            string officerNote = officerNotes ? m.OfficerNote : string.Empty;
            if (context.World.FindOnlinePlayer(m.Guid) is { } online)
            {
                GuildRosterFlags flags = GuildRosterFlags.Online
                    | (online.IsAfk ? GuildRosterFlags.Afk : 0)
                    | (online.IsDnd ? GuildRosterFlags.Dnd : 0);
                entries.Add(new GuildRosterEntry(m.Guid, flags, name, m.Rank, online.Level, (byte)online.Class, online.ZoneId, 0, m.PublicNote, officerNote));
            }
            else
            {
                float days = (now - m.LogoutTime) / 86400f;
                entries.Add(new GuildRosterEntry(m.Guid, GuildRosterFlags.Offline, name, m.Rank, m.Level, (byte)(info?.Class ?? 0), m.ZoneId, days, m.PublicNote, officerNote));
            }
        }

        byte[] packet = GuildPackets.BuildRoster(guild, entries);
        if (viewer is not null)
        {
            viewer.Session.Send(WorldOpcode.SmsgGuildRoster, packet);
        }
        else
        {
            Broadcast(guild, WorldOpcode.SmsgGuildRoster, packet);
        }
    }

    private void SetNote(Player player, string name, string note, bool officer)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (!guild.HasRight(RankOf(guild, player), officer ? GuildRights.EditOfficerNote : GuildRights.EditPublicNote))
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return;
        }

        if (FindMember(guild, name) is not { } member)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.PlayerNotInGuildS);
            return;
        }

        if (note.Length > Guild.MaxNoteLength)
        {
            return;
        }

        if (officer)
        {
            member.OfficerNote = note;
        }
        else
        {
            member.PublicNote = note;
        }

        Save(guild);
        SendRoster(guild, player);
    }

    private void AfterRankChange(Guild guild, Player leader)
    {
        leader.Session.Send(WorldOpcode.SmsgGuildQueryResponse, GuildPackets.BuildQueryResponse(guild));
        SendRoster(guild, null);
    }

    private bool TryGetLedGuild(Player player, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Guild? guild)
    {
        guild = GetGuildOf(player);
        if (guild is null)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return false;
        }

        if (guild.LeaderId != player.Guid.Low)
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return false;
        }

        return true;
    }

    /// <summary>The checks shared by promote and demote (vmangos HandleGuildPromoteOpcode / HandleGuildDemoteOpcode).</summary>
    private bool TryGetRankTarget(
        Player player,
        string name,
        uint right,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Guild? guild,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GuildMember? member)
    {
        member = null;
        guild = GetGuildOf(player);
        if (guild is null)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return false;
        }

        if (!guild.HasRight(RankOf(guild, player), right))
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return false;
        }

        member = FindMember(guild, name);
        if (member is null)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.PlayerNotInGuildS);
            return false;
        }

        if (member.CharacterId == player.Guid.Low)
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.NameInvalid);
            return false;
        }

        return true;
    }

    private GuildMember? FindMember(Guild guild, string name)
        => name.Length == 0 || context.Characters.FindByName(name) is not { } info ? null : guild.Find(info.Id);

    private string NameOf(GuildMember member) => context.Characters.Find(member.CharacterId)?.Name ?? string.Empty;

    private static byte RankOf(Guild guild, Player player) => guild.Find(player.Guid.Low)?.Rank ?? guild.LowestRank;

    private void AddMember(Guild guild, uint characterId, byte rank, int exceptPetitionId = 0)
    {
        guild.AddMember(characterId, rank);
        _memberOf[characterId] = guild;
        if (context.World.FindOnlinePlayer(ObjectGuid.Player(characterId)) is { } online)
        {
            GuildMember member = guild.Find(characterId)!;
            member.Level = online.Level;
            member.ZoneId = online.ZoneId;
            SetFields(online, guild.Id, rank);
            _invites.Remove(online.Guid);
        }

        MemberJoined?.Invoke(guild, characterId, exceptPetitionId);
    }

    /// <summary>
    /// vmangos Guild::DelMember: removing the leader hands the guild to the best ranked member
    /// (GE_LEADER_CHANGED and GE_LEFT). True when nobody is left and the guild must disband.
    /// </summary>
    private bool DeleteMember(Guild guild, uint characterId, bool disbanding = false)
    {
        if (!disbanding && guild.LeaderId == characterId)
        {
            GuildMember? best = guild.Members.Where(m => m.CharacterId != characterId).OrderBy(m => m.Rank).FirstOrDefault();
            if (best is null)
            {
                return true;
            }

            string oldName = context.Characters.Find(characterId)?.Name ?? string.Empty;
            SetLeader(guild, best);
            BroadcastEvent(guild, GuildEvent.LeaderChanged, ObjectGuid.Empty, oldName, NameOf(best));
            BroadcastEvent(guild, GuildEvent.Left, ObjectGuid.Player(characterId), oldName);
        }

        guild.RemoveMember(characterId);
        _memberOf.Remove(characterId);
        if (context.World.FindOnlinePlayer(ObjectGuid.Player(characterId)) is { } online)
        {
            SetFields(online, 0, 0);
        }

        MemberLeft?.Invoke(guild, characterId);
        return guild.MemberCount == 0;
    }

    private void SetLeader(Guild guild, GuildMember member)
    {
        guild.LeaderId = member.CharacterId;
        ChangeRank(guild, member, Guild.GuildMasterRank);
    }

    private void ChangeRank(Guild guild, GuildMember member, byte rank)
    {
        member.Rank = rank;
        if (context.World.FindOnlinePlayer(member.Guid) is { } online)
        {
            SetFields(online, guild.Id, rank);
        }
    }

    /// <summary>vmangos Guild::Disband: GE_DISBANDED, every member removed, the guild deleted.</summary>
    private void Disband(Guild guild)
    {
        BroadcastEvent(guild, GuildEvent.Disbanded, ObjectGuid.Empty);
        foreach (GuildMember member in guild.Members.ToList())
        {
            DeleteMember(guild, member.CharacterId, disbanding: true);
        }

        foreach (ObjectGuid invited in _invites.Where(i => i.Value.GuildId == guild.Id).Select(i => i.Key).ToList())
        {
            _invites.Remove(invited);
        }

        _guilds.Remove(guild.Id);
        context.Persistence.DeleteGuild(guild.Id);
    }

    private void Save(Guild guild) => context.Persistence.SaveGuild(guild.ToData());

    private static void SetFields(Player player, int guildId, byte rank)
    {
        player.SetUInt32(UpdateFields.PlayerGuildid, (uint)guildId);
        player.SetUInt32(UpdateFields.PlayerGuildrank, rank);
    }

    private static void SendResult(Player player, GuildCommand command, string text, GuildCommandError error)
        => player.Session.Send(WorldOpcode.SmsgGuildCommandResult, GuildPackets.BuildCommandResult(command, text, error));
}
