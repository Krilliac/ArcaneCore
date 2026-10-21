using System.Globalization;
using System.Numerics;
using ArcaneCore.Data.World.WorldState;

namespace ArcaneCore.World.Playerbots.EventScenarios;

/// <summary>What the bots of a scenario group do once they stand at their start (<c>game_event_playerbot_group.role</c>).</summary>
internal enum EventScenarioRole : byte
{
    /// <summary>Nothing: the bots are created, levelled and placed, then left to their own play.</summary>
    None = 0,

    /// <summary>Each bot holds one of the group's defend posts (<c>.playerbot defend</c>).</summary>
    Defend = 1,

    /// <summary>The group is the raid of a siege (<c>.playerbot siege</c>).</summary>
    Siege = 2,
}

/// <summary>A race and class a scenario group's bot is created as (one entry of <c>class_spec</c>).</summary>
internal readonly record struct EventScenarioClassSlot(byte Race, byte Class);

/// <summary>A defend post of a scenario group (<c>game_event_playerbot_post</c>).</summary>
internal sealed record EventScenarioPost(uint PostId, uint Map, Vector3 Centre, float Radius);

/// <summary>The siege of a scenario group (<c>game_event_playerbot_siege</c> and its waypoints).</summary>
internal sealed record EventScenarioSiege(uint ObjectiveEntry, int Raids, int MinReadyPct, uint Map, IReadOnlyList<Vector3> Waypoints);

/// <summary>One validated bot group of a game event (<c>game_event_playerbot_group</c> with its posts, siege and waypoints).</summary>
internal sealed record EventScenarioGroup(
    ushort EventId,
    uint GroupId,
    string NamePrefix,
    int Count,
    byte Team,
    IReadOnlyList<EventScenarioClassSlot> ClassSpec,
    int Level,
    bool Provision,
    uint StartMap,
    Vector3 Start,
    float StartOrientation,
    EventScenarioRole Role,
    IReadOnlyList<EventScenarioPost> Posts,
    EventScenarioSiege? Siege,
    string? Comment);

/// <summary>
/// The scenarios of the world database, validated: the rows of <c>game_event_playerbot_*</c> turned into groups, each bad row logged and
/// left out (never thrown). Immutable once built; any thread.
/// </summary>
internal sealed class EventScenarioCatalog
{
    /// <summary>The most bots a group may ask for (a typo guard; the bot registry and the running cap bound it as well).</summary>
    internal const int MaxGroupCount = 500;

    /// <summary>The longest name prefix: a generated suffix of <see cref="EventScenarioNames.SuffixLength"/> letters must still fit a 12 letter name.</summary>
    internal const int MaxPrefixLength = 8;

    /// <summary>The shortest name prefix.</summary>
    internal const int MinPrefixLength = 2;

    /// <summary>The highest level a group may be boosted to (the 1.12 cap).</summary>
    internal const int MaxLevel = 60;

    // Post ids pack the event (12 bits), the group (8) and the post (8), so a group's posts get a stable id (see EventScenarioPostIds).
    internal const uint MaxEventId = 0xFFF;
    internal const uint MaxGroupId = 0xFF;
    internal const uint MaxPostId = 0xFF;

    private static readonly EventScenarioGroup[] s_none = [];

    private readonly Dictionary<ushort, EventScenarioGroup[]> _byEvent;

    private EventScenarioCatalog(Dictionary<ushort, EventScenarioGroup[]> byEvent, IReadOnlyList<string> issues)
    {
        _byEvent = byEvent;
        Issues = issues;
    }

    /// <summary>No scenarios.</summary>
    internal static EventScenarioCatalog Empty { get; } = new([], []);

    /// <summary>What was left out and why, one line per problem.</summary>
    internal IReadOnlyList<string> Issues { get; }

    /// <summary>The events that have at least one valid group, in ascending order.</summary>
    internal IEnumerable<ushort> Events => _byEvent.Keys.Order();

    /// <summary>The number of valid groups.</summary>
    internal int GroupCount => _byEvent.Values.Sum(g => g.Length);

    /// <summary>The groups of an event in group order (empty when it has none). Allocation free.</summary>
    internal IReadOnlyList<EventScenarioGroup> GroupsOf(ushort eventId) => _byEvent.TryGetValue(eventId, out EventScenarioGroup[]? groups) ? groups : s_none;

    /// <summary>Whether the event has a scenario.</summary>
    internal bool HasEvent(ushort eventId) => _byEvent.ContainsKey(eventId);

    /// <summary>
    /// Validate <paramref name="content"/>. <paramref name="knownEvent"/> says whether <c>game_event</c> has the entry. Every rejected row
    /// adds an issue line; the rest still load.
    /// </summary>
    internal static EventScenarioCatalog Build(GameEventPlayerbotContent content, Func<uint, bool> knownEvent)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(knownEvent);
        var issues = new List<string>();
        var built = new Dictionary<ushort, List<EventScenarioGroup>>();
        var seen = new HashSet<(uint, uint)>();
        foreach (GameEventPlayerbotGroupRow row in content.Groups)
        {
            string at = string.Create(CultureInfo.InvariantCulture, $"game_event_playerbot_group event {row.Event} group {row.GroupId}");
            if (!seen.Add((row.Event, row.GroupId))) { issues.Add($"{at}: duplicate key, ignored"); continue; }
            if (Validate(row, content, knownEvent, at, issues) is not { } group) continue;
            if (!built.TryGetValue(group.EventId, out List<EventScenarioGroup>? list)) built[group.EventId] = list = [];
            list.Add(group);
        }

        // Rows that name no group at all are worth a line too: a typo in group_id would otherwise just do nothing.
        var groupKeys = new HashSet<(uint, uint)>(content.Groups.Select(g => (g.Event, g.GroupId)));
        foreach (GameEventPlayerbotPostRow post in content.Posts.Where(p => !groupKeys.Contains((p.Event, p.GroupId))))
            issues.Add(string.Create(CultureInfo.InvariantCulture, $"game_event_playerbot_post event {post.Event} group {post.GroupId} post {post.PostId}: no such group, ignored"));
        foreach (GameEventPlayerbotSiegeRow siege in content.Sieges.Where(s => !groupKeys.Contains((s.Event, s.GroupId))))
            issues.Add(string.Create(CultureInfo.InvariantCulture, $"game_event_playerbot_siege event {siege.Event} group {siege.GroupId}: no such group, ignored"));
        foreach (GameEventPlayerbotWaypointRow way in content.Waypoints.Where(w => !groupKeys.Contains((w.Event, w.GroupId))))
            issues.Add(string.Create(CultureInfo.InvariantCulture, $"game_event_playerbot_waypoint event {way.Event} group {way.GroupId} seq {way.Seq}: no such group, ignored"));

        var byEvent = built.ToDictionary(p => p.Key, p => p.Value.OrderBy(g => g.GroupId).ToArray());
        return new EventScenarioCatalog(byEvent, issues);
    }

    private static EventScenarioGroup? Validate(GameEventPlayerbotGroupRow row, GameEventPlayerbotContent content, Func<uint, bool> knownEvent, string at, List<string> issues)
    {
        if (row.Event is 0 or > MaxEventId) { issues.Add($"{at}: the event must be 1..{MaxEventId}, skipped"); return null; }
        if (!knownEvent(row.Event)) { issues.Add($"{at}: game_event has no such entry, skipped"); return null; }
        if (row.GroupId > MaxGroupId) { issues.Add($"{at}: the group id must be 0..{MaxGroupId}, skipped"); return null; }
        string prefix = row.NamePrefix.Trim();
        if (prefix.Length is < MinPrefixLength or > MaxPrefixLength || !prefix.All(char.IsAsciiLetter))
        {
            issues.Add($"{at}: name_prefix must be {MinPrefixLength}..{MaxPrefixLength} letters, skipped");
            return null;
        }

        if (row.Count is < 1 or > MaxGroupCount) { issues.Add($"{at}: count must be 1..{MaxGroupCount}, skipped"); return null; }
        if (row.Team > 1) { issues.Add($"{at}: team must be 0 (Alliance) or 1 (Horde), skipped"); return null; }
        if (row.Level > MaxLevel) { issues.Add($"{at}: level must be 0..{MaxLevel}, skipped"); return null; }
        if (row.Role > (byte)EventScenarioRole.Siege) { issues.Add($"{at}: role must be 0, 1 or 2, skipped"); return null; }
        if (!float.IsFinite(row.StartX) || !float.IsFinite(row.StartY) || !float.IsFinite(row.StartZ) || !float.IsFinite(row.StartO))
        {
            issues.Add($"{at}: the start is not a finite position, skipped");
            return null;
        }

        if (!EventScenarioClassSpec.TryParse(row.ClassSpec, row.Team, out IReadOnlyList<EventScenarioClassSlot> spec, out string? specProblem))
        {
            issues.Add($"{at}: class_spec {specProblem}, skipped");
            return null;
        }

        var role = (EventScenarioRole)row.Role;
        EventScenarioPost[] posts = [];
        EventScenarioSiege? siege = null;
        if (role == EventScenarioRole.Defend)
        {
            posts = [.. content.Posts.Where(p => p.Event == row.Event && p.GroupId == row.GroupId).Select(p => ToPost(p, issues)).OfType<EventScenarioPost>().OrderBy(p => p.PostId)];
            if (posts.Length == 0) { issues.Add($"{at}: a defend group needs at least one valid post row, skipped"); return null; }
        }
        else if (role == EventScenarioRole.Siege)
        {
            siege = ToSiege(row, content, at, issues);
            if (siege is null) return null;
        }

        float orientation = row.StartO % MathF.Tau;
        if (orientation < 0) orientation += MathF.Tau;
        return new EventScenarioGroup((ushort)row.Event, row.GroupId, prefix, row.Count, row.Team, spec, row.Level, row.Provision, row.StartMap,
            new Vector3(row.StartX, row.StartY, row.StartZ), orientation, role, posts, siege, row.Comment);
    }

    private static EventScenarioPost? ToPost(GameEventPlayerbotPostRow row, List<string> issues)
    {
        string at = string.Create(CultureInfo.InvariantCulture, $"game_event_playerbot_post event {row.Event} group {row.GroupId} post {row.PostId}");
        if (row.PostId > MaxPostId) { issues.Add($"{at}: the post id must be 0..{MaxPostId}, skipped"); return null; }
        if (!float.IsFinite(row.X) || !float.IsFinite(row.Y) || !float.IsFinite(row.Z) || !float.IsFinite(row.Radius))
        {
            issues.Add($"{at}: not a finite position, skipped");
            return null;
        }

        // The radius is clamped the way .playerbot defend clamps it.
        return new EventScenarioPost(row.PostId, row.Map, new Vector3(row.X, row.Y, row.Z), PlayerbotDefend.ClampRadius(row.Radius));
    }

    private static EventScenarioSiege? ToSiege(GameEventPlayerbotGroupRow group, GameEventPlayerbotContent content, string at, List<string> issues)
    {
        GameEventPlayerbotSiegeRow? siege = content.Sieges.FirstOrDefault(s => s.Event == group.Event && s.GroupId == group.GroupId);
        if (siege is null) { issues.Add($"{at}: a siege group needs a game_event_playerbot_siege row, skipped"); return null; }
        if (siege.ObjectiveEntry == 0) { issues.Add($"{at}: the siege objective_entry is 0, skipped"); return null; }
        if (siege.Raids is < 1 or > 2) { issues.Add($"{at}: the siege raids must be 1 or 2, skipped"); return null; }
        GameEventPlayerbotWaypointRow[] way = [.. content.Waypoints.Where(w => w.Event == group.Event && w.GroupId == group.GroupId).OrderBy(w => w.Seq)];
        if (way.Length == 0) { issues.Add($"{at}: a siege group needs waypoint rows, skipped"); return null; }
        if (way.Length > Groups.PlayerbotGroupCoordinator.MaxSiegeWaypoints) { issues.Add($"{at}: at most {Groups.PlayerbotGroupCoordinator.MaxSiegeWaypoints} waypoints, skipped"); return null; }
        if (way.Select(w => w.Map).Distinct().Count() > 1) { issues.Add($"{at}: the waypoints are on more than one map, skipped"); return null; }
        if (way.Any(w => !float.IsFinite(w.X) || !float.IsFinite(w.Y) || !float.IsFinite(w.Z))) { issues.Add($"{at}: a waypoint is not a finite position, skipped"); return null; }
        if (way[0].Map != group.StartMap) { issues.Add($"{at}: the start map {group.StartMap} is not the waypoints' map {way[0].Map}, skipped"); return null; }
        int ready = siege.MinReadyPct == 0 ? 100 : Math.Min((int)siege.MinReadyPct, 100);
        return new EventScenarioSiege(siege.ObjectiveEntry, siege.Raids, ready, way[0].Map, [.. way.Select(w => new Vector3(w.X, w.Y, w.Z))]);
    }
}

/// <summary>The <c>class_spec</c> column: comma separated <c>race:class</c> pairs, cycled over a group's bots.</summary>
internal static class EventScenarioClassSpec
{
    // 1.12 races by team: Human 1, Dwarf 3, Night Elf 4, Gnome 7 (Alliance); Orc 2, Undead 5, Tauren 6, Troll 8 (Horde).
    private static bool IsTeamRace(byte race, byte team) => team == 0 ? race is 1 or 3 or 4 or 7 : race is 2 or 5 or 6 or 8;

    // The race and class pairs the 1.12 client offers (class ids: warrior 1, paladin 2, hunter 3, rogue 4, priest 5, shaman 7, mage 8, warlock 9, druid 11).
    private static bool IsPlayableClass(byte race, byte cls) => race switch
    {
        1 => cls is 1 or 2 or 4 or 5 or 8 or 9,
        2 => cls is 1 or 3 or 4 or 7 or 9,
        3 => cls is 1 or 2 or 3 or 4 or 5,
        4 => cls is 1 or 3 or 4 or 5 or 11,
        5 => cls is 1 or 4 or 5 or 8 or 9,
        6 => cls is 1 or 3 or 7 or 11,
        7 => cls is 1 or 4 or 8 or 9,
        8 => cls is 1 or 3 or 4 or 5 or 7 or 8,
        _ => false,
    };

    /// <summary>Parse <paramref name="text"/>; false with the problem in <paramref name="problem"/> when it is empty, malformed or names a race of the other team.</summary>
    internal static bool TryParse(string? text, byte team, out IReadOnlyList<EventScenarioClassSlot> spec, out string? problem)
    {
        spec = [];
        problem = null;
        if (string.IsNullOrWhiteSpace(text)) { problem = "is empty"; return false; }
        var slots = new List<EventScenarioClassSlot>();
        foreach (string entry in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !byte.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte race)
                || !byte.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte cls) || cls is not (1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 11))
            {
                problem = $"entry '{entry}' is not race:class";
                return false;
            }

            if (!IsTeamRace(race, team)) { problem = $"entry '{entry}' names a race that is not on team {team}"; return false; }
            if (!IsPlayableClass(race, cls)) { problem = $"entry '{entry}' is a race and class pair the game does not have"; return false; }
            slots.Add(new EventScenarioClassSlot(race, cls));
        }

        if (slots.Count == 0) { problem = "is empty"; return false; }
        spec = slots;
        return true;
    }
}

/// <summary>The post id of a scenario's post: stable, and in a range the GM <c>.playerbot defend</c> counter (1, 2, 3 ...) never reaches.</summary>
internal static class EventScenarioPostIds
{
    /// <summary>Bit 30: every scenario post id has it set.</summary>
    internal const int Base = 0x4000_0000;

    /// <summary>Whether <paramref name="postId"/> was made by <see cref="For"/> (the GM defend command counts up from 1).</summary>
    internal static bool IsScenario(int postId) => postId > 0 && (postId & Base) != 0;

    /// <summary>The id of post <paramref name="postId"/> of group <paramref name="groupId"/> of <paramref name="eventId"/> (ranges checked by the catalog).</summary>
    internal static int For(ushort eventId, uint groupId, uint postId)
        => Base | (int)(((uint)eventId & EventScenarioCatalog.MaxEventId) << 16) | (int)((groupId & EventScenarioCatalog.MaxGroupId) << 8) | (int)(postId & EventScenarioCatalog.MaxPostId);
}

/// <summary>The suffix of a generated bot name.</summary>
internal static class EventScenarioNames
{
    /// <summary>Letters after the prefix.</summary>
    internal const int SuffixLength = 4;

    private const string Consonants = "bdfgklmnprstvz";
    private const string Vowels = "aeiou";

    /// <summary>The number of distinct suffixes.</summary>
    internal const int Variants = 14 * 5 * 14 * 5;

    /// <summary>
    /// The name for suffix number <paramref name="index"/> (consonant, vowel, consonant, vowel: letters only, never three alike in a row
    /// inside the suffix; the first letter is moved on when it would make three alike with the prefix's last two).
    /// </summary>
    internal static string For(string prefix, ref int index)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        while (true)
        {
            int i = index % Variants;
            index++;
            char c1 = Consonants[i % 14];
            char v1 = Vowels[i / 14 % 5];
            char c2 = Consonants[i / 70 % 14];
            char v2 = Vowels[i / 980 % 5];
            string name = string.Create(prefix.Length + SuffixLength, (prefix, c1, v1, c2, v2), static (span, state) =>
            {
                state.prefix.AsSpan().CopyTo(span);
                span[state.prefix.Length] = state.c1;
                span[state.prefix.Length + 1] = state.v1;
                span[state.prefix.Length + 2] = state.c2;
                span[state.prefix.Length + 3] = state.v2;
            });
            if (!HasTriple(name, Math.Max(2, prefix.Length))) return char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
        }
    }

    private static bool HasTriple(string name, int from)
    {
        for (int i = from; i < name.Length; i++)
            if (char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(name[i - 1]) && char.ToLowerInvariant(name[i]) == char.ToLowerInvariant(name[i - 2]))
                return true;
        return false;
    }
}
