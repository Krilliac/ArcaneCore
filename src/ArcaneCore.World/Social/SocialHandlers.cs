using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Cluster.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Social;

/// <summary>Friend and ignore lists (vmangos MiscHandler.cpp HandleFriendListOpcode … HandleDelIgnoreOpcode, HandleChatIgnoredOpcode).</summary>
public sealed class SocialHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgFriendList, HandleFriendList);
        table.OnWorld(WorldOpcode.CmsgAddFriend, HandleAddFriend);
        table.OnWorld(WorldOpcode.CmsgDelFriend, HandleDelFriend);
        table.OnWorld(WorldOpcode.CmsgAddIgnore, HandleAddIgnore);
        table.OnWorld(WorldOpcode.CmsgDelIgnore, HandleDelIgnore);
        table.OnWorld(WorldOpcode.CmsgChatIgnored, HandleChatIgnored);
    }

    internal static SocialContext Social(WorldSession session) => session.Services.GetRequiredService<SocialFeature>().Context;

    private static SocialFeature Feature(WorldSession session) => session.Services.GetRequiredService<SocialFeature>();

    /// <summary>CMSG_FRIEND_LIST (empty): the friend and ignore lists (vmangos HandleFriendListOpcode → SendSocialList).</summary>
    private static void HandleFriendList(WorldSession session, Player player, byte[] payload)
    {
        Feature(session).ExecuteWhenReady(player, social =>
        {
            social.Friends.SendFriendList(player);
            social.Friends.SendIgnoreList(player);
        });
    }

    /// <summary>CMSG_ADD_FRIEND: CString name (vmangos HandleAddFriendOpcode).</summary>
    private static void HandleAddFriend(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        string name = CharacterNames.Normalize(reader.ReadCString());
        Feature(session).ExecuteWhenReady(player, social => social.Friends.AddFriend(player, name));
    }

    /// <summary>CMSG_DEL_FRIEND: u64 guid (vmangos HandleDelFriendOpcode).</summary>
    private static void HandleDelFriend(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var friend = new ObjectGuid(reader.ReadUInt64());
        Feature(session).ExecuteWhenReady(player, social => social.Friends.RemoveFriend(player, friend));
    }

    /// <summary>CMSG_ADD_IGNORE: CString name (vmangos HandleAddIgnoreOpcode).</summary>
    private static void HandleAddIgnore(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        string name = CharacterNames.Normalize(reader.ReadCString());
        Feature(session).ExecuteWhenReady(player, social => social.Friends.AddIgnore(player, name));
    }

    /// <summary>CMSG_DEL_IGNORE: u64 guid (vmangos HandleDelIgnoreOpcode).</summary>
    private static void HandleDelIgnore(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        var ignored = new ObjectGuid(reader.ReadUInt64());
        Feature(session).ExecuteWhenReady(player, social => social.Friends.RemoveIgnore(player, ignored));
    }

    /// <summary>
    /// CMSG_CHAT_IGNORED: u64 guid of a player whose message the client dropped because it is
    /// ignored; that player gets CHAT_MSG_IGNORED with the ignorer's name (vmangos
    /// HandleChatIgnoredOpcode). The client filters ignored players' chat itself.
    /// </summary>
    private static void HandleChatIgnored(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        if (session.World.FindOnlinePlayer(new ObjectGuid(reader.ReadUInt64())) is { } other)
        {
            other.Session.Send(WorldOpcode.SmsgMessagechat,
                ChatPackets.BuildMessage(ChatType.Ignored, Language.Universal, player.Guid, player.Name, ChatTag.None));
        }
    }
}

/// <summary>What <see cref="CrossWorkerSocial.DeliverWhisper"/> did with a whisper routed to this worker.</summary>
public enum RoutedWhisperOutcome
{
    /// <summary>The recipient is online here and got the whisper.</summary>
    Delivered = 0,

    /// <summary>The recipient is not online on this worker (or the delivery is not a whisper).</summary>
    RecipientNotHere = 1,

    /// <summary>The recipient ignores the sender: refused here, on the recipient's worker, and nothing was sent to the recipient.</summary>
    Ignored = 2,
}

/// <summary>
/// Friends and ignore across workers (S3c). Friend status of characters online on another worker comes from the realm presence directory instead of the local
/// session table, and a whisper routed to this worker is checked against the recipient's ignore list here, where the list lives.
/// <para>
/// Ownership: one instance per <see cref="SocialFeature"/>. <see cref="TryGet"/> is thread-safe and non-blocking (reads one immutable dictionary through a volatile
/// read). <see cref="Tick"/>, <see cref="DeliverWhisper"/> and everything touching <see cref="SocialContext"/> run on the world thread. The directory reads
/// (<see cref="IRealmPresenceDirectory.FindByGuid"/>) may block on the peer link, so they only ever run on the thread pool inside <see cref="RefreshAsync"/>.
/// </para>
/// <para>Memory: the cache holds at most one entry per distinct listed friend that is online on another worker, rebuilt every refresh; one refresh runs at a time.</para>
/// </summary>
public sealed class CrossWorkerSocial(IRealmPresenceDirectory directory, ILogger? logger = null) : IRemoteFriendPresence
{
    private Dictionary<ulong, RemoteFriendInfo> _view = [];
    private int _busy;

    /// <inheritdoc />
    public bool TryGet(ObjectGuid friend, out RemoteFriendInfo info)
        => Volatile.Read(ref _view).TryGetValue(friend.Low, out info);

    /// <summary>
    /// Reads <paramref name="wanted"/> from the directory, replaces the cache and returns the characters whose presence changed (came online, went offline, moved
    /// zone, changed level, class or away status). Runs on the thread pool because the directory may block on the peer link; never call the directory on the world
    /// thread. A directory failure propagates and leaves the previous cache in place.
    /// </summary>
    public Task<IReadOnlyList<ulong>> RefreshAsync(IReadOnlyCollection<ulong> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        return Task.Run(() =>
        {
            var next = new Dictionary<ulong, RemoteFriendInfo>(wanted.Count);
            foreach (ulong guid in wanted)
            {
                if (directory.FindByGuid(guid) is { } entry)
                {
                    next[guid] = new RemoteFriendInfo(
                        entry.IsDnd ? FriendStatus.Dnd : entry.IsAfk ? FriendStatus.Afk : FriendStatus.Online,
                        entry.ZoneId, entry.Level, entry.Class, TeamRules.TeamOf((Race)entry.Race));
                }
            }

            Dictionary<ulong, RemoteFriendInfo> previous = Volatile.Read(ref _view);
            var changed = new List<ulong>();
            foreach ((ulong guid, RemoteFriendInfo info) in next)
            {
                if (!previous.TryGetValue(guid, out RemoteFriendInfo old) || old != info)
                {
                    changed.Add(guid);
                }
            }

            foreach (ulong guid in previous.Keys)
            {
                if (!next.ContainsKey(guid))
                {
                    changed.Add(guid);
                }
            }

            Volatile.Write(ref _view, next);
            return (IReadOnlyList<ulong>)changed;
        });
    }

    /// <summary>
    /// One pass (world thread, from the feature's periodic timer): starts a refresh unless one is still running, then tells the listers of every friend whose presence
    /// changed (<see cref="FriendsService.NotifyRemotePresence"/>) back on the world thread. A failing directory is logged and retried on the next pass.
    /// </summary>
    public void Tick(WorldRuntime world, SocialContext context)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return;
        }

        ulong[] wanted = context.Friends.RemoteFriendCandidates();
        _ = Task.Run(async () =>
        {
            IReadOnlyList<ulong> changed = [];
            try
            {
                changed = await RefreshAsync(wanted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger?.LogFault(LogLevel.Warning, "social.cross-worker-friends", ex, "Reading friend presence from the realm directory failed; retrying on the next pass.");
            }

            try
            {
                world.Post(() =>
                {
                    Volatile.Write(ref _busy, 0);
                    foreach (ulong guid in changed)
                    {
                        context.Friends.NotifyRemotePresence(ObjectGuid.Player((uint)guid));
                    }
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Volatile.Write(ref _busy, 0); // the world is gone: nothing to notify
                logger?.LogFault(LogLevel.Debug, "social.cross-worker-friends-post", ex, "Posting friend presence changes to the world failed.");
            }
        });
    }

    /// <summary>
    /// A whisper the realm router addressed to a character of this worker (<see cref="RoutedSocialDelivery"/>, channel Whisper). The recipient's own ignore list decides:
    /// an ignored sender is refused here and the recipient is sent nothing. Otherwise the recipient gets CHAT_MSG_WHISPER in Universal like a local whisper.
    /// (The 1.12 client already drops ignored players' chat and answers CMSG_CHAT_IGNORED; vmangos also enforces ignore on the server for channel chat,
    /// Channel.cpp:730 and :782. Doing it on the recipient's worker keeps it true for clients that do not filter.) World thread.
    /// </summary>
    public static RoutedWhisperOutcome DeliverWhisper(WorldRuntime world, SocialContext context, RoutedSocialDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(delivery);
        if (delivery.Channel != SocialRouteChannel.Whisper
            || delivery.RecipientGuid is 0 or > uint.MaxValue
            || delivery.SenderGuid is 0 or > uint.MaxValue
            || world.FindOnlinePlayer(ObjectGuid.Player((uint)delivery.RecipientGuid)) is not { } recipient)
        {
            return RoutedWhisperOutcome.RecipientNotHere;
        }

        ObjectGuid sender = ObjectGuid.Player((uint)delivery.SenderGuid);
        if (context.Friends.HasIgnore(recipient, sender))
        {
            return RoutedWhisperOutcome.Ignored;
        }

        recipient.Session.Send(WorldOpcode.SmsgMessagechat,
            ChatPackets.BuildMessage(ChatType.Whisper, Language.Universal, sender, delivery.PayloadText, ChatTag.None));
        return RoutedWhisperOutcome.Delivered;
    }
}
