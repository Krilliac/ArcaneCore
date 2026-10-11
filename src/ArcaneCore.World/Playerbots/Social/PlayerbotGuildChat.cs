using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Guilds;
using ArcaneCore.Game.Instances;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Social;
using ArcaneCore.Kernel.Social;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots.Chat;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// One thing a guild's bots may remark on: the key of its built-in lines (<see cref="PlayerbotSocialTemplates"/>), a plain sentence of what
/// happened (screened like any input), the instruction a model gets, the placeholder values, and who may say it
/// (<see cref="OnlySpeaker"/>: only that character; <see cref="ExceptSpeaker"/>: anyone but).
/// </summary>
internal sealed record PlayerbotGuildTopic(
    string Key, string Sentence, string Instruction, IReadOnlyDictionary<string, string> Values, long QueuedMs,
    uint? OnlySpeaker = null, uint? ExceptSpeaker = null);

/// <summary>
/// What bots say in their guild's chat (docs/areas/playbots-social.md). Events and chance queue <i>topics</i> per guild (at most
/// <see cref="MaxTopicsPerGuild"/>, the oldest dropped; a topic older than <see cref="TopicLifeMs"/> is dropped unsaid):
/// a real player joined (<c>welcome</c>), a member reached level 10, 20, ... (<c>ding</c> by the member itself when it is a bot, otherwise
/// <c>grats</c> by another bot), a dungeon or raid boss fell (<c>boss</c>, once per guild, map instance and victim within <see cref="BossSeenLifeMs"/>, for the guilds of the killer and its
/// group), a bot won a battleground (<c>bgwin</c>, once per guild in <see cref="PlayerbotSocialTimings.BgWinSpacingMs"/>), and small talk
/// (<c>chatter</c>, a chance per pass aiming at half of <see cref="PlayerbotSocialGuildChatOptions.MessagesPerHour"/> an hour).
/// <para>
/// A topic is said only when guild chat is on, <see cref="PlayerbotSocialGuildChatOptions.MessagesPerHour"/> is above zero, the bot chat
/// service is on, a real player who can read guild chat is online, the guild's hourly window has room and a bot member that may speak, is
/// not scripted, is out of combat and has not spoken within <see cref="PlayerbotSocialTimings.SpeakerGapMs"/> is there. The line then goes
/// through <see cref="PlayerbotChat.TryAnnounce"/>: the providers, caps and safety screening of the bot chat, built-in lines under
/// <c>RequireOptIn</c>; the window counts the line when it is queued. One guild line per guild per pass at most.
/// </para>
/// </summary>
internal sealed class PlayerbotGuildChat(ILogger logger)
{
    /// <summary>The most topics one guild keeps waiting.</summary>
    internal const int MaxTopicsPerGuild = 4;

    /// <summary>How long a topic stays worth saying (five minutes of world time).</summary>
    internal const long TopicLifeMs = 5 * 60_000;

    /// <summary>How long a boss kill is remembered per guild, map instance and victim (ten minutes of world time), and the most kept.</summary>
    internal const long BossSeenLifeMs = 10 * 60_000;

    private const int MaxBossSeen = 4096;

    private readonly Dictionary<int, List<PlayerbotGuildTopic>> _topics = [];
    private readonly PlayerbotSocialRateLimiter<int> _guilds = new();
    private readonly PlayerbotSocialRateLimiter<Guid> _speakers = new();
    private readonly Dictionary<(int Guild, uint Instance, ulong Victim), long> _bossSeen = [];
    private readonly Dictionary<int, long> _bgWinAt = [];
    private long _sweptAtMs;

    /// <summary>Topics waiting for a guild (tests and status).</summary>
    internal int Waiting(int guildId) => _topics.TryGetValue(guildId, out List<PlayerbotGuildTopic>? list) ? list.Count : 0;

    /// <summary>Lines the guild has had queued within the hour.</summary>
    internal int UsedThisHour(int guildId, long nowMs) => _guilds.Used(guildId, nowMs);

    internal void Queue(int guildId, PlayerbotGuildTopic topic)
    {
        if (!_topics.TryGetValue(guildId, out List<PlayerbotGuildTopic>? list)) _topics[guildId] = list = [];
        list.Add(topic);
        while (list.Count > MaxTopicsPerGuild) list.RemoveAt(0);
    }

    // --- topic sources (world thread) ---------------------------------------------------------------------------------

    /// <summary>A character joined <paramref name="guild"/> (not through a charter): a real player is welcomed; the founder and the bots are not.</summary>
    internal void OnMemberJoined(PlayerbotSocialPass pass, Guild guild, uint characterId, int petitionId)
    {
        if (petitionId != 0 || characterId == guild.LeaderId || pass.Registered.Contains(characterId)) return;
        if (pass.Social.Characters.Find(characterId)?.Name is not { } name) return;
        Queue(guild.Id, new PlayerbotGuildTopic("guild.welcome", $"{name} joined the guild {guild.Name}.",
            $"Welcome {name}, who has just joined your guild {guild.Name}.", Values(("guild", guild.Name), ("who", name)), pass.NowMs, ExceptSpeaker: characterId));
    }

    /// <summary>A player's level changed: a multiple of ten in a guild is remarked on.</summary>
    internal void OnLevelChanged(PlayerbotSocialPass pass, Player player)
    {
        byte level = player.Level;
        if (level < 10 || level % 10 != 0 || pass.Social.Guilds.GetGuildOf(player) is not { } guild) return;
        string number = level.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (player.Session is WorldSession { IsManaged: true })
            Queue(guild.Id, new PlayerbotGuildTopic("guild.ding", $"{player.Name} reached level {number}.",
                $"You have just reached level {number}. Say so, briefly.", Values(("guild", guild.Name), ("who", player.Name), ("newlevel", number)),
                pass.NowMs, OnlySpeaker: player.Guid.Low));
        else
            Queue(guild.Id, new PlayerbotGuildTopic("guild.grats", $"{player.Name} reached level {number}.",
                $"Congratulate {player.Name}, who has just reached level {number}.", Values(("guild", guild.Name), ("who", player.Name), ("newlevel", number)),
                pass.NowMs, ExceptSpeaker: player.Guid.Low));
    }

    /// <summary>
    /// A unit died: a dungeon or raid boss (the instance-bind flag, <see cref="InstanceManager.CreatureFlagExtraInstanceBind"/>, or a world-boss
    /// rank) killed by a player queues one topic for each guild of the killer and of its group members. A boss keeps the same creature guid in every
    /// instance of its dungeon, so a kill is keyed by the map instance too and forgotten after <see cref="BossSeenLifeMs"/> (an open-world boss, instance 0, respawns).
    /// </summary>
    internal void OnUnitKilled(PlayerbotSocialPass pass, Map map, Unit? killer, Unit victim)
    {
        if (victim is not Creature creature || map.Template is not { IsDungeon: true } template) return;
        if ((creature.Template.ExtraFlags & InstanceManager.CreatureFlagExtraInstanceBind) == 0 && !creature.IsWorldBoss) return;
        if (killer is null || DuelRules.ControllingPlayer(killer) is not { } player) return;
        var members = new List<Player> { player };
        if (pass.Social.Groups.GetGroup(player.Guid) is { } group)
            foreach (GroupMemberSlot slot in group.Members)
                if (slot.Guid != player.Guid && pass.World.FindOnlinePlayer(slot.Guid) is { } other) members.Add(other);
        foreach (Player member in members)
            if (pass.Social.Guilds.GetGuildOf(member) is { } guild)
                QueueBoss(pass, guild, map.InstanceId, creature.Guid.Value, creature.Template.Name, template.Name, player.Name);
    }

    /// <summary>Queue the boss remark for <paramref name="guild"/> unless this (guild, instance, victim) was remarked on within <see cref="BossSeenLifeMs"/>.</summary>
    internal bool QueueBoss(PlayerbotSocialPass pass, Guild guild, uint instanceId, ulong victimGuid, string boss, string instance, string who)
    {
        if (_bossSeen.Count >= MaxBossSeen)
        {
            long now = pass.NowMs;
            foreach (KeyValuePair<(int Guild, uint Instance, ulong Victim), long> entry in _bossSeen.ToArray())
                if (now - entry.Value >= BossSeenLifeMs) _bossSeen.Remove(entry.Key);
            if (_bossSeen.Count >= MaxBossSeen) _bossSeen.Clear();
        }

        (int, uint, ulong) key = (guild.Id, instanceId, victimGuid);
        if (_bossSeen.TryGetValue(key, out long at) && pass.NowMs - at < BossSeenLifeMs) return false;
        _bossSeen[key] = pass.NowMs;
        Queue(guild.Id, new PlayerbotGuildTopic("guild.boss", $"{boss} was killed in {instance}.",
            $"Remark on the news that {boss} has just been killed in {instance}.",
            Values(("guild", guild.Name), ("who", who), ("boss", boss), ("instance", instance)), pass.NowMs));
        return true;
    }

    /// <summary>A bot won a battleground (SMSG_BATTLEFIELD_WIN): its guild hears of it, at most once per <see cref="PlayerbotSocialTimings.BgWinSpacingMs"/>.</summary>
    internal void OnBattlegroundWon(PlayerbotSocialPass pass, PlayerbotSocialBot bot, string battleground)
    {
        if (pass.Social.Guilds.GetGuildOf(bot.Player) is not { } guild) return;
        if (_bgWinAt.TryGetValue(guild.Id, out long last) && pass.NowMs - last < pass.Timings.BgWinSpacingMs) return;
        _bgWinAt[guild.Id] = pass.NowMs;
        Queue(guild.Id, new PlayerbotGuildTopic("guild.bgwin", $"A member of the guild won {battleground}.",
            $"Your guild has just won {battleground}. Say so, briefly.", Values(("guild", guild.Name), ("who", bot.Player.Name), ("battleground", battleground)), pass.NowMs));
    }

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
    {
        var values = new Dictionary<string, string>(pairs.Length, StringComparer.Ordinal);
        foreach ((string key, string value) in pairs) values[key] = value;
        return values;
    }

    // --- speaking -----------------------------------------------------------------------------------------------------

    /// <summary>One pass: small talk by chance, then at most one waiting topic per guild is handed to the chat service.</summary>
    internal void Speak(PlayerbotSocialPass pass)
    {
        PlayerbotSocialGuildChatOptions options = pass.Options.GuildChat;
        if (!options.Enabled || options.MessagesPerHour <= 0 || pass.Chat is not { IsActive: true } chat)
        {
            _topics.Clear();
            return;
        }

        if (pass.NowMs - _sweptAtMs >= 60_000)
        {
            _sweptAtMs = pass.NowMs;
            _guilds.Sweep(pass.NowMs);
            _speakers.Sweep(pass.NowMs, pass.Timings.SpeakerGapMs);
            foreach (int id in _bgWinAt.Keys.ToArray())
                if (pass.Social.Guilds.Get(id) is null) { _bgWinAt.Remove(id); _guilds.Forget(id); }
        }

        if (pass.Timings.IdleChatter) RollSmallTalk(pass, options);
        foreach (int id in _topics.Keys.ToArray())
        {
            List<PlayerbotGuildTopic> list = _topics[id];
            list.RemoveAll(topic => pass.NowMs - topic.QueuedMs >= TopicLifeMs);
            if (list.Count == 0 || pass.Social.Guilds.Get(id) is not { } guild)
            {
                _topics.Remove(id);
                continue;
            }

            if (!_guilds.HasRoom(id, options.MessagesPerHour, pass.NowMs) || !HasListener(pass, guild)) continue;
            foreach (PlayerbotGuildTopic topic in list.ToArray())
            {
                if (PickSpeaker(pass, guild, topic) is not { } speaker) continue;
                BotChatAdmission admission = chat.TryAnnounce(Ask(pass, guild, speaker, topic));
                if (admission == BotChatAdmission.Queued)
                {
                    _guilds.TryTake(id, options.MessagesPerHour, pass.NowMs);
                    _speakers.MarkSpoke(speaker.Id, pass.NowMs);
                    list.Remove(topic);
                    logger.LogDebug("Playerbot {Bot} says {Topic} in the guild {Guild}", speaker.Player.Name, topic.Key, guild.Name);
                }
                else if (admission == BotChatAdmission.Disabled)
                {
                    _topics.Clear();
                    return;
                }

                break; // one line per guild per pass; a refused one (budget, queue) is tried again next pass
            }
        }
    }

    private void RollSmallTalk(PlayerbotSocialPass pass, PlayerbotSocialGuildChatOptions options)
    {
        double chance = options.MessagesPerHour / 2.0 * pass.ElapsedMs / PlayerbotSocialRateLimiter<int>.HourMs;
        foreach (Guild guild in pass.Social.Guilds.All)
        {
            if (Waiting(guild.Id) > 0 || pass.Random.NextDouble() >= chance) continue;
            Queue(guild.Id, new PlayerbotGuildTopic("guild.chatter", "Some small talk in the guild chat.",
                "Say one short, friendly line of small talk about what you are doing in the game.", Values(("guild", guild.Name)), pass.NowMs));
        }
    }

    private static BotChatAsk Ask(PlayerbotSocialPass pass, Guild guild, PlayerbotSocialBot speaker, PlayerbotGuildTopic topic)
        => new(speaker.Id, speaker.Player.Guid, speaker.Party.PersonaFor(speaker.Player), speaker.Player.Guid, speaker.Player.Name, ChatType.Guild,
            topic.Sentence, FromMaster: false, InviteAllowed: false, SenderAccountId: 0,
            Topic: new BotChatTopic(topic.Key, topic.Instruction, topic.Values, guild.Name));

    /// <summary>Whether a real (non-managed) member who may read guild chat is online: no one else is addressed, so nothing is said to bots alone.</summary>
    private static bool HasListener(PlayerbotSocialPass pass, Guild guild)
    {
        foreach (GuildMember member in guild.Members)
            if (guild.HasRight(member.Rank, GuildRights.GuildChatListen)
                && pass.World.FindOnlinePlayer(member.Guid) is { Session: WorldSession { IsManaged: false } })
                return true;
        return false;
    }

    private PlayerbotSocialBot? PickSpeaker(PlayerbotSocialPass pass, Guild guild, PlayerbotGuildTopic topic)
    {
        var candidates = new List<PlayerbotSocialBot>();
        foreach (GuildMember member in guild.Members)
        {
            if (topic.OnlySpeaker is { } only && member.CharacterId != only) continue;
            if (topic.ExceptSpeaker == member.CharacterId) continue;
            if (!guild.HasRight(member.Rank, GuildRights.GuildChatSpeak) || !pass.ByCharacter.TryGetValue(member.CharacterId, out PlayerbotSocialBot? bot)) continue;
            if (!bot.Player.IsAlive || bot.Player.Combat.IsInCombat) continue;
            if (!_speakers.GapElapsed(bot.Id, pass.Timings.SpeakerGapMs, pass.NowMs)) continue;
            candidates.Add(bot);
        }

        return candidates.Count == 0 ? null : candidates[pass.Random.Next(candidates.Count)];
    }
}
