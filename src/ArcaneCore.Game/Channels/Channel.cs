using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Channels;

/// <summary>
/// A chat channel and its rules (vmangos Channel.cpp, citations per method). Members are kept
/// ordered by guid like vmangos' std::map, so "the first remaining member" matches. World thread.
/// </summary>
public sealed class Channel
{
    private readonly SocialContext _context;
    private readonly SortedDictionary<ulong, ChannelMemberFlags> _players = [];
    private readonly HashSet<ObjectGuid> _banned = [];

    /// <summary>vmangos Channel::Channel: built-ins (by name pattern) or custom channels.</summary>
    internal Channel(SocialContext context, string name)
    {
        _context = context;
        BuiltInChannel? builtIn = BuiltInChannels.Find(name);
        if (builtIn is not null)
        {
            ChannelId = builtIn.Id;
            Name = name;
            Flags = builtIn.Flags;
            Announce = false;
            return;
        }

        // normalizePlayerName(m_name, 128): lower case with a capital first letter.
        Name = NormalizeCustomName(name);
        if (Name.Length == 0)
        {
            Name = "INVALIDCHANNEL";
            Announce = false;
        }
        else if (context.Options.VmangosChannelExtensions && Name == "World")
        {
            // vmangos Channel.cpp:63-66, not retail (SocialOptions.VmangosChannelExtensions).
            Flags = ChannelFlags.General;
            Announce = false;
        }
        else if (context.Options.VmangosChannelExtensions && Name is "China" or "中国")
        {
            // vmangos Channel.cpp:67-71; the second spelling is the UTF-8 Mandarin name.
            Flags = ChannelFlags.Custom;
            Announce = false;
        }
        else
        {
            Flags = ChannelFlags.Custom;
            Announce = true;
        }
    }

    public string Name { get; }

    /// <summary>The ChatChannels.dbc id; non-zero only for built-in channels (vmangos IsConstant).</summary>
    public uint ChannelId { get; }

    public bool IsConstant => ChannelId != 0;

    public ChannelFlags Flags { get; }

    public bool Announce { get; private set; }

    public bool Moderate { get; private set; }

    public string Password { get; private set; } = string.Empty;

    public ObjectGuid Owner { get; private set; }

    public int MemberCount => _players.Count;

    public IEnumerable<ObjectGuid> Members => _players.Keys.Select(k => new ObjectGuid(k));

    public bool IsOn(ObjectGuid guid) => _players.ContainsKey(guid.Value);

    public bool IsBanned(ObjectGuid guid) => _banned.Contains(guid);

    public ChannelMemberFlags FlagsOf(ObjectGuid guid) => _players.GetValueOrDefault(guid.Value);

    /// <summary>
    /// vmangos Channel::Join: already a member (silent for built-ins), banned, wrong password;
    /// guilded players may not join GuildRecruitment; JOINED to the members when announcing,
    /// then YOU_JOINED; the first player of a custom channel without owner becomes owner and
    /// moderator.
    /// </summary>
    internal bool Join(Player player, string password)
    {
        if (IsOn(player.Guid))
        {
            if (!IsConstant)
            {
                SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerAlreadyMember, Name, player.Guid));
            }

            return false;
        }

        if (IsBanned(player.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.Banned, Name));
            return false;
        }

        if (Password.Length > 0 && password != Password)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.WrongPassword, Name));
            return false;
        }

        if (Flags == BuiltInChannels.GuildRecruitmentFlags && _context.Guilds.GetGuildOf(player) is not null)
        {
            return false;
        }

        if (Announce)
        {
            SendToAll(ChannelPackets.BuildNotify(ChatNotify.Joined, Name, player.Guid));
        }

        _players[player.Guid.Value] = ChannelMemberFlags.None;
        SendToOne(player, ChannelPackets.BuildYouJoined(Name, Flags));
        if ((Flags & ChannelFlags.Custom) != 0 && !IsConstant && Owner.IsEmpty)
        {
            SetOwner(player.Guid, _players.Count > 1);
            SetFlag(player.Guid, ChannelMemberFlags.Moderator, true);
        }

        return true;
    }

    /// <summary>
    /// vmangos Channel::Leave: YOU_LEFT when <paramref name="send"/>, LEFT to the members when
    /// announcing, and ownership passes to the first remaining member.
    /// </summary>
    internal bool Leave(ObjectGuid guid, bool send)
    {
        Player? player = _context.World.FindOnlinePlayer(guid);
        if (!IsOn(guid))
        {
            if (send && player is not null)
            {
                SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotMember, Name));
            }

            return false;
        }

        if (send && player is not null)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.YouLeft, Name));
        }

        bool changeOwner = (FlagsOf(guid) & ChannelMemberFlags.Owner) != 0;
        _players.Remove(guid.Value);
        if (Announce)
        {
            SendToAll(ChannelPackets.BuildNotify(ChatNotify.Left, Name, guid));
        }

        if (changeOwner)
        {
            SetOwner(_players.Count > 0 ? new ObjectGuid(_players.Keys.First()) : ObjectGuid.Empty, true);
        }

        return true;
    }

    /// <summary>
    /// vmangos Channel::KickOrBan: member and moderator (or GM); target online and a member; only
    /// the owner or a GM may remove the owner; PLAYER_KICKED / PLAYER_BANNED to everyone (the
    /// target included); a removed owner hands ownership to the kicker.
    /// </summary>
    internal Player? KickOrBan(Player player, string targetName, bool ban)
    {
        if (!CheckModerator(player))
        {
            return null;
        }

        Player? target = _context.World.FindOnlinePlayer(targetName);
        if (target is null || !IsOn(target.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerNotFound, Name, targetName));
            return null;
        }

        bool changeOwner = Owner == target.Guid;
        if (!IsGm(player) && changeOwner && player.Guid != Owner)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotOwner, Name));
            return null;
        }

        byte[] packet = ban && _banned.Add(target.Guid)
            ? ChannelPackets.BuildNotify(ChatNotify.PlayerBanned, Name, target.Guid, player.Guid)
            : ChannelPackets.BuildNotify(ChatNotify.PlayerKicked, Name, target.Guid, player.Guid);
        SendToAll(packet);
        _players.Remove(target.Guid.Value);
        if (changeOwner)
        {
            SetOwner(_players.Count > 0 ? player.Guid : ObjectGuid.Empty, true);
        }

        return target;
    }

    /// <summary>vmangos Channel::UnBan.</summary>
    internal void Unban(Player player, string targetName)
    {
        if (!CheckModerator(player))
        {
            return;
        }

        Player? target = _context.World.FindOnlinePlayer(targetName);
        if (target is null)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerNotFound, Name, targetName));
            return;
        }

        if (!_banned.Remove(target.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerNotBanned, Name, targetName));
            return;
        }

        SendToAll(ChannelPackets.BuildNotify(ChatNotify.PlayerUnbanned, Name, target.Guid, player.Guid));
    }

    /// <summary>vmangos Channel::Password: moderator or GM; PASSWORD_CHANGED to everyone.</summary>
    internal void SetPassword(Player player, string password)
    {
        if (!CheckModerator(player))
        {
            return;
        }

        Password = password;
        SendToAll(ChannelPackets.BuildNotify(ChatNotify.PasswordChanged, Name, player.Guid));
    }

    /// <summary>
    /// vmangos Channel::SetMode: moderator or GM; target online, a member and (unless both are
    /// GMs or two-side channels are allowed) of the same faction; only the owner may change the
    /// owner; built-in channels only take GM moderators.
    /// </summary>
    internal void SetMode(Player player, string targetName, bool moderator, bool set)
    {
        if (!CheckModerator(player))
        {
            return;
        }

        Player? target = _context.World.FindOnlinePlayer(targetName);
        if (target is null)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerNotFound, Name, targetName));
            return;
        }

        if (moderator && player.Guid == Owner && target.Guid == Owner)
        {
            return;
        }

        if (!IsOn(target.Guid)
            || ((!IsGm(player) || !IsGm(target)) && player.Team != target.Team && !_context.Options.AllowTwoSideChannel))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerNotFound, Name, targetName));
            return;
        }

        if (Owner == target.Guid && Owner != player.Guid)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotOwner, Name));
            return;
        }

        if (moderator)
        {
            if ((Flags & ChannelFlags.General) != 0 && !IsGm(target))
            {
                return;
            }

            SetFlag(target.Guid, ChannelMemberFlags.Moderator, set);
        }
        else
        {
            SetFlag(target.Guid, ChannelMemberFlags.Muted, set);
        }
    }

    /// <summary>
    /// vmangos Channel::SetOwner(guid, name): the owner or a GM hands ownership to an online
    /// member of the same faction, who also becomes a moderator.
    /// </summary>
    internal void SetOwner(Player player, string targetName)
    {
        if (!IsOn(player.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotMember, Name));
            return;
        }

        if (!IsGm(player) && player.Guid != Owner)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotOwner, Name));
            return;
        }

        Player? target = _context.World.FindOnlinePlayer(targetName);
        if (target is null || !IsOn(target.Guid) || (target.Team != player.Team && !_context.Options.AllowTwoSideChannel))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerNotFound, Name, targetName));
            return;
        }

        if ((Flags & ChannelFlags.General) != 0 && !IsGm(target))
        {
            return;
        }

        _players[target.Guid.Value] |= ChannelMemberFlags.Moderator;
        SetOwner(target.Guid, true);
    }

    /// <summary>vmangos Channel::SendWhoOwner / MakeChannelOwner: "Nobody" for built-ins or without owner.</summary>
    internal void SendWhoOwner(Player player)
    {
        if (!IsOn(player.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotMember, Name));
            return;
        }

        string owner = IsConstant || Owner.IsEmpty
            ? "Nobody"
            : _context.Characters.Find(Owner.Low)?.Name is { Length: > 0 } name ? name : "PLAYER_NOT_FOUND";
        SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.ChannelOwner, Name, owner));
    }

    /// <summary>
    /// vmangos Channel::List: members the viewer may see under the who-list rules (staff see
    /// all; players see staff up to GmLevelInWhoList; IsVisibleGloballyFor hides the other
    /// faction unless two-side who lists are allowed).
    /// </summary>
    internal void List(Player player)
    {
        if (!IsOn(player.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotMember, Name));
            return;
        }

        var visible = new List<(ObjectGuid, ChannelMemberFlags)>(_players.Count);
        foreach ((ulong raw, ChannelMemberFlags flags) in _players)
        {
            if (_context.World.FindOnlinePlayer(new ObjectGuid(raw)) is { } member && _context.CanSeeOnline(player, member))
            {
                visible.Add((member.Guid, flags));
            }
        }

        player.Session.Send(WorldOpcode.SmsgChannelList, ChannelPackets.BuildList(Name, Flags, visible));
    }

    /// <summary>vmangos Channel::Announce: toggle join/leave announcements.</summary>
    internal void ToggleAnnounce(Player player)
    {
        if (!CheckModerator(player))
        {
            return;
        }

        Announce = !Announce;
        SendToAll(ChannelPackets.BuildNotify(Announce ? ChatNotify.AnnouncementsOn : ChatNotify.AnnouncementsOff, Name, player.Guid));
    }

    /// <summary>vmangos Channel::Moderate: toggle moderation.</summary>
    internal void ToggleModerate(Player player)
    {
        if (!CheckModerator(player))
        {
            return;
        }

        Moderate = !Moderate;
        SendToAll(ChannelPackets.BuildNotify(Moderate ? ChatNotify.ModerationOn : ChatNotify.ModerationOff, Name, player.Guid));
    }

    /// <summary>
    /// vmangos Channel::Say: member; not muted (WorldDefense needs internal honor rank 15, see
    /// <see cref="ArcaneCore.Game.Honor.HonorHooks.InternalRank"/>); moderated channels need a moderator or GM; universal language with
    /// two-side channels; members ignoring a non-moderator speaker do not receive it.
    /// <para>
    /// With honor disabled (<c>World:Honor:Enabled</c> false: no rank source is registered) nobody can ever hold a rank, so the WorldDefense
    /// rank gate does not apply and the channel is open like LocalDefense; the message carries rank 0. vmangos has no switch to turn honor
    /// off, so this case does not arise there (deliberate deviation, docs/areas/battlegrounds.md).
    /// </para>
    /// </summary>
    internal void Say(Player player, string text, Language language)
    {
        // GetHonorMgr().GetRank().rank (Channel.cpp:636-648, 670); null while honor is disabled.
        byte? rankSource = ArcaneCore.Game.Honor.HonorHooks.For(_context.World).InternalRank?.Invoke(player);
        byte honorRank = rankSource ?? 0;
        if (!IsOn(player.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotMember, Name));
            return;
        }

        ChannelMemberFlags flags = FlagsOf(player.Guid);
        if ((flags & ChannelMemberFlags.Muted) != 0 || (ChannelId == BuiltInChannels.WorldDefenseId && rankSource is { } rank && rank < BuiltInChannels.WorldDefenseSpeakRank))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.Muted, Name));
            return;
        }

        bool isModerator = (flags & ChannelMemberFlags.Moderator) != 0;
        if (Moderate && !isModerator && !IsGm(player))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotModerator, Name));
            return;
        }

        if (_context.Options.AllowTwoSideChannel)
        {
            language = Language.Universal;
        }

        byte[] packet = ChannelPackets.BuildChannelMessage(Name, language, player.Guid, text, player.ChatTag, honorRank);
        SendToAll(packet, isModerator ? ObjectGuid.Empty : player.Guid, WorldOpcode.SmsgMessagechat);
    }

    /// <summary>
    /// vmangos Channel::Invite: the inviter is a member; the target is online, not a member,
    /// not banned and of the same faction; the target gets INVITE unless it ignores the
    /// inviter; the inviter always gets PLAYER_INVITED.
    /// </summary>
    internal void Invite(Player player, string targetName)
    {
        if (!IsOn(player.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotMember, Name));
            return;
        }

        Player? target = _context.World.FindOnlinePlayer(targetName);
        if (target is null)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerNotFound, Name, targetName));
            return;
        }

        if (IsOn(target.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerAlreadyMember, Name, target.Guid));
            return;
        }

        if (IsBanned(target.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerInviteBanned, Name, targetName));
            return;
        }

        if (target.Team != player.Team && !_context.Options.AllowTwoSideChannel)
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.InviteWrongFaction, Name));
            return;
        }

        if (!_context.IsIgnoring(target, player.Guid))
        {
            SendToOne(target, ChannelPackets.BuildNotify(ChatNotify.Invite, Name, player.Guid));
        }

        SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.PlayerInvited, Name, target.Name));
    }

    /// <summary>
    /// vmangos Channel::SetOwner(guid, exclaim): built-in channels only take GM owners; the old
    /// owner loses the flag; MODE_CHANGE (and OWNER_CHANGED when exclaiming) to everyone.
    /// </summary>
    private void SetOwner(ObjectGuid guid, bool exclaim)
    {
        Player? player = guid.IsEmpty ? null : _context.World.FindOnlinePlayer(guid);
        if (!guid.IsEmpty && player is null)
        {
            return;
        }

        if (player is not null && (Flags & ChannelFlags.General) != 0 && !IsGm(player))
        {
            return;
        }

        if (!Owner.IsEmpty && _players.ContainsKey(Owner.Value))
        {
            _players[Owner.Value] &= ~ChannelMemberFlags.Owner;
        }

        Owner = guid;
        if (!Owner.IsEmpty)
        {
            ChannelMemberFlags old = FlagsOf(Owner);
            _players[Owner.Value] = old | ChannelMemberFlags.Owner;
            SendToAll(ChannelPackets.BuildModeChange(Name, Owner, old, _players[Owner.Value]));
            if (exclaim)
            {
                SendToAll(ChannelPackets.BuildNotify(ChatNotify.OwnerChanged, Name, Owner));
            }
        }
    }

    /// <summary>vmangos Channel::SetModerator / SetMute (Channel.h): MODE_CHANGE to everyone when the flag changes.</summary>
    private void SetFlag(ObjectGuid guid, ChannelMemberFlags flag, bool set)
    {
        ChannelMemberFlags old = FlagsOf(guid);
        if (((old & flag) != 0) == set)
        {
            return;
        }

        ChannelMemberFlags now = set ? old | flag : old & ~flag;
        _players[guid.Value] = now;
        SendToAll(ChannelPackets.BuildModeChange(Name, guid, old, now));
    }

    private bool CheckModerator(Player player)
    {
        if (!IsOn(player.Guid))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotMember, Name));
            return false;
        }

        if ((FlagsOf(player.Guid) & ChannelMemberFlags.Moderator) == 0 && !IsGm(player))
        {
            SendToOne(player, ChannelPackets.BuildNotify(ChatNotify.NotModerator, Name));
            return false;
        }

        return true;
    }

    private static bool IsGm(Player player) => player.Security >= AccountSecurity.GameMaster;

    private static void SendToOne(Player player, byte[] packet) => player.Session.Send(WorldOpcode.SmsgChannelNotify, packet);

    /// <summary>vmangos Channel::SendToAll: every online member except those ignoring <paramref name="except"/>.</summary>
    private void SendToAll(byte[] packet, ObjectGuid except = default, WorldOpcode opcode = WorldOpcode.SmsgChannelNotify)
    {
        foreach (ulong raw in _players.Keys.ToList())
        {
            if (_context.World.FindOnlinePlayer(new ObjectGuid(raw)) is { } member && (except.IsEmpty || !_context.IsIgnoring(member, except)))
            {
                member.Session.Send(opcode, packet);
            }
        }
    }

    /// <summary>
    /// vmangos normalizePlayerName(m_name, 128) only changes case; it does not trim. ChannelManager keys the channel on
    /// the lowered request text, so trimming here would announce a name ("Foo") that no longer finds the channel ("foo ").
    /// </summary>
    private static string NormalizeCustomName(string name)
    {
        if (name.Length == 0 || name.Length > 128)
        {
            return string.Empty;
        }

        string lower = name.ToLowerInvariant();
        return char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}
