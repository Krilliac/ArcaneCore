using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Diagnostics;
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

/// <summary>What a worker knows of a friend who is online on another worker (from realm presence): the fields of SMSG_FRIEND_STATUS plus the faction for the visibility check.</summary>
public readonly record struct RemoteFriendInfo(FriendStatus Status, uint Area, uint Level, uint Class, Team Team);

/// <summary>
/// A real online/offline transition of a watched friend who lives on another worker. <see cref="Info"/> is the current presence when
/// <see cref="Online"/>, and the last cached presence (used for the visibility rule) when not. <see cref="Announce"/> is false for a friend found online the first
/// time she is watched (the lister's friend list was already sent from an empty cache): the listers get the corrected friend list, without the "has come online" line.
/// </summary>
public readonly record struct RemotePresenceChange(ulong Guid, bool Online, RemoteFriendInfo Info, bool Announce = true);

/// <summary>
/// Non-blocking read of the characters online on other workers (a cache of realm presence). Implementations must never block the world thread:
/// <see cref="TryGet"/> answers from memory.
/// </summary>
public interface IRemoteFriendPresence
{
    /// <summary>True with the friend's last known presence when the realm directory lists the character online.</summary>
    bool TryGet(ObjectGuid friend, out RemoteFriendInfo info);
}

/// <summary>One online character's friend and ignore list (vmangos PlayerSocial).</summary>
public sealed class PlayerSocial
{
    /// <summary>vmangos SOCIALMGR_FRIEND_LIMIT.</summary>
    public const int FriendLimit = 50;

    /// <summary>vmangos SOCIALMGR_IGNORE_LIMIT ("checked max for 1.12.1").</summary>
    public const int IgnoreLimit = 25;

    private readonly Dictionary<uint, SocialFlags> _entries = [];

    /// <summary>
    /// The changes made before the player's rows arrived (a list made by <see cref="FriendsService.Get"/> before <see cref="FriendsService.Load"/>),
    /// per target: the flags set and the flags cleared. The load applies them on top of the stored row instead of dropping them.
    /// </summary>
    private Dictionary<uint, (SocialFlags Set, SocialFlags Cleared)>? _pending;

    /// <summary>Whether the stored rows are in (a list that is not yet loaded keeps its changes in <see cref="_pending"/>).</summary>
    internal bool Loaded { get; init; }

    internal IReadOnlyDictionary<uint, (SocialFlags Set, SocialFlags Cleared)>? Pending => _pending;

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

        if (!Loaded)
        {
            (SocialFlags set, SocialFlags cleared) = (_pending ??= []).GetValueOrDefault(other);
            _pending[other] = on ? (set | flag, cleared & ~flag) : (set & ~flag, cleared | flag);
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

    /// <summary>
    /// Presence of friends who are online on another worker, from the realm presence directory; null on a single-worker server. A friend found
    /// online in this process always wins over it. World thread.
    /// </summary>
    public IRemoteFriendPresence? Remote { get; set; }

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

    public bool IsLoaded(Player player) => _socials.TryGetValue(player.Guid, out PlayerSocial? social) && social.Loaded;

    /// <summary>Install a player's rows (entries of deleted characters are skipped).</summary>
    public void Load(Player player, IEnumerable<SocialEntry> entries)
    {
        var social = new PlayerSocial { Loaded = true };
        foreach (SocialEntry entry in entries)
        {
            if (entry.OtherId > 0 && context.Characters.Find((uint)entry.OtherId) is not null)
            {
                social.Set((uint)entry.OtherId, entry.Flags & (SocialFlags.Friend | SocialFlags.Ignored), true);
            }
        }

        // Changes made on the empty list before the rows arrived are deltas over the stored rows: flags set are added, flags cleared are
        // removed (a stored Friend plus a pending Ignore is Friend|Ignored). Additions beyond the list limits are dropped. What was written
        // before the load were absolute flags of the empty list, so every touched target is written again with the merged flags.
        if (_socials.TryGetValue(player.Guid, out PlayerSocial? early) && early.Pending is { } pending)
        {
            SocialFlags[] kinds = [SocialFlags.Friend, SocialFlags.Ignored];
            uint[] targets = [.. pending.Keys.Order()];

            // Clears first, so a removal that frees a slot runs before the addition that needs it.
            foreach (uint other in targets)
            {
                foreach (SocialFlags flag in kinds)
                {
                    if ((pending[other].Cleared & flag) != 0) social.Set(other, flag, false);
                }
            }

            foreach (uint other in targets)
            {
                SocialFlags set = pending[other].Set;
                foreach (SocialFlags flag in kinds)
                {
                    if ((set & flag) == 0 || social.Has(other, flag)) continue;
                    if (context.Characters.Find(other) is null) continue; // a deleted character takes no slot
                    int limit = flag == SocialFlags.Friend ? PlayerSocial.FriendLimit : PlayerSocial.IgnoreLimit;
                    if (social.Count(flag) >= limit)
                    {
                        AnomalyLedger.Shared.Report("social", "pending-over-limit", player.Name,
                            $"{flag} of character {other} made before the list loaded is dropped: the list holds {limit}", AnomalySeverity.Info);
                        continue;
                    }

                    social.Set(other, flag, true);
                }
            }

            foreach (uint other in targets)
            {
                Persist(player, other, social.Entries.GetValueOrDefault(other));
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
            bool visible = online is not null ? context.CanSeeOnline(player, online) : RemoteVisible(player, target.Guid) is not null;
            result = visible ? FriendsResult.AddedOnline : FriendsResult.AddedOffline;
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
        if (friend is not null)
        {
            return context.CanSeeOnline(player, friend)
                ? (PresenceOf(friend), friend.ZoneId, friend.Level, (uint)friend.Class)
                : (FriendStatus.Offline, 0, 0, 0);
        }

        return RemoteVisible(player, ObjectGuid.Player(other)) is { } remote
            ? (remote.Status, remote.Area, remote.Level, remote.Class)
            : (FriendStatus.Offline, 0, 0, 0);
    }

    /// <summary>
    /// The presence of a friend who is online on another worker, when <paramref name="viewer"/> may see it (the faction rule of
    /// <see cref="SocialContext.CanSeeOnline"/>; realm presence carries no GM level, so a remote staff member counts as a plain player).
    /// </summary>
    private RemoteFriendInfo? RemoteVisible(Player viewer, ObjectGuid friend)
        => Remote is { } remote && remote.TryGet(friend, out RemoteFriendInfo info) && CanSeeRemote(viewer, info) ? info : null;

    private bool CanSeeRemote(Player viewer, RemoteFriendInfo info)
        => viewer.Security > AccountSecurity.Player || info.Team == viewer.Team || context.World.Options.AllowTwoSideWhoList;

    /// <summary>
    /// Guids of the listed friends of every loaded list who are not online in this process: the characters to look up in realm presence.
    /// World thread.
    /// </summary>
    public ulong[] RemoteFriendCandidates()
    {
        var wanted = new HashSet<ulong>();
        foreach (PlayerSocial social in _socials.Values)
        {
            foreach ((uint other, SocialFlags flags) in social.Entries)
            {
                if ((flags & SocialFlags.Friend) != 0 && context.World.FindOnlinePlayer(ObjectGuid.Player(other)) is null)
                {
                    wanted.Add(other);
                }
            }
        }

        return [.. wanted];
    }

    /// <summary>
    /// Applies one batch of <see cref="RemotePresenceChange"/>. An announced change (a watched friend on another worker really came online or went offline) sends
    /// FRIEND_ONLINE / FRIEND_OFFLINE like <see cref="BroadcastPresence"/> to every online lister here who lists her and may see it (the faction rule of
    /// <see cref="SocialContext.CanSeeOnline"/>, applied to the last known presence for an offline). A silent change (a friend found online when first watched,
    /// after the lister's list or add answer was built from an empty cache) resends SMSG_FRIEND_LIST once to each such lister, so the client shows her online
    /// without a "has come online" line. A friend online in this process is skipped, its own <see cref="BroadcastPresence"/> covers it. World thread.
    /// </summary>
    public void NotifyRemotePresence(IReadOnlyList<RemotePresenceChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        HashSet<ObjectGuid>? resend = null;
        foreach (RemotePresenceChange change in changes)
        {
            if (change.Guid is 0 or > uint.MaxValue)
            {
                continue;
            }

            ObjectGuid friend = ObjectGuid.Player((uint)change.Guid);
            if (context.World.FindOnlinePlayer(friend) is not null)
            {
                continue;
            }

            RemoteFriendInfo info = change.Info;
            byte[]? packet = null;
            if (change.Announce)
            {
                packet = change.Online
                    ? SocialPackets.BuildFriendStatus(FriendsResult.Online, friend, info.Status, info.Area, info.Level, info.Class)
                    : SocialPackets.BuildFriendStatus(FriendsResult.Offline, friend, FriendStatus.Offline, 0, 0, 0);
            }

            foreach ((ObjectGuid listerGuid, PlayerSocial social) in _socials)
            {
                if (!social.Has(friend.Low, SocialFlags.Friend)
                    || context.World.FindOnlinePlayer(listerGuid) is not { } lister
                    || !CanSeeRemote(lister, info))
                {
                    continue;
                }

                if (packet is not null)
                {
                    lister.Session.Send(WorldOpcode.SmsgFriendStatus, packet);
                }
                else
                {
                    (resend ??= new HashSet<ObjectGuid>()).Add(listerGuid);
                }
            }
        }

        if (resend is null)
        {
            return;
        }

        foreach (ObjectGuid listerGuid in resend)
        {
            if (context.World.FindOnlinePlayer(listerGuid) is { } lister)
            {
                SendFriendList(lister);
            }
        }
    }

    /// <summary>One change; see <see cref="NotifyRemotePresence(IReadOnlyList{RemotePresenceChange})"/>.</summary>
    public void NotifyRemotePresence(RemotePresenceChange change) => NotifyRemotePresence([change]);

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
