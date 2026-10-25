using System.Globalization;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Events:Holidays</c>: bots at a running festival (docs/areas/playbots-social.md, World events). While a game
/// event with a client holiday id runs (Midsummer, Winter Veil, Hallow's End, ...), an idle bot that stays in a town may stand around the
/// festival's NPCs instead of at the inn: 8 to 12 yards from a festival NPC that talks, gives quests or sells (never inside the NPC's
/// interaction distance), at most <see cref="MaxPerServiceAnchor"/> around a quest giver or vendor, <see cref="MaxPerGossipAnchor"/> around
/// an NPC that only talks and <see cref="MaxPerTown"/> in a town per team. There it dances, cheers or bows now and then (an untargeted text
/// emote, at most <see cref="EmotesPerListenerMinute"/> a minute to one real player and <see cref="EmotesPerAnchorMinute"/> at one anchor),
/// says one line when it arrives, and the "an event has started" remark names the festival. A player hears at most one unprompted line from
/// all bots together per <c>Social:Initiative:OpeningCooldownMinutes</c>, built-in only after "ai off", none when cut off or ignoring the
/// bot. Every key is live.
/// </summary>
public sealed class PlayerbotHolidayOptions
{
    /// <summary>The most event ids <see cref="ExcludedEvents"/> may name.</summary>
    public const int MaxExcludedEvents = 64;

    /// <summary>Gather and celebrate at running festivals (on by default; <c>Social:Events:Enabled</c> is the master switch).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The percent chance (0..100, default 60) that a bot celebrates a festival on a given day; rolled per bot per festival per day (the day
    /// changes for each bot at a time of its own). Sociable bots celebrate more often: <c>p = 1 - (1 - c)^(0.5 + sociability)</c>, which is
    /// exactly <c>c</c> at sociability 0.5. 0 sends nobody, 100 every bot.
    /// </summary>
    public int CelebrateChance { get; set; } = 60;

    /// <summary>How far from the town centre a festival NPC may stand to count, in yards (20..1000, default 250); it must also be inside the bot's home.</summary>
    public int SpotRadiusYards { get; set; } = 250;

    /// <summary>
    /// The minutes a festival NPC whose spot a bot could not reach is set aside for that bot, and a town whose crowd was attacked by a player
    /// is set aside for that team (1..1440, default 30).
    /// </summary>
    public int SpotBackoffMinutes { get; set; } = 30;

    /// <summary>The most bots around one festival NPC that only talks (1..20, default 6).</summary>
    public int MaxPerGossipAnchor { get; set; } = 6;

    /// <summary>The most bots around one festival quest giver or vendor (0..10, default 3; 0 keeps bots away from them).</summary>
    public int MaxPerServiceAnchor { get; set; } = 3;

    /// <summary>The most bots celebrating in one town per team (1..200, default 24).</summary>
    public int MaxPerTown { get; set; } = 24;

    /// <summary>The shortest gap between two celebration emotes of one bot, in seconds (5..3600, default 30).</summary>
    public int EmoteMinSeconds { get; set; } = 30;

    /// <summary>The longest gap between two celebration emotes of one bot, in seconds (5..3600, not below <see cref="EmoteMinSeconds"/>, default 90).</summary>
    public int EmoteMaxSeconds { get; set; } = 90;

    /// <summary>The festival text emotes one real player receives per minute from all bots together (0..10, default 2; 0 sends none while a player is in range).</summary>
    public int EmotesPerListenerMinute { get; set; } = 2;

    /// <summary>The festival text emotes around one anchor per minute, players in range or not (0..30, default 3).</summary>
    public int EmotesPerAnchorMinute { get; set; } = 3;

    /// <summary>Let bots say the arrival line and the named event remark (on by default; chat must be on as well).</summary>
    public bool TalkEnabled { get; set; } = true;

    /// <summary>The minutes between two festival lines of the same bot (1..1440, default 20).</summary>
    public int TalkBotGapMinutes { get; set; } = 20;

    /// <summary>The seconds between two festival lines in the same zone, whichever bot (1..86400, default 120).</summary>
    public int TalkZoneGapSeconds { get; set; } = 120;

    /// <summary>
    /// Game event ids that are never treated as a festival or part of one, separated by commas and/or spaces (<c>"12, 24 25"</c>; each 1..65535,
    /// at most 64; empty: none). A string so that a reload compares it by value.
    /// </summary>
    public string ExcludedEvents { get; set; } = string.Empty;

    /// <summary>The ids of <paramref name="text"/> and the first error; the ids are empty when there is one. Duplicates count once.</summary>
    public static (IReadOnlySet<ushort> Ids, string? Error) ParseExcludedEvents(string? text)
    {
        var ids = new HashSet<ushort>();
        if (string.IsNullOrWhiteSpace(text)) return (ids, null);
        foreach (string token in text.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                return (new HashSet<ushort>(), $"'{token}' is not an event id (a number)");
            if (id is < 1 or > ushort.MaxValue) return (new HashSet<ushort>(), $"event id {id} must be 1..{ushort.MaxValue}");
            ids.Add((ushort)id);
            if (ids.Count > MaxExcludedEvents) return (new HashSet<ushort>(), $"at most {MaxExcludedEvents} event ids");
        }

        return (ids, null);
    }

    /// <summary>The first error of an <see cref="ExcludedEvents"/> text, or null when it is valid (the live key's check).</summary>
    public static string? CheckExcludedEvents(string? text) => ParseExcludedEvents(text).Error;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Events:Holidays";
        if (CelebrateChance is < 0 or > 100) throw new InvalidOperationException($"{section}: CelebrateChance must be 0..100.");
        if (SpotRadiusYards is < 20 or > 1000) throw new InvalidOperationException($"{section}: SpotRadiusYards must be 20..1000.");
        if (SpotBackoffMinutes is < 1 or > 1440) throw new InvalidOperationException($"{section}: SpotBackoffMinutes must be 1..1440.");
        if (MaxPerGossipAnchor is < 1 or > 20) throw new InvalidOperationException($"{section}: MaxPerGossipAnchor must be 1..20.");
        if (MaxPerServiceAnchor is < 0 or > 10) throw new InvalidOperationException($"{section}: MaxPerServiceAnchor must be 0..10.");
        if (MaxPerTown is < 1 or > 200) throw new InvalidOperationException($"{section}: MaxPerTown must be 1..200.");
        if (EmoteMinSeconds is < 5 or > 3600) throw new InvalidOperationException($"{section}: EmoteMinSeconds must be 5..3600.");
        if (EmoteMaxSeconds is < 5 or > 3600) throw new InvalidOperationException($"{section}: EmoteMaxSeconds must be 5..3600.");
        if (EmoteMaxSeconds < EmoteMinSeconds) throw new InvalidOperationException($"{section}: EmoteMaxSeconds must not be below EmoteMinSeconds.");
        if (EmotesPerListenerMinute is < 0 or > 10) throw new InvalidOperationException($"{section}: EmotesPerListenerMinute must be 0..10.");
        if (EmotesPerAnchorMinute is < 0 or > 30) throw new InvalidOperationException($"{section}: EmotesPerAnchorMinute must be 0..30.");
        if (TalkBotGapMinutes is < 1 or > 1440) throw new InvalidOperationException($"{section}: TalkBotGapMinutes must be 1..1440.");
        if (TalkZoneGapSeconds is < 1 or > 86_400) throw new InvalidOperationException($"{section}: TalkZoneGapSeconds must be 1..86400.");
        if (CheckExcludedEvents(ExcludedEvents) is { } error) throw new InvalidOperationException($"{section}: ExcludedEvents: {error}.");
    }
}
