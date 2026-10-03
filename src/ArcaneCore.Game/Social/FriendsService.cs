using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Social;

/// <summary>SMSG_FRIEND_STATUS results (vmangos SocialMgr.h FriendsResult; gtker FriendResult agrees).</summary>
public enum FriendsResult : byte
{
    DbError = 0x00,
    ListFull = 0x01,
    Online = 0x02,
    Offline = 0x03,
    NotFound = 0x04,
    Removed = 0x05,
    AddedOnline = 0x06,
    AddedOffline = 0x07,
    Already = 0x08,
    Self = 0x09,
    Enemy = 0x0A,
    IgnoreFull = 0x0B,
    IgnoreSelf = 0x0C,
    IgnoreNotFound = 0x0D,
    IgnoreAlready = 0x0E,
    IgnoreAdded = 0x0F,
    IgnoreRemoved = 0x10,
    IgnoreAmbiguous = 0x11,
}

/// <summary>A friend's presence in SMSG_FRIEND_LIST / SMSG_FRIEND_STATUS (vmangos SocialMgr.h FriendStatus).</summary>
public enum FriendStatus : byte
{
    Offline = 0x00,
    Online = 0x01,
    Afk = 0x02,
    Dnd = 0x04,
}

/// <summary>One online character's friend and ignore list (vmangos PlayerSocial).</summary>
public sealed class PlayerSocial
{
    /// <summary>vmangos SOCIALMGR_FRIEND_LIMIT.</summary>
    public const int FriendLimit = 50;

    /// <summary>vmangos SOCIALMGR_IGNORE_LIMIT ("checked max for 1.12.1").</summary>
    public const int IgnoreLimit = 25;

    private readonly Dictionary<uint, SocialFlags> _entries = [];

    public IReadOnlyDictionary<uint, SocialFlags> Entries => _entries;

    public int Count(SocialFlags flag) => _entries.Values.Count(f => (f & flag) != 0);

    public bool Has(uint other, SocialFlags flag) => _entries.TryGetValue(other, out SocialFlags f) && (f & flag) != 0;

    /// <summary>Set or clear one flag; returns the entry's new flags.</summary>
    internal SocialFlags Set(uint other, SocialFlags flag, bool on)
    {
        SocialFlags flags = _entries.GetValueOrDefault(other);
        flags = on ? flags | flag : flags & ~flag;
        if (flags == SocialFlags.None)
        {
            _entries.Remove(other);
        }
        else
        {
            _entries[other] = flags;
        }

        return flags;
    }
}

/// <summary>
/// Friend and ignore lists (vmangos SocialMgr.cpp and MiscHandler.cpp HandleAdd/DelFriend/
/// Ignore). Lists of online characters are held here; changes are written through
/// <see cref="ISocialPersistence"/>. World thread.
/// </summary>
public sealed class FriendsService(SocialContext context)
{
    private readonly Dictionary<ObjectGuid, PlayerSocial> _socials = [];

    /// <summary>The loaded list of an online player (empty until its rows arrive).</summary>
    public PlayerSocial Get(Player player)
    {
        if (!_socials.TryGetValue(player.Guid, out PlayerSocial? social))
        {
            social = new PlayerSocial();
            _socials[player.Guid] = social;
        }

        return social;
    }

    public bool IsLoaded(Player player) => _socials.ContainsKey(player.Guid);

    /// <summary>Install a player's rows (entries of deleted characters are skipped).</summary>
    public void Load(Player player, IEnumerable<SocialEntry> entries)
    {
        var social = new PlayerSocial();
        foreach (SocialEntry entry in entries)
        {
            if (entry.OtherId > 0 && context.Characters.Find((uint)entry.OtherId) is not null)
            {
                social.Set((uint)entry.OtherId, entry.Flags & (SocialFlags.Friend | SocialFlags.Ignored), true);
            }
        }

        _socials[player.Guid] = social;
    }

    public void Unload(Player player) => _socials.Remove(player.Guid);

    public bool HasIgnore(Player ignorer, ObjectGuid target)
        => target.IsPlayer && _socials.TryGetValue(ignorer.Guid, out PlayerSocial? s) && s.Has(target.Low, SocialFlags.Ignored);

    /// <summary>SMSG_FRIEND_LIST (vmangos PlayerSocial::SendFriendList).</summary>
    public void SendFriendList(Player player)
    {
        PlayerSocial social = Get(player);
        var friends = new List<(ObjectGuid, FriendStatus, uint, uint, uint)>();
        foreach ((uint other, SocialFlags flags) in social.Entries)
        {
            if ((flags & SocialFlags.Friend) == 0)
            {
                continue;
            }

            (FriendStatus status, uint area, uint level, uint cls) = GetFriendInfo(player, other);
            friends.Add((ObjectGuid.Player(other), status, area, level, cls));
        }

        player.Session.Send(WorldOpcode.SmsgFriendList, SocialPackets.BuildFriendList(friends));
    }

    /// <summary>SMSG_IGNORE_LIST (vmangos PlayerSocial::SendIgnoreList).</summary>
    public void SendIgnoreList(Player player)
    {
        IEnumerable<ObjectGuid> ignored = Get(player).Entries
            .Where(e => (e.Value & SocialFlags.Ignored) != 0)
            .Select(e => ObjectGuid.Player(e.Key));
        player.Session.Send(WorldOpcode.SmsgIgnoreList, SocialPackets.BuildIgnoreList([.. ignored]));
    }

    /// <summary>
    /// CMSG_ADD_FRIEND (vmangos HandleAddFriendOpcode): self, wrong faction (without
    /// AllowTwoSide.AddFriend, for players), already listed, list full; otherwise added with
    /// the friend's presence. An unknown name gets no answer in vmangos; cmangos answers
    /// FRIEND_NOT_FOUND, which the client shows — we follow cmangos there (docs/areas/social.md).
    /// </summary>
    public void AddFriend(Player player, string name)
    {
        CharacterInfo? target = name.Length == 0 ? null : context.Characters.FindByName(name);
        if (target is null)
        {
            SendStatus(player, FriendsResult.NotFound, ObjectGuid.Empty);
            return;
        }

        PlayerSocial social = Get(player);
        FriendsResult result;
        if (target.Id == player.Guid.Low)
        {
            result = FriendsResult.Self;
        }
        else if (player.Team != target.Team && !context.Options.AllowTwoSideAddFriend && player.Security < AccountSecurity.Moderator)
        {
            result = FriendsResult.Enemy;
        }
        else if (social.Has(target.Id, SocialFlags.Friend))
        {
            result = FriendsResult.Already;
        }
        else if (social.Count(SocialFlags.Friend) >= PlayerSocial.FriendLimit)
        {
            result = FriendsResult.ListFull;
        }
        else
        {
            Player? online = context.World.FindOnlinePlayer(target.Guid);
            result = online is not null && context.CanSeeOnline(player, online) ? FriendsResult.AddedOnline : FriendsResult.AddedOffline;
            Persist(player, target.Id, social.Set(target.Id, SocialFlags.Friend, true));
        }

        SendStatus(player, result, target.Guid);
    }

    /// <summary>CMSG_DEL_FRIEND (vmangos HandleDelFriendOpcode → FRIEND_REMOVED).</summary>
    public void RemoveFriend(Player player, ObjectGuid friend)
    {
        if (!friend.IsPlayer)
        {
            return;
        }

        PlayerSocial social = Get(player);
        if (social.Has(friend.Low, SocialFlags.Friend))
        {
            Persist(player, friend.Low, social.Set(friend.Low, SocialFlags.Friend, false));
        }

        SendStatus(player, FriendsResult.Removed, friend);
    }

    /// <summary>CMSG_ADD_IGNORE (vmangos HandleAddIgnoreOpcode): self, already, full, added.</summary>
    public void AddIgnore(Player player, string name)
    {
        CharacterInfo? target = name.Length == 0 ? null : context.Characters.FindByName(name);
        if (target is null)
        {
            SendStatus(player, FriendsResult.IgnoreNotFound, ObjectGuid.Empty);
            return;
        }

        PlayerSocial social = Get(player);
        FriendsResult result;
        if (target.Id == player.Guid.Low)
        {
            result = FriendsResult.IgnoreSelf;
        }
        else if (social.Has(target.Id, SocialFlags.Ignored))
        {
            result = FriendsResult.IgnoreAlready;
        }
        else if (social.Count(SocialFlags.Ignored) >= PlayerSocial.IgnoreLimit)
        {
            result = FriendsResult.IgnoreFull;
        }
        else
        {
            result = FriendsResult.IgnoreAdded;
            Persist(player, target.Id, social.Set(target.Id, SocialFlags.Ignored, true));
        }

        SendStatus(player, result, target.Guid);
    }

    /// <summary>CMSG_DEL_IGNORE (vmangos HandleDelIgnoreOpcode → FRIEND_IGNORE_REMOVED).</summary>
    public void RemoveIgnore(Player player, ObjectGuid ignored)
    {
        if (!ignored.IsPlayer)
        {
            return;
        }

        PlayerSocial social = Get(player);
        if (social.Has(ignored.Low, SocialFlags.Ignored))
        {
            Persist(player, ignored.Low, social.Set(ignored.Low, SocialFlags.Ignored, false));
        }

        SendStatus(player, FriendsResult.IgnoreRemoved, ignored);
    }

    /// <summary>
    /// FRIEND_ONLINE / FRIEND_OFFLINE to every online character that lists
    /// <paramref name="player"/> as a friend and may see it (vmangos
    /// SocialMgr::SendFriendStatus(broadcast) → BroadcastToFriendListers).
    /// </summary>
    public void BroadcastPresence(Player player, bool online)
    {
        byte[] packet = online
            ? SocialPackets.BuildFriendStatus(FriendsResult.Online, player.Guid, PresenceOf(player), player.ZoneId, player.Level, (uint)player.Class)
            : SocialPackets.BuildFriendStatus(FriendsResult.Offline, player.Guid, FriendStatus.Offline, 0, 0, 0);
        foreach ((ObjectGuid listerGuid, PlayerSocial social) in _socials)
        {
            if (listerGuid == player.Guid || !social.Has(player.Guid.Low, SocialFlags.Friend))
            {
                continue;
            }

            if (context.World.FindOnlinePlayer(listerGuid) is { } lister && context.CanSeeOnline(lister, player))
            {
                lister.Session.Send(WorldOpcode.SmsgFriendStatus, packet);
            }
        }
    }

    public static FriendStatus PresenceOf(Player player)
        => player.IsDnd ? FriendStatus.Dnd : player.IsAfk ? FriendStatus.Afk : FriendStatus.Online;

    /// <summary>vmangos SocialMgr::GetFriendInfo for a listed friend.</summary>
    private (FriendStatus Status, uint Area, uint Level, uint Class) GetFriendInfo(Player player, uint other)
    {
        Player? friend = context.World.FindOnlinePlayer(ObjectGuid.Player(other));
        return friend is not null && context.CanSeeOnline(player, friend)
            ? (PresenceOf(friend), friend.ZoneId, friend.Level, (uint)friend.Class)
            : (FriendStatus.Offline, 0, 0, 0);
    }

    private void SendStatus(Player player, FriendsResult result, ObjectGuid friend)
    {
        if (result is FriendsResult.AddedOnline or FriendsResult.Online)
        {
            (FriendStatus status, uint area, uint level, uint cls) = GetFriendInfo(player, friend.Low);
            player.Session.Send(WorldOpcode.SmsgFriendStatus, SocialPackets.BuildFriendStatus(result, friend, status, area, level, cls));
        }
        else
        {
            player.Session.Send(WorldOpcode.SmsgFriendStatus, SocialPackets.BuildFriendStatus(result, friend, FriendStatus.Offline, 0, 0, 0));
        }
    }

    /// <summary>
    /// Queue the row write. A refusal (too many writes waiting for storage for this character or realm,
    /// which a normal player cannot reach) disconnects the session; the change is not persisted.
    /// </summary>
    private void Persist(Player player, uint other, SocialFlags flags)
    {
        if (!context.Persistence.TrySetSocial((int)player.Guid.Low, (int)other, flags))
        {
            player.Session.Kick();
        }
    }
}
