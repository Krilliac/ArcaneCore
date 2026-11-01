using System.Runtime.CompilerServices;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Cluster.Social;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using ArcaneCore.World.Social;
using ArcaneCore.Data.Social;
using ArcaneCore.Game;
using ArcaneCore.Game.Chat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Talents;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.World.Features;
using ArcaneCore.World.Items;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Talents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Chat;

/// <summary>
/// Per-speaker chat state and the gates that read it (docs/areas/chat.md): the flood mute
/// (vmangos MasterPlayer::UpdateSpeakTime / Player::CanSpeak) and the whisper acceptance of game
/// masters (vmangos MasterPlayer::AcceptsWhispersFrom). State is in memory only. The flood mute is
/// the session's (vmangos WorldSession::m_muteTime), so it is keyed by account id and survives a
/// logout to the character screen and a relog; the flood counter and the whisper state are the
/// player's (vmangos MasterPlayer) and are keyed by the <see cref="Player"/> object. Account mutes
/// come from <see cref="IChatMuteSource"/>s. World thread only.
/// </summary>
public sealed class ChatFeature : IWorldFeature
{
    private readonly IConfiguration? _configuration;
    private readonly IServiceProvider? _services;
    private readonly IChatMuteSource[] _muteSources;
    private readonly TimeProvider _clock;
    private readonly ConditionalWeakTable<Player, ChatState> _states = [];
    private readonly Dictionary<int, long> _sessionMutes = [];
    private long _lastMuteSweepUnixSeconds = long.MinValue;

    public ChatFeature(
        IConfiguration? configuration = null,
        IEnumerable<IChatMuteSource>? muteSources = null,
        TimeProvider? timeProvider = null,
        IServiceProvider? services = null)
    {
        _configuration = configuration;
        _muteSources = muteSources is null ? [] : [.. muteSources];
        _clock = timeProvider ?? TimeProvider.System;
        _services = services;
    }

    /// <summary>The chat rules.</summary>
    public ChatOptions Options { get; } = new();

    /// <summary>Optional override for severity-3 hyperlink catalog existence checks.</summary>
    public Func<string, uint, bool>? LinkExists { get; set; }

    /// <summary>The client's text emotes and emotes (<see cref="ChatOptions.EmotesTextDbcPath"/>); null without the files.</summary>
    public EmoteCatalog? Emotes { get; set; }

    /// <summary>Whole seconds since the Unix epoch (vmangos time(nullptr)).</summary>
    public long NowUnixSeconds => _clock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>
    /// Validates <paramref name="message"/> against <see cref="ChatOptions.StrictLinkSeverity"/>,
    /// including severity-3 catalog checks when a catalog or <see cref="LinkExists"/> is configured.
    /// </summary>
    public bool IsValidChatMessage(string message)
        => Options.StrictLinkSeverity <= 0
            || ChatSanitizer.IsValidChatMessage(message, Options.StrictLinkSeverity, LinkExists ?? ValidateCatalogLink);

    /// <summary>
    /// Checks whether a hyperlink entry exists in the active item, quest, spell, talent, or enchantment catalog.
    /// When a catalog is not loaded in the current host, falls back to accepting the link type.
    /// </summary>
    public bool ValidateCatalogLink(string linkType, uint id)
    {
        if (id == 0)
        {
            return false;
        }

        if (LinkExists is not null)
        {
            return LinkExists(linkType, id);
        }

        if (_services is null)
        {
            return true;
        }

        return linkType switch
        {
            "item" => _services.GetService<ItemsFeature>()?.Templates is { Count: > 0 } items
                ? items.Find(id) is not null
                : _services.GetService<IItemTemplateStore>() is not { Count: > 0 } itemStore || itemStore.Find(id) is not null,
            "quest" => _services.GetService<QuestNpcFeature>()?.Services.Quests is { Count: > 0 } quests
                ? quests.Get(id) is not null
                : _services.GetService<QuestStore>() is not { Count: > 0 } questStore || questStore.Get(id) is not null,
            "spell" => _services.GetService<SpellFeature>()?.System.Store is { Count: > 0 } spells
                ? spells.Get(id) is not null
                : _services.GetService<SpellStore>() is not { Count: > 0 } spellStore || spellStore.Get(id) is not null,
            "talent" => _services.GetService<TalentFeature>()?.Catalog is { TalentCount: > 0 } talents
                ? talents.ById(id) is not null
                : _services.GetService<TalentCatalog>() is not { TalentCount: > 0 } talentCatalog || talentCatalog.ById(id) is not null,
            "enchant" => _services.GetService<SpellFeature>()?.EnchantmentCatalogProvider?.Current is { } enchants
                ? enchants.Find(id) is not null
                : _services.GetService<IItemEnchantmentCatalog>() is { } enchantCatalog
                    ? enchantCatalog.Find(id) is not null
                    : _services.GetService<SpellFeature>()?.System.Store is not { Count: > 0 } enchantSpells || enchantSpells.Get(id) is not null,
            _ => false,
        };
    }

    public void Attach(WorldRuntime world)
    {
        _configuration?.GetSection(ChatOptions.SectionName).Bind(Options);
        if (Options.EmotesTextDbcPath.Length > 0 || Options.EmotesDbcPath.Length > 0)
        {
            if (Options.EmotesTextDbcPath.Length == 0 || Options.EmotesDbcPath.Length == 0)
            {
                throw new InvalidOperationException("World:Chat:EmotesTextDbcPath and World:Chat:EmotesDbcPath are set together or not at all");
            }

            Emotes = new EmoteCatalog(EmoteDbcReaders.LoadText(Options.EmotesTextDbcPath), EmoteDbcReaders.LoadEmotes(Options.EmotesDbcPath));
        }

        world.Updated += ExpireSessionMutes;
        Cluster = _services?.GetService<ClusterChatRouting>();
        if (Cluster is { } cluster)
        {
            cluster.Deliver = delivery => world.Post(() => ApplyClusterDelivery(world, delivery));
        }
    }

    /// <summary>The unix time until which <paramref name="player"/> cannot speak (vmangos WorldSession::m_muteTime): the latest of the flood mute and every source.</summary>
    public long MutedUntil(Player player)
    {
        long until = SessionMute(player.AccountId);
        foreach (IChatMuteSource source in _muteSources)
        {
            until = Math.Max(until, source.MutedUntilUnixSeconds(player));
        }

        return until;
    }

    /// <summary>vmangos Player::CanSpeak: <c>m_muteTime &lt;= time(nullptr)</c>.</summary>
    public bool CanSpeak(Player player) => MutedUntil(player) <= NowUnixSeconds;

    /// <summary>
    /// The text of the notification a muted speaker gets (mangos_string 705 with vmangos
    /// <c>secsToTimeString</c>), or null when the player can speak.
    /// </summary>
    public string? MuteNotice(Player player)
    {
        long now = NowUnixSeconds;
        long until = MutedUntil(player);
        return until <= now ? null : $"You must wait {ChatText.SecsToTimeString(until - now)} before speaking again.";
    }

    /// <summary>
    /// Mute <paramref name="player"/> for at least until <paramref name="untilUnixSeconds"/> (never
    /// shortens an existing mute, as vmangos UpdateSpeakTime "prevent overwrite mute time").
    /// </summary>
    public void MuteUntil(Player player, long untilUnixSeconds)
    {
        _sessionMutes[player.AccountId] = Math.Max(SessionMute(player.AccountId), untilUnixSeconds);
    }

    /// <summary>
    /// vmangos MasterPlayer::UpdateSpeakTime (MasterPlayerChat.cpp:10-35), the anti-flood counter:
    /// staff are exempt; a message inside the delay window counts, and the count reaching
    /// <see cref="ChatOptions.FloodMessageCount"/> mutes the speaker for
    /// <see cref="ChatOptions.FloodMuteSeconds"/> (the message that trips the mute is still delivered,
    /// because the mute check precedes this call in the handler).
    /// </summary>
    public void UpdateSpeakTime(Player player)
    {
        if (player.Security > AccountSecurity.Player)
        {
            return;
        }

        ChatState state = State(player);
        long current = NowUnixSeconds;
        if (state.SpeakTime > current)
        {
            uint max = Options.FloodMessageCount;
            if (max == 0)
            {
                return;
            }

            state.SpeakCount++;
            if (state.SpeakCount >= max)
            {
                MuteUntil(player, current + Options.FloodMuteSeconds);
                state.SpeakCount = 0;
            }
        }
        else
        {
            state.SpeakCount = 0;
        }

        state.SpeakTime = current + Options.FloodMessageDelaySeconds;
    }

    /// <summary>
    /// vmangos MasterPlayer::IsAcceptWhispers: plain players always accept ("players always
    /// accept", Player.cpp:134); a staff account starts as <see cref="ChatOptions.GmWhisperingTo"/> says.
    /// </summary>
    public bool AcceptsWhispers(Player player) => State(player).AcceptsWhispers;

    /// <summary>vmangos Player::SetAcceptWhispers (<c>.whispers on|off</c>).</summary>
    public void SetAcceptWhispers(Player player, bool on) => State(player).AcceptsWhispers = on;

    /// <summary>
    /// vmangos MasterPlayer::AcceptsWhispersFrom (MasterPlayer.h:99): the receiver accepts whispers
    /// in general, or has whispered <paramref name="whisperer"/> first.
    /// </summary>
    public bool AcceptsWhispersFrom(Player receiver, ObjectGuid whisperer)
    {
        ChatState state = State(receiver);
        return state.AcceptsWhispers || (state.AllowedWhisperers?.Contains(whisperer) ?? false);
    }

    /// <summary>vmangos MasterPlayer::Whisper (end): a sender that does not accept whispers lets the receiver whisper back.</summary>
    public void NoteWhisperSent(Player sender, Player receiver) => NoteWhisperSent(sender, receiver.Guid);

    /// <summary>The same for a receiver on another worker, known only by its guid.</summary>
    public void NoteWhisperSent(Player sender, ObjectGuid receiver)
    {
        ChatState state = State(sender);
        if (!state.AcceptsWhispers)
        {
            (state.AllowedWhisperers ??= []).Add(receiver);
        }
    }

    /// <summary>vmangos MasterPlayer::ClearAllowedWhisperers (<c>.whispers off</c>).</summary>
    public void ClearAllowedWhisperers(Player player) => State(player).AllowedWhisperers?.Clear();

    /// <summary>
    /// Drop the account's flood mute (<c>.unmute</c>; vmangos HandleUnmuteCommand sets <c>m_muteTime</c> to 0, the same
    /// field the flood mute lives in). True when a mute still in force was removed. Keyed by account, so it also covers a
    /// character that logged out to the character screen.
    /// </summary>
    public bool ClearMute(int accountId)
    {
        bool inForce = SessionMute(accountId) > 0;
        _sessionMutes.Remove(accountId);
        return inForce;
    }

    private long SessionMute(int accountId)
    {
        if (!_sessionMutes.TryGetValue(accountId, out long until))
        {
            return 0;
        }

        if (until <= NowUnixSeconds)
        {
            _sessionMutes.Remove(accountId); // expired: nothing to remember (CanSpeak is m_muteTime <= now)
            return 0;
        }

        return until;
    }

    private void ExpireSessionMutes(uint diffMs)
    {
        long now = NowUnixSeconds;
        if (now == _lastMuteSweepUnixSeconds)
        {
            return;
        }

        _lastMuteSweepUnixSeconds = now;
        // Once per clock second on the world thread, including an idle world with no maps:
        // disconnected accounts must not retain entries just because nobody queries them again.
        foreach (int accountId in _sessionMutes.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
        {
            _sessionMutes.Remove(accountId);
        }
    }

    /// <summary>
    /// Cross-worker chat (M15.2 S2c): the routing the cluster hosting registers, or null in a single-worker realm (and in a host without the cluster services), where every chat path
    /// behaves as before.
    /// </summary>
    public ClusterChatRouting? Cluster { get; private set; }

    /// <summary>
    /// Applies one chat frame that another worker routed to a character of this one (world thread; posted by the routing's pump). The delivery is checked against the registered
    /// session generation again here, because the session can be replaced between the pump and the tick; a stale one is dropped. Rules are the local ones: a whisper's faction,
    /// staff and mute gates and away/busy auto-reply (answered to the whisperer through <see cref="ClusterChatRouting.SendReply"/>), the sub-group of party chat, the listen
    /// right and ignore list of guild and officer chat, the channel membership, faction and ignore list of a channel line.
    /// </summary>
    internal void ApplyClusterDelivery(WorldRuntime world, ClusterChatDelivery delivery)
    {
        ClusterChatRouting? cluster = Cluster;
        if (cluster is null || !cluster.IsCurrent(delivery.RecipientGuid, delivery.RecipientSessionGeneration)
            || world.FindOnlinePlayer(new ObjectGuid(delivery.RecipientGuid)) is not { } recipient)
        {
            return;
        }

        ClusterChatFrame frame = delivery.Frame;
        var speaker = new ObjectGuid(frame.SenderGuid);
        SocialContext? social = _services?.GetService<SocialFeature>()?.Context;
        switch (frame.Kind)
        {
            case ClusterChatFrameKind.Whisper:
                ApplyClusterWhisper(world, cluster, recipient, frame);
                return;

            case ClusterChatFrameKind.WhisperReply:
                foreach (ClusterChatPacket packet in frame.Packets)
                {
                    recipient.Session.Send(packet.Opcode, packet.Payload);
                }

                if (frame.NoteWhisperGuid != 0)
                {
                    NoteWhisperSent(recipient, new ObjectGuid(frame.NoteWhisperGuid));
                }

                return;

            case ClusterChatFrameKind.Group:
                if (frame.SubGroup != ClusterChatFrame.NoSubGroup
                    && social?.Groups.GetGroup(recipient.Guid)?.Find(recipient.Guid) is { } slot && slot.SubGroup != frame.SubGroup)
                {
                    return; // party chat reaches the speaker's sub-group only (vmangos Group::BroadcastPacket with a sub-group)
                }

                break;

            case ClusterChatFrameKind.Guild:
            case ClusterChatFrameKind.Officer:
            {
                // vmangos Guild::BroadcastToGuild / BroadcastChatMsgToOfficers: the listen right, and not for a member who ignores the speaker.
                bool officer = frame.Kind == ClusterChatFrameKind.Officer;
                if (social is null || social.Guilds.GetGuildOf(recipient) is not { } guild || guild.Find(recipient.Guid.Low) is not { } member
                    || !guild.HasRight(member.Rank, officer ? GuildRights.OfficerChatListen : GuildRights.GuildChatListen)
                    || social.IsIgnoring(recipient, speaker))
                {
                    return;
                }

                break;
            }

            case ClusterChatFrameKind.Channel:
                if (social is null || frame.Channel is not { } channelName
                    || social.Channels.Find(recipient.Team, channelName) is not { } channel || !channel.IsOn(recipient.Guid)
                    || (!social.Options.AllowTwoSideChannel && (Team)frame.SenderTeam != recipient.Team)
                    || (!frame.IgnoreExempt && social.IsIgnoring(recipient, speaker)))
                {
                    return;
                }

                break;

            default:
                return;
        }

        foreach (ClusterChatPacket packet in frame.Packets)
        {
            recipient.Session.Send(packet.Opcode, packet.Payload);
        }
    }

    /// <summary>The receiving half of <c>ChatHandlers.Whisper</c> (vmangos MasterPlayer::Whisper) for a whisperer on another worker; every outcome is answered to the whisperer.</summary>
    private void ApplyClusterWhisper(WorldRuntime world, ClusterChatRouting cluster, Player receiver, ClusterChatFrame request)
    {
        var whisperer = new ObjectGuid(request.SenderGuid);
        var answering = new ClusterChatSender(receiver.Guid.Value, receiver.Name, receiver.AccountId);
        var senderSecurity = (AccountSecurity)request.SenderSecurity;

        void Answer(ulong noted, params ClusterChatPacket[] packets) => cluster.SendReply(
            answering,
            request.SenderGuid,
            new ClusterChatFrame { Kind = ClusterChatFrameKind.WhisperReply, SenderGuid = receiver.Guid.Value, NoteWhisperGuid = noted, Packets = packets });

        // A plain player cannot see a staff member who does not accept its whispers: the same notice as for an offline target.
        if (senderSecurity == AccountSecurity.Player && receiver.Security > AccountSecurity.Player
            && !AcceptsWhispersFrom(receiver, whisperer))
        {
            Answer(0, new ClusterChatPacket(WorldOpcode.SmsgChatPlayerNotFound, ChatPackets.BuildPlayerNotFound(receiver.Name)));
            return;
        }

        if (receiver.Security == AccountSecurity.Player && request.MuteNotice is { } notice)
        {
            Answer(0, new ClusterChatPacket(WorldOpcode.SmsgNotification, ChatPackets.BuildNotification(notice)));
            return;
        }

        if (senderSecurity == AccountSecurity.Player && receiver.Security == AccountSecurity.Player
            && !world.Options.AllowTwoSideChat && (Team)request.SenderTeam != receiver.Team)
        {
            Answer(0, new ClusterChatPacket(WorldOpcode.SmsgChatWrongFaction, []));
            return;
        }

        receiver.Session.Send(WorldOpcode.SmsgMessagechat,
            ChatPackets.BuildMessage(ChatType.Whisper, Language.Universal, whisperer, request.Text, (ChatTag)request.ChatTag));
        var replies = new List<ClusterChatPacket>
        {
            new(WorldOpcode.SmsgMessagechat, ChatPackets.BuildMessage(ChatType.WhisperInform, Language.Universal, receiver.Guid, request.Text, receiver.ChatTag)),
        };
        if (receiver.IsDnd)
        {
            replies.Add(new ClusterChatPacket(WorldOpcode.SmsgMessagechat,
                ChatPackets.BuildMessage(ChatType.Dnd, Language.Universal, receiver.Guid, receiver.DndMessage, ChatTag.None)));
        }
        else if (receiver.IsAfk)
        {
            replies.Add(new ClusterChatPacket(WorldOpcode.SmsgMessagechat,
                ChatPackets.BuildMessage(ChatType.Afk, Language.Universal, receiver.Guid, receiver.AfkMessage, ChatTag.None)));
        }

        Answer(receiver.Guid.Value, [.. replies]);
    }

    private ChatState State(Player player) => _states.GetValue(player, p => new ChatState(p.Security == AccountSecurity.Player || Options.GmWhisperingTo == 1));

    private sealed class ChatState(bool acceptsWhispers)
    {
        internal long SpeakTime;
        internal uint SpeakCount;
        internal bool AcceptsWhispers = acceptsWhispers;
        internal HashSet<ObjectGuid>? AllowedWhisperers;
    }
}
