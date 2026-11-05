using System.Text.RegularExpressions;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Groups;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>What a heard line asks of the bots' group and guild talk (<see cref="PlayerbotLfgIntents.Read"/>).</summary>
internal enum PlayerbotLfgIntent : byte
{
    /// <summary>Nothing the LFG talk answers.</summary>
    None,

    /// <summary>"Any groups for SFK?": the speaker looks for a group (or says what they would join).</summary>
    GroupSearch,

    /// <summary>"inv", "I can tank": the speaker offers to join a group a bot is looking to fill.</summary>
    Available,

    /// <summary>"ginv", "can I join your guild": the speaker asks for a guild invitation.</summary>
    GuildJoin,

    /// <summary>"Any guild recruiting?": the speaker looks for a guild.</summary>
    GuildSearch,
}

/// <summary>What a group search is about (<see cref="PlayerbotLfgIntents.Resolve"/>).</summary>
internal enum PlayerbotLfgSubjectKind : byte
{
    /// <summary>The line states no subject: anything that fits the asker.</summary>
    None,

    /// <summary>A five-player instance.</summary>
    Dungeon,

    /// <summary>A raid.</summary>
    Raid,

    /// <summary>A quest a bot waits for a group for, or one in the asker's own log.</summary>
    Quest,

    /// <summary>A zone, by its exact name.</summary>
    Zone,

    /// <summary>The line states a subject no fact backs (an unknown name): the bots do not guess.</summary>
    Unresolved,
}

/// <summary>
/// The subject of a group search. <paramref name="Key"/> is the matching key's value (<see cref="PlayerbotGroupGoal.Key"/>): the instance map of a
/// dungeon or raid, the objective creature of a quest goal (0 for a quest only the asker holds); <paramref name="QuestId"/> the quest;
/// <paramref name="ZoneId"/> the zone; <paramref name="Display"/> the name for a line to say.
/// </summary>
internal sealed record PlayerbotLfgSubject(PlayerbotLfgSubjectKind Kind, uint Key = 0, uint QuestId = 0, uint ZoneId = 0, string Display = "");

/// <summary>One group goal a bot waits for or a group forms for, as the resolver needs it: its kind, key value, quest and title, and where the caller stands.</summary>
internal sealed record PlayerbotLfgGoal(PlayerbotGroupGoalKind Kind, uint Key, uint QuestId, string? QuestTitle, uint CallerZoneId);

/// <summary>
/// The real state a subject is resolved against: the goals bots wait for, the asker's incomplete quests (id and title), and the zone ids by
/// plain name (<see cref="PlayerbotLfgZoneIndex.IdOf"/>; 0 for none).
/// </summary>
internal sealed record PlayerbotLfgFacts(IReadOnlyList<PlayerbotLfgGoal> Goals, IReadOnlyList<(uint QuestId, string Title)> AskerQuests, Func<string, uint> ZoneIdOf);

/// <summary>
/// The zones of the area table by plain name ("elwynn forest", and "barrens" for "The Barrens"), built once from one <see cref="AreaTable"/>: the
/// owner keeps the index as long as the table is the same instance. Sub-areas are not zones.
/// </summary>
internal sealed class PlayerbotLfgZoneIndex
{
    private readonly Dictionary<string, uint> _zones = new(StringComparer.Ordinal);

    public PlayerbotLfgZoneIndex(AreaTable areas)
    {
        ArgumentNullException.ThrowIfNull(areas);
        foreach (AreaTemplate area in areas.All)
        {
            if (!area.IsZone || string.IsNullOrWhiteSpace(area.Name)) continue;
            string plain = PlayerbotWorldChatter.Normalize(area.Name);
            if (plain.Length < 4) continue;
            _zones.TryAdd(plain, area.Entry);
            if (plain.StartsWith("the ", StringComparison.Ordinal)) _zones.TryAdd(plain[4..], area.Entry);
        }
    }

    /// <summary>How many zones the index holds (tests).</summary>
    public int Count => _zones.Count;

    /// <summary>The zone with that plain name (lower case, letters, digits and spaces), or 0.</summary>
    public uint IdOf(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return 0;
        if (_zones.TryGetValue(plain, out uint id)) return id;
        return plain.StartsWith("the ", StringComparison.Ordinal) && _zones.TryGetValue(plain[4..], out id) ? id : 0;
    }
}

/// <summary>
/// Reading group and guild talk (docs/areas/playbots.md, Bot voice): what a heard line asks (<see cref="Read"/>), which dungeon, raid, quest
/// or zone a group search is about (<see cref="Resolve"/>). Pure: no world, no clock; the facts it resolves against are handed in. Lines are
/// read on the text <see cref="PlayerbotWorldChatter.Normalize"/> makes (lower case, letters, digits and apostrophes).
/// </summary>
internal static partial class PlayerbotLfgIntents
{
    private const int MaxLineLength = 255;

    /// <summary>The longest run of words tried as a zone name ("swamp of sorrows", "eastern plaguelands").</summary>
    private const int MaxZoneWords = 4;

    private static readonly HashSet<string> s_stop = new(StringComparer.Ordinal)
    {
        "any", "anyone", "any1", "anybody", "someone", "group", "groups", "grp", "raid", "raids", "run", "runs", "for", "to", "doing", "in", "going",
        "at", "a", "an", "the", "is", "there", "lf", "lfg", "lfm", "looking", "inv", "invite", "please", "pls", "plz", "and", "do", "want", "wanna",
        "up", "go", "dungeon", "dungeons", "instance", "lvl", "level", "need", "more", "can", "who", "pst", "whisper", "me", "x", "running",
        "tank", "healer", "heals", "heal", "dps", "dd", "mt", "damage",
        "warrior", "paladin", "hunter", "rogue", "priest", "shaman", "mage", "warlock", "druid",
    };

    /// <summary>What the line asks, and (for an offer to join) the role it states: tank, healer or damage; null when it states none. Class words state no role.</summary>
    /// <param name="text">The line as typed.</param>
    /// <param name="whisper">Whether it was whispered (to a poster): the whisper form of the invite words applies, bare "me", "x" and "1" included; in a channel only the stricter form does.</param>
    /// <param name="role">The stated role (an <see cref="PlayerbotLfgIntent.Available"/> line only).</param>
    internal static PlayerbotLfgIntent Read(string text, bool whisper, out BotChatRole? role)
    {
        role = null;
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLineLength) return PlayerbotLfgIntent.None;
        string line = PlayerbotWorldChatter.Normalize(text);
        if (line.Length == 0 || PlayerbotBuiltinChat.IsAbusive(line)) return PlayerbotLfgIntent.None;
        if (GuildJoin().IsMatch(line)) return PlayerbotLfgIntent.GuildJoin;
        if (GuildSearch().IsMatch(line)) return PlayerbotLfgIntent.GuildSearch;

        // A player's own LFM is the world chatter's to answer, never a search.
        if (PlayerbotWorldChatter.Read(text) == WorldLineKind.LookingForMore) return PlayerbotLfgIntent.None;
        if (IsGroupSearch(line)) return PlayerbotLfgIntent.GroupSearch;

        bool available = whisper
            ? PlayerbotVoiceFacts.AsksInvite(text) || AvailableWhisper().IsMatch(line)
            : PlayerbotVoiceFacts.AsksInviteInChannel(text) || AvailableChannel().IsMatch(line);
        if (!available) return PlayerbotLfgIntent.None;
        role = StatedRole(line);
        return PlayerbotLfgIntent.Available;
    }

    /// <summary>Whether the line holds a word that makes it a call for a group, the cue the short dungeon abbreviations ("st", "dm", "sm") need to count.</summary>
    internal static bool HasGroupCue(string text)
        => !string.IsNullOrWhiteSpace(text) && Cue().IsMatch(PlayerbotWorldChatter.Normalize(text));

    /// <summary>
    /// What the line is about, against real state: a dungeon or raid by name (the one that suits <paramref name="askerLevel"/> when a name is
    /// ambiguous, "dm"; on a tie the one a bot waits for), else a quest title of a waiting goal or of the asker's log, else a zone by exact name;
    /// <see cref="PlayerbotLfgSubjectKind.None"/> when the line states no subject, <see cref="PlayerbotLfgSubjectKind.Unresolved"/> when it
    /// states one nothing matches.
    /// </summary>
    internal static PlayerbotLfgSubject Resolve(string text, int askerLevel, PlayerbotLfgFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLineLength) return new(PlayerbotLfgSubjectKind.None);
        string line = PlayerbotWorldChatter.Normalize(text);
        if (line.Length == 0) return new(PlayerbotLfgSubjectKind.None);
        bool cue = Cue().IsMatch(line);

        foreach ((string token, List<PlayerbotDungeon> named) in PlayerbotDungeons.Mentions(line))
        {
            if (PlayerbotDungeons.IsShortToken(token) && !cue) continue;
            PlayerbotDungeon dungeon = Choose(named, askerLevel, facts);
            return new(dungeon.Raid ? PlayerbotLfgSubjectKind.Raid : PlayerbotLfgSubjectKind.Dungeon, dungeon.MapId, Display: dungeon.Name);
        }

        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string stated = Stated(words);
        if (stated.Length >= 4 && QuestOf(line, stated, facts) is { } quest) return quest;
        if (ZoneOf(words, facts) is { } zone) return zone;
        return stated.Length == 0 ? new(PlayerbotLfgSubjectKind.None) : new(PlayerbotLfgSubjectKind.Unresolved, Display: stated);
    }

    private static bool IsGroupSearch(string line)
    {
        if (AnyGroup().IsMatch(line) || ThereIsGroup().IsMatch(line)) return true;
        if (AnyoneDoing().Match(line) is { Success: true } doing) return Names(doing.Groups["x"].Value, line);
        if (LookingFor().Match(line) is { Success: true } looking) return NamesDungeon(looking.Groups["x"].Value, line);
        return TrailingAnyone().Match(line) is { Success: true } trailing && NamesDungeon(trailing.Groups["x"].Value, line);
    }

    /// <summary>Whether the words after "anyone doing" state something: a dungeon, or a word of four letters or more that is no cue word.</summary>
    private static bool Names(string clause, string line)
    {
        if (NamesDungeon(clause, line)) return true;
        foreach (string word in clause.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (word.Length >= 4 && !s_stop.Contains(word)) return true;
        return false;
    }

    private static bool NamesDungeon(string clause, string line)
    {
        if (string.IsNullOrWhiteSpace(clause)) return false;
        bool cue = Cue().IsMatch(line);
        foreach ((string token, _) in PlayerbotDungeons.Mentions(clause))
            if (cue || !PlayerbotDungeons.IsShortToken(token)) return true;
        return false;
    }

    /// <summary>The role a line states ("i can tank", "inv, healer"): tank, healer or damage; null when it states none. Class words state no role.</summary>
    internal static BotChatRole? RoleOf(string text)
        => string.IsNullOrWhiteSpace(text) || text.Length > MaxLineLength ? null : StatedRole(PlayerbotWorldChatter.Normalize(text));

    private static BotChatRole? StatedRole(string line)
    {
        Match match = Role().Match(line);
        if (!match.Success) return null;
        if (match.Groups["tank"].Success) return BotChatRole.Tank;
        return match.Groups["heal"].Success ? BotChatRole.Healer : BotChatRole.Damage;
    }

    /// <summary>The dungeon among those a word names: the one that fits the level, else one a bot waits for, else the nearest range.</summary>
    private static PlayerbotDungeon Choose(List<PlayerbotDungeon> named, int level, PlayerbotLfgFacts facts)
    {
        if (named.Count == 1) return named[0];
        PlayerbotDungeon? fitting = null;
        int fits = 0;
        foreach (PlayerbotDungeon dungeon in named)
            if (PlayerbotDungeons.Fits(dungeon, level) && fits++ == 0) fitting = dungeon;
        if (fits == 1) return fitting!;
        foreach (PlayerbotDungeon dungeon in named)
            foreach (PlayerbotLfgGoal goal in facts.Goals)
                if (goal.Kind is PlayerbotGroupGoalKind.Dungeon or PlayerbotGroupGoalKind.Raid && goal.Key == dungeon.MapId) return dungeon;
        if (fitting is not null) return fitting;
        PlayerbotDungeon nearest = named[0];
        int best = int.MaxValue;
        foreach (PlayerbotDungeon dungeon in named)
        {
            int distance = level < dungeon.MinLevel ? dungeon.MinLevel - level : level > dungeon.MaxLevel ? level - dungeon.MaxLevel : 0;
            if (distance < best)
            {
                best = distance;
                nearest = dungeon;
            }
        }

        return nearest;
    }

    /// <summary>The words that state a subject: no cue word, number or class, in order.</summary>
    private static string Stated(string[] words)
    {
        var kept = new List<string>(words.Length);
        foreach (string word in words)
        {
            if (s_stop.Contains(word) || IsNumber(word)) continue;
            kept.Add(word);
        }

        return string.Join(' ', kept);
    }

    private static bool IsNumber(string word)
    {
        foreach (char c in word)
            if (c is < '0' or > '9') return false;
        return true;
    }

    private static PlayerbotLfgSubject? QuestOf(string line, string stated, PlayerbotLfgFacts facts)
    {
        uint questId = 0, key = 0;
        string title = string.Empty;
        foreach (PlayerbotLfgGoal goal in facts.Goals)
        {
            if (goal.QuestId == 0 || string.IsNullOrEmpty(goal.QuestTitle)) continue;
            if (!Matches(goal.QuestTitle!, line, stated)) continue;
            return new(PlayerbotLfgSubjectKind.Quest, goal.Key, goal.QuestId, Display: goal.QuestTitle!);
        }

        foreach ((uint id, string name) in facts.AskerQuests)
        {
            if (id == 0 || string.IsNullOrEmpty(name) || !Matches(name, line, stated)) continue;
            questId = id;
            title = name;
            break;
        }

        if (questId == 0) return null;
        foreach (PlayerbotLfgGoal goal in facts.Goals)
            if (goal.QuestId == questId) key = goal.Key;
        return new(PlayerbotLfgSubjectKind.Quest, key, questId, Display: title);
    }

    /// <summary>The title is said whole in the line, or what the line states (four letters or more) is part of the title.</summary>
    private static bool Matches(string title, string line, string stated)
    {
        string plain = PlayerbotWorldChatter.Normalize(title);
        if (plain.Length < 4) return false;
        return ContainsPhrase(line, plain) || plain.Contains(stated, StringComparison.Ordinal);
    }

    private static bool ContainsPhrase(string line, string phrase)
    {
        int at = line.IndexOf(phrase, StringComparison.Ordinal);
        while (at >= 0)
        {
            bool starts = at == 0 || line[at - 1] == ' ';
            int end = at + phrase.Length;
            if (starts && (end == line.Length || line[end] == ' ')) return true;
            at = line.IndexOf(phrase, at + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static PlayerbotLfgSubject? ZoneOf(string[] words, PlayerbotLfgFacts facts)
    {
        for (int length = Math.Min(MaxZoneWords, words.Length); length >= 1; length--)
            for (int start = 0; start + length <= words.Length; start++)
            {
                string name = length == 1 ? words[start] : string.Join(' ', words, start, length);
                if (name.Length < 4) continue;
                uint id = facts.ZoneIdOf(name);
                if (id != 0) return new(PlayerbotLfgSubjectKind.Zone, ZoneId: id, Display: name);
            }

        return null;
    }

    [GeneratedRegex(@"\bginv\b|\bg inv(ite)?\b|\bguild inv(ite)?\b|\b(can i|could i|may i|want to|wanna|like to) join (?:(?:your|ur|the) )?guild\b")]
    private static partial Regex GuildJoin();

    [GeneratedRegex(@"\b(any|looking for(?: a)?|lf|lfg) guilds?\b|\bguilds? (recruiting|lf members|recruitment)\b")]
    private static partial Regex GuildSearch();

    [GeneratedRegex(@"\bany(?:one)? (?:groups?|grp|raids?|runs?)\b")]
    private static partial Regex AnyGroup();

    [GeneratedRegex(@"\bis there (?:a|an|any) (?:group|grp|raid|run)\b")]
    private static partial Regex ThereIsGroup();

    [GeneratedRegex(@"\banyone (?:doing|running|going(?: to)?|up for|wants? to do|wanna do|for)\b(?<x>.*)$")]
    private static partial Regex AnyoneDoing();

    [GeneratedRegex(@"\b(?:lf|lfg|looking for(?: a)?)(?! (?:tanks?|healers?|heals?|dps|dd|mt)\b)(?: (?:group|grp|raid))?(?: for)?\b(?<x>.*)$")]
    private static partial Regex LookingFor();

    [GeneratedRegex(@"^(?<x>.+) any(?:one|1)$")]
    private static partial Regex TrailingAnyone();

    [GeneratedRegex(@"\b(any|anyone|any1|lf\d?m?|lfg|lfm|group|groups|grp|run|runs|doing|inv|raid|raids)\b")]
    private static partial Regex Cue();

    [GeneratedRegex(@"\bi('?m| am) (available|free|up for it)\b|\bneed( a)? (tank|healer|heals?|dps|dd)\b|\bstill (looking|need)\b")]
    private static partial Regex AvailableWhisper();

    [GeneratedRegex(@"\bi('?m| am) (available|free|up for it)\b")]
    private static partial Regex AvailableChannel();

    [GeneratedRegex(@"\b(?<tank>tank|mt)\b|\b(?<heal>heals?|healer)\b|\b(?<dps>dps|dd|damage)\b")]
    private static partial Regex Role();
}
