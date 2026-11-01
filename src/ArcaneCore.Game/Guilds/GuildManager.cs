using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Characters;
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

    /// <summary>This worker is not the Social holder; guild administration runs on the holder.</summary>
    NotHolder,
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

    /// <summary>
    /// The link to the Social lease holder when this worker is part of a cluster; null in a single process (every rule then runs
    /// here, as before). On the holder every operation runs against this manager and its results are published through the link; on any other worker the guild
    /// state held here is a read replica (<see cref="ApplyReplica"/>) and every operation that changes guild state is forwarded to the holder
    /// (<see cref="IGuildHolderLink.Forward"/>). World thread.
    /// </summary>
    public IGuildHolderLink? Link { get; set; }

    /// <summary>True when this manager decides guild state: a single process, or the Social lease holder.</summary>
    public bool IsAuthority => Link is null || Link.IsHolder;

    private bool Forwarding => Link is { IsHolder: false };

    /// <summary>
    /// Optional durable block allocator for guild IDs when cluster ID range reservation is enabled.
    /// </summary>
    public DurableIdBlockAllocator? IdBlockAllocator { get; set; }

    private int AllocateGuildId()
    {
        if (IdBlockAllocator is { } allocator)
        {
            int allocated = allocator.NextInt32();
            if (allocated >= _nextId && allocated < int.MaxValue)
                _nextId = allocated + 1;
            return allocated;
        }

        if (!IsAuthority)
            throw new InvalidOperationException("guild ids are allocated by the Social holder");

        if (_nextId <= 0)
            throw new IdRangeExhaustedException(IdRangeScopes.Guild, "guild ID space exhausted");

        return _nextId++;
    }

    public IReadOnlyCollection<Guild> All => _guilds.Values;

    /// <summary>Whether <see cref="Load"/> has run (guild creation waits for it so ids never collide with stored guilds).</summary>
    public bool IsLoaded { get; private set; }

    public Guild? Get(int id) => _guilds.GetValueOrDefault(id);

    /// <summary>vmangos GuildMgr::GetGuildByName (case-insensitive).</summary>
    public Guild? GetByName(string name) => _guilds.Values.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public Guild? GetGuildOf(uint characterId) => _memberOf.GetValueOrDefault(characterId);

    public Guild? GetGuildOf(Player player) => GetGuildOf(player.Guid.Low);

    private Guild? GetGuildOf(GuildActor actor) => GetGuildOf(actor.Guid.Low);

    /// <summary>The guild <paramref name="player"/> has been invited to, if any (vmangos Player::GetGuildIdInvited).</summary>
    public int InvitedTo(Player player) => InvitedTo(player.Guid);

    /// <summary>The guild <paramref name="character"/> has been invited to, if any; invitations are kept by the holder.</summary>
    public int InvitedTo(ObjectGuid character) => _invites.TryGetValue(character, out var invite) ? invite.GuildId : 0;

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

            // vmangos Guild.cpp:396-398: the first rank is the guild master's, "prevent loss leader rights".
            guild.Ranks[0].Rights |= GuildRights.All;

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

            // vmangos Guild::CheckGuildStructure (Guild.cpp:328-350).
            bool repaired = false;
            if (guild.Find(guild.LeaderId) is not { } leader)
            {
                // DelMember (Guild.cpp:539-561): a missing leader is replaced by the best ranked member,
                // with no broadcast (the old slot is gone).
                GuildMember best = guild.Members.OrderBy(m => m.Rank).First();
                SetLeader(guild, best);
                repaired = true;
            }
            else if (leader.Rank != Guild.GuildMasterRank)
            {
                // The leader's rank is the guild master's (Guild.cpp:339-340). Left on a lower rank,
                // the leader cannot invite, speak or remove.
                ChangeRank(guild, leader, Guild.GuildMasterRank);
                repaired = true;
            }

            // Only the leader may sit on the guild master rank (Guild.cpp:342-348). A second one
            // cannot be demoted or removed: both refuse rank 0.
            foreach (GuildMember member in guild.Members)
            {
                if (member.CharacterId != guild.LeaderId && member.Rank == Guild.GuildMasterRank)
                {
                    ChangeRank(guild, member, Guild.OfficerRank);
                    repaired = true;
                }
            }

            if (repaired)
            {
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
        // A query is answered from the replica on any worker (the replica is what the holder last published).
        if (Get((int)guildId) is { } guild)
        {
            player.Session.Send(WorldOpcode.SmsgGuildQueryResponse, GuildPackets.BuildQueryResponse(guild));
            return;
        }

        SendResult(GuildActor.Of(player), GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
    }

    /// <summary>
    /// CMSG_GUILD_INVITE (vmangos HandleGuildInviteOpcode): target online; inviter guilded;
    /// target not ignoring the inviter; same faction unless two-side guilds; target not guilded
    /// or invited; INVITE right. The inviter gets no reply on success.
    /// </summary>
    public void Invite(Player player, string name)
    {
        if (!TryForward(player, GuildRequestKind.Invite, name))
        {
            Invite(GuildActor.Of(player), name);
        }
    }

    private void Invite(GuildActor player, string name)
    {
        OnlineView? target = name.Length == 0 ? null : FindOnline(name);
        if (target is not { } online)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.PlayerNotFoundS);
            return;
        }

        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        if (IsIgnoring(online, player.Guid))
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.IgnoringYouS);
            return;
        }

        if (!context.Options.AllowTwoSideGuild && online.Team != player.Team)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.NotAllied);
            return;
        }

        if (GetGuildOf(online.Guid.Low) is not null)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.AlreadyInGuildS);
            return;
        }

        if (InvitedTo(online.Guid) != 0)
        {
            SendResult(player, GuildCommand.Invite, name, GuildCommandError.AlreadyInvitedToGuildS);
            return;
        }

        if (!guild.HasRight(RankOf(guild, player), GuildRights.Invite))
        {
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.Permissions);
            return;
        }

        _invites[online.Guid] = (guild.Id, player.Guid);
        SendTo(online.Guid, WorldOpcode.SmsgGuildInvite, GuildPackets.BuildInvite(player.Name, guild.Name));
    }

    /// <summary>
    /// CMSG_GUILD_ACCEPT (vmangos HandleGuildAcceptOpcode): join at the lowest rank when the
    /// guild exists, the player is unguilded and (without two-side guilds) shares the leader's
    /// faction; GE_JOINED to the guild.
    /// </summary>
    public void Accept(Player player)
    {
        if (!TryForward(player, GuildRequestKind.Accept))
        {
            Accept(GuildActor.Of(player));
        }
    }

    private void Accept(GuildActor player)
    {
        int invitedTo = InvitedTo(player.Guid);
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
        if (!TryForward(player, GuildRequestKind.Decline))
        {
            Decline(GuildActor.Of(player));
        }
    }

    private void Decline(GuildActor player)
    {
        if (GetGuildOf(player) is not null || !_invites.Remove(player.Guid, out var invite))
        {
            return;
        }

        SendTo(invite.Inviter, WorldOpcode.SmsgGuildDecline, GuildPackets.BuildDecline(player.Name));
    }

    /// <summary>CMSG_GUILD_INFO (vmangos HandleGuildInfoOpcode).</summary>
    public void Info(Player player)
    {
        if (!TryForward(player, GuildRequestKind.Info))
        {
            Info(GuildActor.Of(player));
        }
    }

    private void Info(GuildActor player)
    {
        if (GetGuildOf(player) is not { } guild)
        {
            SendResult(player, GuildCommand.Create, string.Empty, GuildCommandError.PlayerNotInGuild);
            return;
        }

        int accounts = guild.Members.Select(m => context.Characters.Find(m.CharacterId)?.AccountId ?? -1).Distinct().Count();
        player.Send(WorldOpcode.SmsgGuildInfo, GuildPackets.BuildInfo(guild, accounts));
    }

    /// <summary>CMSG_GUILD_ROSTER (vmangos HandleGuildRosterOpcode): silent when unguilded.</summary>
    public void Roster(Player player)
    {
        // The roster needs the holder's view of who is online across workers; the holder builds it and sends it to the requester.
        if (!TryForward(player, GuildRequestKind.Roster))
        {
            Roster(GuildActor.Of(player));
        }
    }

    private void Roster(GuildActor player)
    {
        if (GetGuildOf(player) is { } guild)
        {
            SendRoster(guild, player);
        }
    }

    /// <summary>CMSG_GUILD_PROMOTE (vmangos HandleGuildPromoteOpcode).</summary>
    public void Promote(Player player, string name)
    {
        if (!TryForward(player, GuildRequestKind.Promote, name))
        {
            Promote(GuildActor.Of(player), name);
        }
    }

    private void Promote(GuildActor player, string name)
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
        if (!TryForward(player, GuildRequestKind.Demote, name))
        {
            Demote(GuildActor.Of(player), name);
        }
    }

    private void Demote(GuildActor player, string name)
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
        if (!TryForward(player, GuildRequestKind.Remove, name))
        {
            Remove(GuildActor.Of(player), name);
        }
    }

    private void Remove(GuildActor player, string name)
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
        if (!TryForward(player, GuildRequestKind.Leave))
        {
            Leave(GuildActor.Of(player));
        }
    }

    private void Leave(GuildActor player)
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
        if (!TryForward(player, GuildRequestKind.Disband))
        {
            Disband(GuildActor.Of(player));
        }
    }

    private void Disband(GuildActor player)
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
        if (!TryForward(player, GuildRequestKind.SetLeader, name))
        {
            SetLeader(GuildActor.Of(player), name);
        }
    }

    private void SetLeader(GuildActor player, string name)
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

        if (member.CharacterId == oldSlot.CharacterId)
        {
            // Beyond vmangos, which has no self check: handing leadership to oneself would demote the leader to an officer
            // (SetLeader then ChangeRank on the same member), the state Load repairs.
            SendResult(player, GuildCommand.Invite, string.Empty, GuildCommandError.NameInvalid);
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
        if (Forwarding && RejectOversized(player, motd, Guild.MaxMotdLength))
        {
            return;
        }

        if (!TryForward(player, GuildRequestKind.SetMotd, motd))
        {
            SetMotd(GuildActor.Of(player), motd);
        }
    }

    private void SetMotd(GuildActor player, string motd)
    {
        if (RejectOversized(player, motd, Guild.MaxMotdLength))
        {
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
        if (Forwarding && RejectOversized(player, name, Guild.MaxRankNameLength))
        {
            return;
        }

        if (!TryForward(player, GuildRequestKind.SetRank, name, string.Empty, rankId, rights))
        {
            SetRank(GuildActor.Of(player), rankId, rights, name);
        }
    }

    private void SetRank(GuildActor player, uint rankId, uint rights, string name)
    {
        if (!TryGetLedGuild(player, out Guild? guild) || RejectOversized(player, name, Guild.MaxRankNameLength))
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
        if (Forwarding && RejectOversized(player, name, Guild.MaxRankNameLength))
        {
            return;
        }

        if (!TryForward(player, GuildRequestKind.AddRank, name))
        {
            AddRank(GuildActor.Of(player), name);
        }
    }

    private void AddRank(GuildActor player, string name)
    {
        // vmangos HandleGuildAddRankOpcode checks the length before anything else (GuildHandler.cpp:600-604).
        if (RejectOversized(player, name, Guild.MaxRankNameLength) || !TryGetLedGuild(player, out Guild? guild) || guild.Ranks.Count >= Guild.MaxRanks)
        {
            return;
        }

        guild.AddRank(name, GuildRights.GuildChatListen | GuildRights.GuildChatSpeak);
        Save(guild);
        AfterRankChange(guild, player);
    }

    /// <summary>
    /// CMSG_GUILD_DEL_RANK (vmangos HandleGuildDelRankOpcode → Guild::DelRank): leader only;
    /// the lowest rank goes unless only the minimum (5) remain. Members of the deleted rank keep
    /// the out-of-range id like vmangos (Guild.cpp:696-707; "&lt;unknown&gt;", no rights, clamped at the
    /// next load, :458-460) unless <see cref="GuildOptions.DeleteRankMovesMembers"/> moves them.
    /// </summary>
    public void DeleteRank(Player player)
    {
        if (!TryForward(player, GuildRequestKind.DeleteRank))
        {
            DeleteRank(GuildActor.Of(player));
        }
    }

    private void DeleteRank(GuildActor player)
    {
        if (!TryGetLedGuild(player, out Guild? guild))
        {
            return;
        }

        if (guild.Ranks.Count > Guild.MinRanks)
        {
            guild.RemoveLowestRank();
            if (Options.DeleteRankMovesMembers)
            {
                foreach (GuildMember member in guild.Members.Where(m => m.Rank > guild.LowestRank).ToList())
                {
                    ChangeRank(guild, member, guild.LowestRank);
                }
            }

            Save(guild);
        }

        AfterRankChange(guild, player);
    }

    /// <summary>CMSG_GUILD_INFO_TEXT (vmangos HandleGuildChangeInfoTextOpcode): MODIFY_GUILD_INFO right; no reply.</summary>
    public void SetInfo(Player player, string info)
    {
        if (Forwarding && RejectOversized(player, info, Guild.MaxInfoLength))
        {
            return;
        }

        if (!TryForward(player, GuildRequestKind.SetInfo, info))
        {
            SetInfo(GuildActor.Of(player), info);
        }
    }

    private void SetInfo(GuildActor player, string info)
    {
        if (RejectOversized(player, info, Guild.MaxInfoLength))
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

        if (Forwarding)
        {
            // The replica gives this worker's player its guild fields and the MOTD; the holder updates the member row and tells every member's worker.
            SetFields(player, guild.Id, member.Rank);
            player.Session.Send(WorldOpcode.SmsgGuildEvent, GuildPackets.BuildEvent(GuildEvent.Motd, ObjectGuid.Empty, guild.Motd));
            TryForward(player, GuildRequestKind.LoggedIn, a: player.Level, b: player.ZoneId, reportFailure: false);
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
    /// <param name="transferredAway">The character left through a committed cluster transfer and is still online elsewhere: only the pending invite
    /// is forgotten; the member row is not updated or saved and no SignedOff is sent.</param>
    public void OnLoggingOut(Player player, bool transferredAway = false)
    {
        if (Forwarding)
        {
            // Invitations and the member row are the holder's; a transfer only drops the pending invite.
            if (transferredAway)
            {
                TryForward(player, GuildRequestKind.ForgetInvite, reportFailure: false);
            }
            else if (GetGuildOf(player) is not null)
            {
                TryForward(player, GuildRequestKind.LoggedOut, a: player.Level, b: player.ZoneId, reportFailure: false);
            }
            else
            {
                TryForward(player, GuildRequestKind.ForgetInvite, reportFailure: false);
            }

            return;
        }

        _invites.Remove(player.Guid);
        if (transferredAway)
        {
            return;
        }

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

    /// <summary>
    /// The guild-name antispam check of Guild::Create (Guild.cpp:130-137); null (the default, as vmangos) filters nothing.
    /// </summary>
    public ICharterAntispamFilter? Antispam { get; set; }

    /// <summary>
    /// .guild create and CMSG_GUILD_CREATE (vmangos HandleGuildCreateCommand / HandleGuildCreateOpcode → Guild::Create): the
    /// leader founds a guild with default ranks; a name the antispam check calls spam is refused as invalid.
    /// </summary>
    public GuildAdminResult Create(uint leaderId, string name, out Guild? guild)
    {
        guild = null;
        if (!IsAuthority)
        {
            return GuildAdminResult.NotHolder;
        }

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

        if (Antispam?.IsSpam(name) == true)
        {
            return GuildAdminResult.NameInvalid; // "Attempt to create guild with spam name"
        }

        if (context.Characters.Find(leaderId) is null)
        {
            return GuildAdminResult.CharacterNotFound;
        }

        if (GetGuildOf(leaderId) is not null)
        {
            return GuildAdminResult.AlreadyInGuild;
        }

        guild = new Guild(AllocateGuildId(), name, leaderId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        guild.CreateDefaultRanks();
        _guilds[guild.Id] = guild;
        AddMember(guild, leaderId, Guild.GuildMasterRank);
        Save(guild);
        return GuildAdminResult.Ok;
    }

    /// <summary>.guild delete (vmangos HandleGuildDeleteCommand → Guild::Disband).</summary>
    public GuildAdminResult Delete(string name)
    {
        if (!IsAuthority)
        {
            return GuildAdminResult.NotHolder;
        }

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
        if (!IsAuthority)
        {
            return GuildAdminResult.NotHolder;
        }

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
        if (Link is { IsHolder: false } link)
        {
            // The character leaves its guild at the holder (a deleted character's cleanup runs on whichever worker handled the deletion).
            return link.Forward(new GuildRequest(GuildRequestKind.AdminUninvite, ObjectGuid.Player(characterId)), null) ? GuildAdminResult.Ok : GuildAdminResult.NotHolder;
        }

        if (GetGuildOf(characterId) is not { } guild || guild.Find(characterId) is not { } member)
        {
            return GuildAdminResult.NotInGuild;
        }

        string name = NameOf(member);
        bool wasLeader = guild.LeaderId == characterId;
        if (DeleteMember(guild, characterId))
        {
            Disband(guild);
            return GuildAdminResult.Ok;
        }

        Save(guild);
        if (!wasLeader)
        {
            // DeleteMember already broadcast GE_LEADER_CHANGED and GE_LEFT for a removed guild master (Guild.cpp:569-573).
            BroadcastEvent(guild, GuildEvent.Left, ObjectGuid.Player(characterId), name);
        }

        return GuildAdminResult.Ok;
    }

    /// <summary>.guild rank (vmangos HandleGuildRankCommand → MemberSlot::ChangeRank).</summary>
    public GuildAdminResult AdminSetRank(uint characterId, byte rank)
    {
        if (!IsAuthority)
        {
            return GuildAdminResult.NotHolder;
        }

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
            SendTo(member.Guid, opcode, payload);
        }
    }

    /// <summary>
    /// SMSG_GUILD_ROSTER (vmangos Guild::SendGuildRoster): to one viewer (officer notes when it
    /// has VIEWOFFNOTE) or, with no viewer, to the whole guild without officer notes.
    /// </summary>
    private void SendRoster(Guild guild, GuildActor? viewer)
    {
        bool officerNotes = viewer is not null && guild.HasRight(RankOf(guild, viewer), GuildRights.ViewOfficerNote);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var entries = new List<GuildRosterEntry>(guild.MemberCount);
        foreach (GuildMember m in guild.Members)
        {
            CharacterInfo? info = context.Characters.Find(m.CharacterId);
            string name = info?.Name ?? string.Empty;
            string officerNote = officerNotes ? m.OfficerNote : string.Empty;
            if (FindOnline(m.Guid) is { } online)
            {
                GuildRosterFlags flags = GuildRosterFlags.Online
                    | (online.Afk ? GuildRosterFlags.Afk : 0)
                    | (online.Dnd ? GuildRosterFlags.Dnd : 0);
                entries.Add(new GuildRosterEntry(m.Guid, flags, name, m.Rank, online.Level, online.Class, online.ZoneId, 0, m.PublicNote, officerNote));
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
            viewer.Send(WorldOpcode.SmsgGuildRoster, packet);
        }
        else
        {
            Broadcast(guild, WorldOpcode.SmsgGuildRoster, packet);
        }
    }

    private void SetNote(Player player, string name, string note, bool officer)
    {
        if (Forwarding && RejectOversized(player, note, Guild.MaxNoteLength))
        {
            return;
        }

        if (!TryForward(player, officer ? GuildRequestKind.SetOfficerNote : GuildRequestKind.SetPublicNote, name, note))
        {
            SetNote(GuildActor.Of(player), name, note, officer);
        }
    }

    private void SetNote(GuildActor player, string name, string note, bool officer)
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

        if (RejectOversized(player, note, Guild.MaxNoteLength))
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

    private void AfterRankChange(Guild guild, GuildActor leader)
    {
        leader.Send(WorldOpcode.SmsgGuildQueryResponse, GuildPackets.BuildQueryResponse(guild));
        SendRoster(guild, null);
    }

    private bool TryGetLedGuild(GuildActor player, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Guild? guild)
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
        GuildActor player,
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

    private static byte RankOf(Guild guild, GuildActor player) => guild.Find(player.Guid.Low)?.Rank ?? guild.LowestRank;

    private void AddMember(Guild guild, uint characterId, byte rank, int exceptPetitionId = 0)
    {
        GuildMember member = guild.AddMember(characterId, rank);
        _memberOf[characterId] = guild;

        // vmangos Guild::AddMember (Guild.cpp:230-256): level/zone from the player, or from the
        // player cache for an offline character, and LogoutTime = now (:250) either way.
        member.LogoutTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (FindOnline(ObjectGuid.Player(characterId)) is { } online)
        {
            member.Level = online.Level;
            member.ZoneId = online.ZoneId;
            if (online.Local is { } local)
            {
                SetFields(local, guild.Id, rank); // a player on another worker gets its fields from the replica of the saved guild
            }

            _invites.Remove(online.Guid);
        }
        else if (context.Characters.Find(characterId) is { } cached)
        {
            member.Level = cached.Level;
            member.ZoneId = cached.ZoneId;
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
        Link?.ReplicateGuild(guild.Id, null);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is longer than <paramref name="maxLength"/> code points (vmangos
    /// utf8length, GuildHandler.cpp:58-62 and the other handlers); the session is disconnected as
    /// vmangos' passive anticheat does unless <see cref="GuildOptions.KickOnOversizedText"/> is off.
    /// The retail client never sends such text.
    /// </summary>
    public bool RejectOversized(Player player, string text, int maxLength) => RejectOversized(GuildActor.Of(player), text, maxLength);

    private bool RejectOversized(GuildActor player, string text, int maxLength)
    {
        if (CharterNameRules.CodePointCount(text) <= maxLength)
        {
            return false;
        }

        if (Options.KickOnOversizedText)
        {
            player.Kick("guild packet oversized");
        }

        return true;
    }

    /// <summary>
    /// Store the guild and, in a cluster, publish the saved state to every other worker's replica. Only the authority stores: a replica never writes
    /// (the load-time repairs run on every worker, but only the holder persists them).
    /// </summary>
    private void Save(Guild guild)
    {
        if (!IsAuthority)
        {
            return;
        }

        GuildData data = guild.ToData();
        context.Persistence.SaveGuild(data);
        Link?.ReplicateGuild(guild.Id, data);
    }

    private static void SetFields(Player player, int guildId, byte rank)
    {
        player.SetUInt32(UpdateFields.PlayerGuildid, (uint)guildId);
        player.SetUInt32(UpdateFields.PlayerGuildrank, rank);
    }

    private static void SendResult(GuildActor player, GuildCommand command, string text, GuildCommandError error)
        => player.Send(WorldOpcode.SmsgGuildCommandResult, GuildPackets.BuildCommandResult(command, text, error));

    // --- cluster: the holder serves, the other workers forward and replicate --------------------------

    /// <summary>
    /// Forward <paramref name="kind"/> for <paramref name="player"/> to the holder when this worker is not the holder. False (the caller then runs the rules itself)
    /// on the holder and in a single process. A holder that cannot be reached answers the player with an internal error result unless <paramref name="reportFailure"/> is false (login bookkeeping).
    /// </summary>
    private bool TryForward(Player player, GuildRequestKind kind, string text = "", string text2 = "", uint a = 0, uint b = 0, bool reportFailure = true)
    {
        if (Link is not { IsHolder: false } link)
        {
            return false;
        }

        if (!link.Forward(new GuildRequest(kind, player.Guid, text, text2, a, b), null) && reportFailure)
        {
            SendResult(GuildActor.Of(player), GuildCommand.Create, string.Empty, GuildCommandError.Internal);
        }

        return true;
    }

    /// <summary>
    /// CMSG_GUILD_CREATE after the handler's payload checks: honoured unless <see cref="GuildOptions.AllowClientGuildCreate"/> is off or the player is guilded, a name over
    /// <see cref="Guild.MaxNameLength"/> disconnects, a failed create sends nothing (vmangos HandleGuildCreateOpcode). Founded here on the holder, forwarded from any other worker.
    /// </summary>
    public void RequestCreate(Player player, string name)
    {
        if (!Options.AllowClientGuildCreate || GetGuildOf(player) is not null || RejectOversized(player, name, Guild.MaxNameLength))
        {
            return;
        }

        if (!TryForward(player, GuildRequestKind.Create, name))
        {
            Create(player.Guid.Low, name, out _);
        }
    }

    /// <summary>
    /// Run a request forwarded by another worker, on the holder (world thread). The actor is looked up in the character directory; its replies go back through
    /// <see cref="IGuildHolderLink.Deliver"/> to the worker it is online on. True when the request changed what it asked for (an emblem saved, a petition turned in), which the
    /// link hands to the forwarding worker's completion callback. Ignored (false) on a worker that is not the holder.
    /// </summary>
    public bool Serve(GuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsAuthority)
        {
            return false;
        }

        switch (request.Kind)
        {
            case GuildRequestKind.AdminUninvite:
                AdminUninvite(request.Actor.Low);
                return true;
            case GuildRequestKind.ForgetInvite:
                _invites.Remove(request.Actor);
                return true;
            case GuildRequestKind.PetitionPut:
            case GuildRequestKind.PetitionDelete:
            case GuildRequestKind.PetitionSign:
            case GuildRequestKind.PetitionTurnIn:
            case GuildRequestKind.PetitionCharacterDeleted:
            case GuildRequestKind.PetitionOffer:
                return context.Petitions.Serve(request);
        }

        if (context.Characters.Find(request.Actor.Low) is not { } info)
        {
            return false;
        }

        ObjectGuid guid = request.Actor;
        var actor = new GuildActor(guid, info.Name, info.Team, info.AccountId, (opcode, payload) => SendTo(guid, opcode, payload));
        switch (request.Kind)
        {
            case GuildRequestKind.Invite: Invite(actor, request.Text); break;
            case GuildRequestKind.Accept: Accept(actor); break;
            case GuildRequestKind.Decline: Decline(actor); break;
            case GuildRequestKind.Info: Info(actor); break;
            case GuildRequestKind.Roster: Roster(actor); break;
            case GuildRequestKind.Promote: Promote(actor, request.Text); break;
            case GuildRequestKind.Demote: Demote(actor, request.Text); break;
            case GuildRequestKind.Remove: Remove(actor, request.Text); break;
            case GuildRequestKind.Leave: Leave(actor); break;
            case GuildRequestKind.Disband: Disband(actor); break;
            case GuildRequestKind.SetLeader: SetLeader(actor, request.Text); break;
            case GuildRequestKind.SetMotd: SetMotd(actor, request.Text); break;
            case GuildRequestKind.SetInfo: SetInfo(actor, request.Text); break;
            case GuildRequestKind.SetPublicNote: SetNote(actor, request.Text, request.Text2, officer: false); break;
            case GuildRequestKind.SetOfficerNote: SetNote(actor, request.Text, request.Text2, officer: true); break;
            case GuildRequestKind.SetRank: SetRank(actor, request.A, request.B, request.Text); break;
            case GuildRequestKind.AddRank: AddRank(actor, request.Text); break;
            case GuildRequestKind.DeleteRank: DeleteRank(actor); break;
            case GuildRequestKind.Create:
                if (Options.AllowClientGuildCreate && GetGuildOf(actor) is null)
                {
                    Create(guid.Low, request.Text, out _);
                }

                break;
            case GuildRequestKind.SetEmblem:
                return ServeEmblem(actor, request.Values);
            case GuildRequestKind.LoggedIn:
                if (GetGuildOf(actor) is { } joined && joined.Find(guid.Low) is { } joinedMember)
                {
                    joinedMember.Level = (byte)request.A;
                    joinedMember.ZoneId = request.B;
                    BroadcastEvent(joined, GuildEvent.SignedOn, guid, actor.Name);
                }

                break;
            case GuildRequestKind.LoggedOut:
                _invites.Remove(guid);
                if (GetGuildOf(actor) is { } left && left.Find(guid.Low) is { } leftMember)
                {
                    leftMember.Level = (byte)request.A;
                    leftMember.ZoneId = request.B;
                    leftMember.LogoutTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    Save(left);
                    BroadcastEvent(left, GuildEvent.SignedOff, guid, actor.Name);
                }

                break;
            default:
                return false;
        }

        return true;
    }

    /// <summary>
    /// Install the holder's published state of one guild on a worker that is not the holder (<paramref name="data"/> null: the guild was disbanded). Players of this worker
    /// that joined, changed rank or left get their guild fields. Raises neither <see cref="MemberJoined"/> nor <see cref="MemberLeft"/>: those are the holder's, whose
    /// petition cleanup is published through its own replica. Ignored on the holder. World thread.
    /// </summary>
    public void ApplyReplica(int guildId, GuildData? data)
    {
        if (IsAuthority)
        {
            return;
        }

        Guild? old = _guilds.GetValueOrDefault(guildId);
        Guild? updated = data is null ? null : Guild.FromData(data);
        if (old is not null)
        {
            foreach (GuildMember member in old.Members)
            {
                if (updated?.Find(member.CharacterId) is not null)
                {
                    continue;
                }

                if (_memberOf.TryGetValue(member.CharacterId, out Guild? current) && ReferenceEquals(current, old))
                {
                    _memberOf.Remove(member.CharacterId);
                }

                if (context.World.FindOnlinePlayer(member.Guid) is { } gone)
                {
                    SetFields(gone, 0, 0);
                }
            }
        }

        if (updated is null)
        {
            _guilds.Remove(guildId);
            return;
        }

        _guilds[guildId] = updated;
        foreach (GuildMember member in updated.Members)
        {
            _memberOf[member.CharacterId] = updated;
            if (context.World.FindOnlinePlayer(member.Guid) is { } online)
            {
                SetFields(online, updated.Id, member.Rank);
            }
        }
    }

    /// <summary>A character that is online, here or (through the link) on another worker.</summary>
    private readonly record struct OnlineView(ObjectGuid Guid, string Name, Team Team, byte Level, byte Class, uint ZoneId, bool Afk, bool Dnd, Player? Local);

    private OnlineView? FindOnline(string name)
        => context.World.FindOnlinePlayer(name) is { } local ? ViewOf(local) : Link?.FindOnline(name) is { } remote ? ViewOf(remote) : null;

    private OnlineView? FindOnline(ObjectGuid guid)
        => context.World.FindOnlinePlayer(guid) is { } local ? ViewOf(local) : Link?.FindOnline(guid) is { } remote ? ViewOf(remote) : null;

    /// <summary>A character that is online, here or on another worker, as the rules of a petition offer need it (name, team); null when it is online nowhere.</summary>
    internal GuildRemoteCharacter? FindOnlineCharacter(ObjectGuid guid)
        => FindOnline(guid) is { } v ? new GuildRemoteCharacter(v.Guid, v.Name, v.Team, v.Level, v.Class, v.ZoneId, v.Afk, v.Dnd) : null;

    private static OnlineView ViewOf(Player p) => new(p.Guid, p.Name, p.Team, p.Level, (byte)p.Class, p.ZoneId, p.IsAfk, p.IsDnd, p);

    private static OnlineView ViewOf(GuildRemoteCharacter r) => new(r.Guid, r.Name, r.Team, r.Level, r.Class, r.ZoneId, r.Afk, r.Dnd, null);

    private bool IsIgnoring(OnlineView target, ObjectGuid ignorer)
        => target.Local is { } local ? context.IsIgnoring(local, ignorer) : Link?.TargetIgnores(target.Guid, ignorer) ?? false;

    /// <summary>
    /// Send a packet to a character: straight to its session when it is online here, otherwise to the worker it is online on through the link (dropped
    /// when it is online nowhere). Also used by <see cref="PetitionManager"/> to tell a petition's owner.
    /// </summary>
    internal void SendTo(ObjectGuid character, WorldOpcode opcode, byte[] payload)
    {
        if (context.World.FindOnlinePlayer(character) is { } local)
        {
            local.Session.Send(opcode, payload);
            return;
        }

        Link?.Deliver(character, opcode, payload);
    }
}

/// <summary>Which guild or charter operation a <see cref="GuildRequest"/> asks the holder for. Values are wire values for a link that serialises them: append only.</summary>
public enum GuildRequestKind : byte
{
    Create = 1,
    Invite = 2,
    Accept = 3,
    Decline = 4,
    Info = 5,
    Roster = 6,
    Promote = 7,
    Demote = 8,
    Remove = 9,
    Leave = 10,
    Disband = 11,
    SetLeader = 12,
    SetMotd = 13,
    SetInfo = 14,
    SetPublicNote = 15,
    SetOfficerNote = 16,
    SetRank = 17,
    AddRank = 18,
    DeleteRank = 19,

    /// <summary>The tabard designer's values (<see cref="GuildRequest.Values"/>: style, colour, border style, border colour, background colour); the leader's worker has checked the designer and the money.</summary>
    SetEmblem = 20,

    /// <summary>A guild member entered the world (<see cref="GuildRequest.A"/> level, <see cref="GuildRequest.B"/> zone): the holder tells the guild.</summary>
    LoggedIn = 21,

    /// <summary>A guild member is leaving the world (level, zone as for <see cref="LoggedIn"/>).</summary>
    LoggedOut = 22,

    /// <summary>The character's pending guild invitation is dropped (it logged out unguilded or moved away).</summary>
    ForgetInvite = 23,

    /// <summary>A GM or character-deletion removal of <see cref="GuildRequest.Actor"/> from its guild.</summary>
    AdminUninvite = 24,

    /// <summary>A charter bought or renamed on another worker: <see cref="GuildRequest.Petition"/> is the whole petition to store and publish.</summary>
    PetitionPut = 30,

    /// <summary>A charter that no longer exists (its item is gone): <see cref="GuildRequest.Id"/> is the petition id.</summary>
    PetitionDelete = 31,

    /// <summary>The actor signs the charter whose item guid is <see cref="GuildRequest.Item"/>.</summary>
    PetitionSign = 32,

    /// <summary>The actor turns in the petition <see cref="GuildRequest.Id"/> (charter item <see cref="GuildRequest.Item"/>); the answer is the completion flag.</summary>
    PetitionTurnIn = 33,

    /// <summary>The character <see cref="GuildRequest.Actor"/> was deleted: its petition and signatures go.</summary>
    PetitionCharacterDeleted = 34,

    /// <summary>The actor offers the charter whose item guid is <see cref="GuildRequest.Item"/> to the character <see cref="GuildRequest.A"/> (a player guid), who is online on another worker; <see cref="GuildRequest.B"/> is 1 when the actor's worker saw the charter in its bags.</summary>
    PetitionOffer = 35,
}

/// <summary>
/// One operation a worker that is not the Social holder asks the holder to run (<see cref="IGuildHolderLink.Forward"/>). The actor is a character guid: the holder
/// resolves name, team and account from the character directory. Immutable.
/// </summary>
/// <param name="Kind">What to run.</param>
/// <param name="Actor">The character the operation is for (it receives the replies).</param>
/// <param name="Text">First text argument (a character name, the MOTD, info text or rank name).</param>
/// <param name="Text2">Second text argument (a note).</param>
/// <param name="A">First number (a rank id, a level).</param>
/// <param name="B">Second number (rank rights, a zone).</param>
/// <param name="Item">A charter item guid value.</param>
/// <param name="Petition">A whole petition (<see cref="GuildRequestKind.PetitionPut"/>).</param>
/// <param name="Id">A petition id.</param>
/// <param name="Values">Emblem values.</param>
public sealed record GuildRequest(
    GuildRequestKind Kind,
    ObjectGuid Actor,
    string Text = "",
    string Text2 = "",
    uint A = 0,
    uint B = 0,
    ulong Item = 0,
    PetitionData? Petition = null,
    int Id = 0,
    IReadOnlyList<int>? Values = null);

/// <summary>A character that is online on another worker, as the realm presence directory knows it (what a roster line and an invitation need).</summary>
public sealed record GuildRemoteCharacter(ObjectGuid Guid, string Name, Team Team, byte Level, byte Class, uint ZoneId, bool Afk, bool Dnd);

/// <summary>
/// How a worker reaches the Social lease holder for guilds and charters (M15.2 S3b). The holder runs every guild and petition rule against its own
/// <see cref="GuildManager"/> and <see cref="PetitionManager"/> and publishes the stored result to every other worker, whose copy is a read replica; a worker that is not
/// the holder forwards what changes state. Implemented by the cluster hosting over the peer link (and in tests by an in-process hub). Every call is made on the calling worker's
/// world thread and must not block; an implementation hands work to the other worker's world thread (<see cref="GuildManager.Serve"/>,
/// <see cref="GuildManager.ApplyReplica"/>, <see cref="PetitionManager.ApplyReplica"/>) and delivers packets to the character's session there.
/// </summary>
public interface IGuildHolderLink
{
    /// <summary>True on the worker that holds the Social lease.</summary>
    bool IsHolder { get; }

    /// <summary>
    /// Ask the holder to run <paramref name="request"/>. False when the holder cannot be reached (nothing was sent). Otherwise the request is queued for the holder's world
    /// thread; <paramref name="completed"/>, when given, later runs on the calling worker's world thread with <see cref="GuildManager.Serve"/>'s result.
    /// </summary>
    bool Forward(GuildRequest request, Action<bool>? completed);

    /// <summary>Holder to worker: send a packet to a character that is online on another worker (dropped when it is online nowhere).</summary>
    void Deliver(ObjectGuid character, WorldOpcode opcode, byte[] payload);

    /// <summary>Holder to every other worker: the stored state of a guild (<paramref name="data"/> null: disbanded).</summary>
    void ReplicateGuild(int guildId, GuildData? data);

    /// <summary>Holder to every other worker: the state of a petition (<paramref name="data"/> null: gone).</summary>
    void ReplicatePetition(int petitionId, PetitionData? data);

    /// <summary>A character online on another worker, by guid, or null.</summary>
    GuildRemoteCharacter? FindOnline(ObjectGuid character);

    /// <summary>A character online on another worker, by normalised name, or null.</summary>
    GuildRemoteCharacter? FindOnline(string name);

    /// <summary>Whether <paramref name="target"/> (online on another worker) has <paramref name="ignorer"/> on its ignore list; false when that is not known.</summary>
    bool TargetIgnores(ObjectGuid target, ObjectGuid ignorer);
}

/// <summary>
/// A character as the guild rules see it: identity and a way to answer it. Wraps a local <see cref="Player"/> (answers go to its session) or, on the holder, a
/// character that is online on another worker (answers go through <see cref="IGuildHolderLink.Deliver"/>). Immutable.
/// </summary>
public sealed class GuildActor(ObjectGuid guid, string name, Team team, int accountId, Action<WorldOpcode, byte[]> send, Action<string>? kick = null)
{
    public ObjectGuid Guid { get; } = guid;

    public string Name { get; } = name;

    public Team Team { get; } = team;

    public int AccountId { get; } = accountId;

    public void Send(WorldOpcode opcode, byte[] payload) => send(opcode, payload);

    /// <summary>Disconnect the character (a no-op for one on another worker, whose own worker has already checked what would kick it).</summary>
    internal void Kick(string reason) => kick?.Invoke(reason);

    public static GuildActor Of(Player player)
    {
        IPlayerSession session = player.Session;
        return new GuildActor(player.Guid, player.Name, player.Team, session.AccountId, (opcode, payload) => session.Send(opcode, payload), reason => session.Kick(reason));
    }
}
