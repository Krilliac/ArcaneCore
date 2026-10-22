using System.Globalization;
using System.Runtime.CompilerServices;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Channels;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Party;
using ArcaneCore.World.Social;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Playerbots.PlayerbotGeometry;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// World thread: say one <see cref="BotSpeech"/> through the bot's own session, as the client would (docs/areas/playbots.md, Bot speech).
/// In order: the bot may speak there now (a group for party chat, a raid for raid chat and the lead for a raid warning, a battleground
/// for battleground chat, the guild the line was written for, membership of the world channel, an online whisper target, an emote target
/// on its map; dead bots only whisper); somebody real can hear a line the bot starts on its own
/// (<see cref="PlayerbotSpeechOptions.RequireRealListener"/>; a reply's audience is its addressee); the place's shared budget, the dedupe
/// and the bot's own limits (<see cref="PlayerbotSpeechLimiter.TryTake"/>); then the client packet (CMSG_MESSAGECHAT with the line, or
/// CMSG_TEXT_EMOTE), sent like the bot's other replies with no action budget, and the line is counted and logged
/// (<see cref="PlayerbotChat.NoteSpoken"/>). Nothing here is a server broadcast: the server's own chat handlers deliver the packet.
/// </summary>
internal static class PlayerbotSpeechDelivery
{
    /// <summary>Say <paramref name="speech"/> as <paramref name="player"/>; the outcome (Queued means said).</summary>
    internal static BotSpeechAdmission Deliver(PlayerbotChat chat, Guid botId, WorldSession session, Player player, BotSpeech speech)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(speech);
        BotSpeechAdmission outcome = Say(chat, botId, session, player, speech);
        if (outcome != BotSpeechAdmission.Queued) chat.NoteDropped(speech, outcome);
        return outcome;
    }

    private static BotSpeechAdmission Say(PlayerbotChat chat, Guid botId, WorldSession session, Player player, BotSpeech speech)
    {
        PlayerbotSpeechOptions options = chat.SpeechOptions;
        long now = chat.NowMs;
        BotSpeechAdmission peek = chat.Limiter.Peek(botId, player.Guid, speech, options, now);
        if (peek != BotSpeechAdmission.Queued) return peek;
        if (!player.IsAlive && speech.Channel is not BotSpeechChannel.Whisper and not BotSpeechChannel.Party and not BotSpeechChannel.Raid
            and not BotSpeechChannel.Guild and not BotSpeechChannel.World)
            return BotSpeechAdmission.Unavailable;
        SocialContext? social = Social(session);
        bool needListener = options.RequireRealListener && !speech.IsReply;
        Place place = Locate(session, player, speech, social);
        if (!place.Available) return BotSpeechAdmission.Unavailable;
        if (needListener && !place.HasRealListener) return BotSpeechAdmission.NoListener;
        SpeechScope? scope = place.ScopeKey is null ? null
            : speech.Channel == BotSpeechChannel.World
                ? SpeechScope.Channel(place.ScopeKey, options.ChannelLinesPerMinute, place.Bots, options.ChannelCrowdBots)
                : new SpeechScope(place.ScopeKey, place.ScopeCap(options), SpeechScope.MinuteMs);
        BotSpeechAdmission taken = chat.Limiter.TryTake(botId, player.Guid, speech, scope, options, now);
        if (taken != BotSpeechAdmission.Queued) return taken;
        if (!Send(session, player, speech, place)) return BotSpeechAdmission.Unavailable;
        chat.NoteSpoken(player.Name, speech, speech.Channel == BotSpeechChannel.Emote ? 0 : speech.Text.Length);
        return BotSpeechAdmission.Queued;
    }

    /// <summary>Where the line would be said: whether the bot may speak there, whether a real player hears it, its shared scope.</summary>
    private readonly record struct Place(bool Available, bool HasRealListener, string? ScopeKey, int Bots, string? ChannelName = null, string? Target = null,
        ChatType? SayAs = null)
    {
        internal static Place None => new(false, false, null, 0);

        internal int ScopeCap(PlayerbotSpeechOptions options) => ScopeKey switch
        {
            null => 0,
            _ when ScopeKey.StartsWith("local:", StringComparison.Ordinal) => options.LocalLinesPerMinute,
            _ when ScopeKey.StartsWith("guild:", StringComparison.Ordinal) => options.GuildLinesPerMinute,
            _ => options.GroupLinesPerMinute,
        };
    }

    private static Place Locate(WorldSession session, Player player, BotSpeech speech, SocialContext? social)
    {
        WorldRuntime world = session.World;
        switch (speech.Channel)
        {
            case BotSpeechChannel.Say:
            case BotSpeechChannel.Yell:
            {
                if (player.Map is not { } map) return Place.None;
                WorldRuntimeOptions o = world.Options;
                float range = speech.Channel == BotSpeechChannel.Yell ? o.ListenRangeYell
                    : o.ListenRangeSay > 0 && o.ListenRangeYell > 0 ? Math.Min(o.ListenRangeSay, o.ListenRangeYell) : o.ListenRangeSay;
                bool real = false;
                foreach (ObjectGuid guid in player.VisibleObjects)
                {
                    if (world.FindOnlinePlayer(guid) is not { Session: WorldSession { IsManaged: false } } other || !ReferenceEquals(other.Map, map)) continue;
                    if (Distance(player, other) <= range) { real = true; break; }
                }

                return new Place(true, real, string.Create(CultureInfo.InvariantCulture, $"local:{map.MapId}:{map.InstanceId}:{player.ZoneId}"), 0);
            }

            case BotSpeechChannel.Party:
            case BotSpeechChannel.Raid:
            case BotSpeechChannel.RaidWarning:
            {
                if (social?.Groups.GetGroup(player.Guid) is not { } group) return Place.None;
                if (speech.Channel != BotSpeechChannel.Party && !group.IsRaid) return Place.None;
                if (speech.Channel == BotSpeechChannel.RaidWarning && !group.IsLeader(player.Guid)) return Place.None;
                bool real = false;
                foreach (GroupMemberSlot slot in group.Members)
                    if (slot.Guid != player.Guid && world.FindOnlinePlayer(slot.Guid) is { Session: WorldSession { IsManaged: false } }) { real = true; break; }
                // In a raid, party chat reaches only the bot's own subgroup (GroupManager.BroadcastChat): a party line goes on raid chat,
                // so a player in another subgroup hears it.
                ChatType? sayAs = speech.Channel == BotSpeechChannel.Party ? PlayerbotChatCommands.GroupChannel(group.IsRaid) : null;
                return new Place(true, real, "group:" + group.Id.ToString(CultureInfo.InvariantCulture), 0, SayAs: sayAs);
            }

            case BotSpeechChannel.Battleground:
            {
                IBattlegroundChatRoster? roster = session.Services.GetService<IBattlegroundChatRoster>()
                    ?? (session.Services.GetService<BattlegroundManager>() is { } manager ? new BattlegroundManagerChatRoster(manager) : null);
                if (roster?.TeamOf(player.Guid) is not { } team) return Place.None;
                bool real = false;
                foreach (ObjectGuid member in team.Members)
                    if (member != player.Guid && world.FindOnlinePlayer(member) is { Session: WorldSession { IsManaged: false } }) { real = true; break; }
                return new Place(true, real, string.Create(CultureInfo.InvariantCulture, $"bg:{player.MapId}:{player.Map?.InstanceId ?? 0}:{(int)player.Team}"), 0);
            }

            case BotSpeechChannel.Guild:
            {
                if (social?.Guilds.GetGuildOf(player) is not { } guild) return Place.None;
                if (speech.Guild is { } wanted && !string.Equals(guild.Name, wanted, StringComparison.OrdinalIgnoreCase)) return Place.None;
                bool real = false;
                foreach (GuildMember member in guild.Members)
                    if (guild.HasRight(member.Rank, GuildRights.GuildChatListen)
                        && world.FindOnlinePlayer(member.Guid) is { Session: WorldSession { IsManaged: false } }) { real = true; break; }
                return new Place(true, real, "guild:" + guild.Id.ToString(CultureInfo.InvariantCulture), 0);
            }

            case BotSpeechChannel.Whisper:
            {
                Player? target = !speech.TargetGuid.IsEmpty ? world.FindOnlinePlayer(speech.TargetGuid) : null;
                target ??= speech.Target is { Length: > 0 } name ? world.FindOnlinePlayer(name) : null;
                if (target is null || target.Guid == player.Guid) return Place.None;
                bool real = target.Session is WorldSession { IsManaged: false };
                return new Place(true, real || speech.ToBot, null, 0, Target: target.Name);
            }

            case BotSpeechChannel.World:
            {
                if (social is null || speech.ChannelName is not { Length: > 0 } name || social.Channels.Find(player.Team, name) is not { } channel
                    || !channel.IsOn(player.Guid)) return Place.None;
                int bots = 0;
                bool real = false;
                foreach (ObjectGuid member in channel.Members)
                {
                    if (world.FindOnlinePlayer(member) is not { } other) continue;
                    if (other.Session is WorldSession { IsManaged: true }) bots++;
                    else real = true;
                }

                string key = string.Create(CultureInfo.InvariantCulture, $"ch:{RuntimeHelpers.GetHashCode(channel):X}:{channel.Name.ToLowerInvariant()}");
                return new Place(true, real, key, bots, ChannelName: channel.Name);
            }

            case BotSpeechChannel.Emote:
            {
                if (speech.TargetGuid.IsEmpty) return new Place(player.Map is not null, true, null, 0);
                if (world.FindOnlinePlayer(speech.TargetGuid) is not { } target || !ReferenceEquals(target.Map, player.Map)) return Place.None;
                return new Place(true, target.Session is WorldSession { IsManaged: false } || speech.ToBot, null, 0);
            }

            default:
                return Place.None;
        }
    }

    private static bool Send(WorldSession session, Player player, BotSpeech speech, Place place)
    {
        Language language = player.Team == Team.Horde ? Language.Orcish : Language.Common;
        (WorldOpcode opcode, byte[] payload) = speech.Channel switch
        {
            BotSpeechChannel.Whisper => (WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Whisper(language, place.Target ?? speech.Target ?? string.Empty, speech.Text)),
            BotSpeechChannel.World => (WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.ChannelMessage(language, place.ChannelName ?? speech.ChannelName ?? string.Empty, speech.Text)),
            BotSpeechChannel.Emote => (WorldOpcode.CmsgTextEmote, BotSocial.PlayerbotSocialPackets.TextEmote(speech.TextEmote, speech.TargetGuid)),
            _ => (WorldOpcode.CmsgMessagechat, PlayerbotChatCommands.Message(place.SayAs ?? speech.ChatType, language, speech.Text)),
        };
        return BotSocial.PlayerbotSocialPackets.Send(session, opcode, payload);
    }

    private static SocialContext? Social(WorldSession session)
    {
        try
        {
            return session.Services.GetService<SocialFeature>()?.Context;
        }
        catch (InvalidOperationException error)
        {
            FaultLedger.Shared.Record("playerbot.chat-delivery", error);
            AnomalyLedger.Shared.Report("bot-chat", "speech-delivery-fault", "speech-delivery", error.Message);
            return null; // not attached
        }
    }

}
