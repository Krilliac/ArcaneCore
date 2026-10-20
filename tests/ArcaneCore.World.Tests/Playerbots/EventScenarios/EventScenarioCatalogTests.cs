using ArcaneCore.Data.World.WorldState;
using ArcaneCore.World.Playerbots.EventScenarios;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.EventScenarios;

/// <summary>The validation of the scenario rows: a bad row is logged and left out, never thrown, and never takes a good one with it.</summary>
public sealed class EventScenarioCatalogTests
{
    private static GameEventPlayerbotGroupRow Group(uint eventId = 5, uint group = 1, string prefix = "Guard", int count = 4, byte team = 1, string spec = "2:1,5:5",
        byte level = 60, byte role = 1) => new()
    {
        Event = eventId, GroupId = group, NamePrefix = prefix, Count = count, Team = team, ClassSpec = spec, Level = level, Provision = true,
        StartMap = 0, StartX = 1, StartY = 2, StartZ = 3, StartO = 0, Role = role,
    };

    private static GameEventPlayerbotPostRow Post(uint eventId = 5, uint group = 1, uint post = 1, float radius = 40f) => new()
    {
        Event = eventId, GroupId = group, PostId = post, Map = 0, X = 1, Y = 2, Z = 3, Radius = radius,
    };

    private static GameEventPlayerbotSiegeRow Siege(uint eventId = 5, uint group = 1, byte raids = 2, byte ready = 100, uint objective = 10181) => new()
    {
        Event = eventId, GroupId = group, ObjectiveEntry = objective, Raids = raids, MinReadyPct = ready,
    };

    private static GameEventPlayerbotWaypointRow Way(uint seq, uint map = 0, uint eventId = 5, uint group = 1, float x = 1) => new()
    {
        Event = eventId, GroupId = group, Seq = seq, Map = map, X = x, Y = 2, Z = 3,
    };

    private static EventScenarioCatalog Build(GameEventPlayerbotContent content, Func<uint, bool>? known = null)
        => EventScenarioCatalog.Build(content, known ?? (id => id == 5));

    private static GameEventPlayerbotContent Content(IEnumerable<GameEventPlayerbotGroupRow> groups, IEnumerable<GameEventPlayerbotPostRow>? posts = null,
        IEnumerable<GameEventPlayerbotSiegeRow>? sieges = null, IEnumerable<GameEventPlayerbotWaypointRow>? ways = null)
        => new([.. groups], [.. posts ?? []], [.. sieges ?? []], [.. ways ?? []]);

    [Fact]
    public void AValidDefendGroupAndSiegeGroup_Load_WithTheirPostsWaypointsAndDefaults()
    {
        EventScenarioCatalog catalog = Build(Content(
            [Group(group: 1, prefix: "Guard"), Group(group: 2, prefix: "Raider", team: 0, spec: "1:1,3:2", role: 2)],
            [Post(post: 2, radius: 5f), Post(post: 1, radius: 9999f)],
            [Siege(group: 2, ready: 0)],
            [Way(2, x: 20, group: 2), Way(1, x: 10, group: 2)]));

        Assert.Empty(catalog.Issues);
        Assert.Equal([(ushort)5], catalog.Events);
        IReadOnlyList<EventScenarioGroup> groups = catalog.GroupsOf(5);
        Assert.Equal(["Guard", "Raider"], groups.Select(g => g.NamePrefix));
        Assert.Equal([1u, 2u], groups[0].Posts.Select(p => p.PostId));
        Assert.Equal([200f, 10f], groups[0].Posts.Select(p => p.Radius)); // clamped as .playerbot defend clamps
        Assert.Equal(2, groups[0].ClassSpec.Count);
        EventScenarioSiege siege = groups[1].Siege!;
        Assert.Equal((10181u, 2, 100), (siege.ObjectiveEntry, siege.Raids, siege.MinReadyPct)); // ready 0 means 100
        Assert.Equal([10f, 20f], siege.Waypoints.Select(w => w.X));
        Assert.Empty(catalog.GroupsOf(6));
    }

    public static TheoryData<string, GameEventPlayerbotContent> Rejections() => new()
    {
        { "no such entry", Content([Group(eventId: 6)], [Post(eventId: 6)]) },
        { "name_prefix", Content([Group(prefix: "G")], [Post()]) },
        { "name_prefix ", Content([Group(prefix: "Guard1")], [Post()]) },
        { "name_prefix  ", Content([Group(prefix: "Toolongpfx")], [Post()]) },
        { "count must be", Content([Group(count: 0)], [Post()]) },
        { "count must be ", Content([Group(count: 501)], [Post()]) },
        { "team must be", Content([Group(team: 2)], [Post()]) },
        { "level must be", Content([Group(level: 70)], [Post()]) },
        { "role must be", Content([Group(role: 3)], []) },
        { "class_spec", Content([Group(spec: "")], [Post()]) },
        { "class_spec ", Content([Group(spec: "2:1,x")], [Post()]) },
        { "not on team", Content([Group(team: 1, spec: "1:1")], [Post()]) },
        { "not on team ", Content([Group(team: 0, spec: "2:1")], [Post(), Post(post: 2)]) },
        { "needs at least one valid post", Content([Group()], []) },
        { "needs a game_event_playerbot_siege row", Content([Group(role: 2)], [], [], [Way(1)]) },
        { "needs waypoint rows", Content([Group(role: 2)], [], [Siege()], []) },
        { "waypoints are on more than one map", Content([Group(role: 2)], [], [Siege()], [Way(1, map: 0), Way(2, map: 1)]) },
        { "is not the waypoints' map", Content([Group(role: 2)], [], [Siege()], [Way(1, map: 1)]) },
        { "raids must be", Content([Group(role: 2)], [], [Siege(raids: 3)], [Way(1)]) },
        { "objective_entry is 0", Content([Group(role: 2)], [], [Siege(objective: 0)], [Way(1)]) },
        { "group id must be", Content([Group(group: 300)], [Post(group: 300)]) },
    };

    [Theory]
    [MemberData(nameof(Rejections))]
    public void ABadGroup_IsLeftOutWithAnIssue_AndTheGoodGroupNextToItStillLoads(string issueFragment, GameEventPlayerbotContent bad)
    {
        GameEventPlayerbotContent good = Content([Group(group: 9, prefix: "Fine")], [Post(group: 9)]);
        var merged = new GameEventPlayerbotContent([.. bad.Groups, .. good.Groups], [.. bad.Posts, .. good.Posts], [.. bad.Sieges], [.. bad.Waypoints]);

        EventScenarioCatalog catalog = Build(merged);

        Assert.Contains(catalog.Issues, issue => issue.Contains(issueFragment.Trim(), StringComparison.Ordinal));
        Assert.Equal(["Fine"], catalog.GroupsOf(5).Select(g => g.NamePrefix));
    }

    [Fact]
    public void ADuplicateKey_RowsWithNoGroup_AndABadPost_AreReportedWithoutThrowing()
    {
        EventScenarioCatalog catalog = Build(Content(
            [Group(), Group()],
            [Post(), Post(group: 77), new GameEventPlayerbotPostRow { Event = 5, GroupId = 1, PostId = 2, X = float.NaN, Radius = 40 }],
            [Siege(group: 77)],
            [Way(1, group: 77)]));

        Assert.Single(catalog.GroupsOf(5));
        Assert.Single(catalog.GroupsOf(5)[0].Posts);
        Assert.Contains(catalog.Issues, i => i.Contains("duplicate key", StringComparison.Ordinal));
        Assert.Contains(catalog.Issues, i => i.Contains("post 2: not a finite position", StringComparison.Ordinal));
        Assert.Contains(catalog.Issues, i => i.Contains("game_event_playerbot_post event 5 group 77", StringComparison.Ordinal));
        Assert.Contains(catalog.Issues, i => i.Contains("game_event_playerbot_siege event 5 group 77", StringComparison.Ordinal));
        Assert.Contains(catalog.Issues, i => i.Contains("game_event_playerbot_waypoint event 5 group 77", StringComparison.Ordinal));
    }

    [Fact]
    public void ClassSpec_ParsesPairs_AndRefusesTheOtherTeamsRacesAndClassesTheGameDoesNotHave()
    {
        Assert.True(EventScenarioClassSpec.TryParse(" 2:1 , 5:5,6:11 ", 1, out IReadOnlyList<EventScenarioClassSlot> spec, out _));
        Assert.Equal([new EventScenarioClassSlot(2, 1), new EventScenarioClassSlot(5, 5), new EventScenarioClassSlot(6, 11)], spec);
        Assert.False(EventScenarioClassSpec.TryParse("1:1", 1, out _, out string? other));
        Assert.Contains("not on team 1", other);
        Assert.False(EventScenarioClassSpec.TryParse("2:6", 1, out _, out _)); // class 6 (death knight) is not in 1.12
        Assert.False(EventScenarioClassSpec.TryParse("2:10", 1, out _, out _));
        Assert.False(EventScenarioClassSpec.TryParse("2", 1, out _, out _));
        Assert.False(EventScenarioClassSpec.TryParse(null, 1, out _, out _));
    }

    [Fact]
    public void PostIds_AreStable_UniquePerPost_AndAboveTheCounterOfTheGmDefendCommand()
    {
        int a = EventScenarioPostIds.For(900, 2, 1);
        Assert.Equal(a, EventScenarioPostIds.For(900, 2, 1));
        var ids = new HashSet<int>();
        foreach (ushort eventId in new ushort[] { 1, 900, 4095 })
            for (uint group = 0; group < 10; group++)
                for (uint post = 0; post < 10; post++)
                    Assert.True(ids.Add(EventScenarioPostIds.For(eventId, group, post)));
        Assert.All(ids, id => Assert.True(id >= EventScenarioPostIds.Base && id > 1_000_000_000));
    }

    [Fact]
    public void GeneratedNames_ArePrefixPlusFourLetters_LettersOnly_NeverThreeAlike_AndDistinct()
    {
        int index = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string prefix in new[] { "Siegebot", "Uccanal", "Ucgate", "Ucroyal", "Ucltop", "Uclbot", "Mmm", "Tt" })
            for (int i = 0; i < 200; i++)
            {
                string name = EventScenarioNames.For(prefix, ref index);
                Assert.StartsWith(prefix, name, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(prefix.Length + EventScenarioNames.SuffixLength, name.Length);
                Assert.True(name.Length <= 12 && name.All(char.IsAsciiLetter), name);
                Assert.True(char.IsUpper(name[0]) && name.Skip(1).All(char.IsLower), name);
                for (int k = Math.Max(2, prefix.Length); k < name.Length; k++) Assert.False(name[k] == name[k - 1] && name[k] == name[k - 2], name);
                names.Add(name);
            }

        Assert.True(names.Count > 1000);
    }
}
