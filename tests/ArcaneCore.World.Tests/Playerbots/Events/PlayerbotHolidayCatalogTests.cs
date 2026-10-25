using ArcaneCore.Game.WorldState.Events;
using ArcaneCore.World.Playerbots.BotSocial;
using ArcaneCore.World.Playerbots.Events;
using Xunit;
using static ArcaneCore.World.Playerbots.BotSocial.PlayerbotSocialEmotes;

namespace ArcaneCore.World.Tests.Playerbots.Events;

/// <summary>
/// Which game events are festivals (P7a H2, design 4.1): a root has a holiday id (Call to Arms and the excluded events aside), a member
/// reaches a root through its <c>LinkedTo</c> chain, and one curated companion is not linked in the data. The ids and links are the ones of
/// the classic-db z2815 dump as the design lists them (events 12/24/25, 2/21, 1, 7, 8, 9, 10, 15, 33, 4/5, 18-20, 1024 and 6).
/// </summary>
public sealed class PlayerbotHolidayCatalogTests
{
    private static readonly IReadOnlySet<ushort> None = new HashSet<ushort>();

    private static GameEventDefinition Event(ushort id, uint holiday, string description = "event", ushort linkedTo = 0)
        => new(id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2045, 1, 1, 0, 0, 0, TimeSpan.Zero), 525_600, 1_440, holiday, description)
        {
            LinkedTo = linkedTo,
        };

    /// <summary>The z2815 rows the design names, with their holiday ids and links.</summary>
    private static List<GameEventDefinition> Z2815() =>
    [
        Event(1, 341, "Midsummer Fire Festival"),
        Event(2, 141, "Feast of Winter Veil"),
        Event(4, 374, "Darkmoon Faire (Elwynn)"),
        Event(5, 375, "Darkmoon Faire (Mulgore)"),
        Event(6, 0, "New Year's Eve"),
        Event(7, 327, "Lunar Festival"),
        Event(8, 335, "Love is in the Air"),
        Event(9, 181, "Noblegarden"),
        Event(10, 201, "Children's Week"),
        Event(12, 324, "Hallow's End"),
        Event(15, 301, "Stranglethorn Fishing Extravaganza"),
        Event(18, 283, "Call to Arms: Alterac Valley"),
        Event(19, 284, "Call to Arms: Warsong Gulch"),
        Event(20, 285, "Call to Arms: Arathi Basin"),
        Event(21, 0, "Winter Veil: Gifts"),
        Event(24, 0, "Wickerman Ember's", linkedTo: 12),
        Event(25, 0, "Banshee Queen", linkedTo: 12),
        Event(33, 321, "Harvest Festival"),
        Event(120, 0, "War Effort: gathering"),
        Event(1024, 0, "Hourly Bells"),
    ];

    private static Func<ushort, GameEventDefinition?> Finder(IEnumerable<GameEventDefinition> events)
    {
        Dictionary<ushort, GameEventDefinition> map = events.ToDictionary(e => e.Id);
        return id => map.GetValueOrDefault(id);
    }

    // --- roots -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(33)]
    public void AnEventWithAHolidayId_IsARoot(ushort id)
        => Assert.True(PlayerbotHolidayCatalog.IsRoot(Z2815().Single(e => e.Id == id), None));

    [Theory]
    [InlineData(1024, "the hourly bells")]
    [InlineData(6, "New Year's Eve has holiday 0 in the data")]
    [InlineData(120, "a war-effort phase")]
    [InlineData(21, "Winter Veil: Gifts has holiday 0")]
    [InlineData(24, "a linked sub-event has holiday 0")]
    [InlineData(18, "Call to Arms: Alterac Valley (holiday 283)")]
    [InlineData(19, "Call to Arms: Warsong Gulch (holiday 284)")]
    [InlineData(20, "Call to Arms: Arathi Basin (holiday 285)")]
    public void HolidayZeroAndCallToArms_AreNotRoots(ushort id, string why)
    {
        Assert.False(PlayerbotHolidayCatalog.IsRoot(Z2815().Single(e => e.Id == id), None), why);
    }

    [Fact]
    public void AnExcludedEvent_IsNotARoot_AndTakesItsMembersWithIt()
    {
        List<GameEventDefinition> events = Z2815();
        IReadOnlySet<ushort> excluded = new HashSet<ushort> { 12 };

        Assert.False(PlayerbotHolidayCatalog.IsRoot(events.Single(e => e.Id == 12), excluded));
        Assert.True(PlayerbotHolidayCatalog.IsRoot(events.Single(e => e.Id == 2), excluded)); // the others stay
        Assert.Null(PlayerbotHolidayCatalog.RootOf(12, Finder(events), excluded));
        Assert.Null(PlayerbotHolidayCatalog.RootOf(24, Finder(events), excluded));
        Assert.Null(PlayerbotHolidayCatalog.RootOf(25, Finder(events), excluded));

        // Excluding a sub-event only drops that one: its root is still a festival.
        Assert.Null(PlayerbotHolidayCatalog.RootOf(24, Finder(events), new HashSet<ushort> { 24 }));
        Assert.Equal((ushort)12, PlayerbotHolidayCatalog.RootOf(25, Finder(events), new HashSet<ushort> { 24 }));
        Assert.Equal((ushort)12, PlayerbotHolidayCatalog.RootOf(12, Finder(events), new HashSet<ushort> { 24 }));
    }

    // --- members ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ARootIsItsOwnRoot_AndTheHallowsEndSubEventsReachIt()
    {
        Func<ushort, GameEventDefinition?> find = Finder(Z2815());

        Assert.Equal((ushort)12, PlayerbotHolidayCatalog.RootOf(12, find, None));
        Assert.Equal((ushort)12, PlayerbotHolidayCatalog.RootOf(24, find, None));
        Assert.Equal((ushort)12, PlayerbotHolidayCatalog.RootOf(25, find, None));
        Assert.Equal((ushort)2, PlayerbotHolidayCatalog.RootOf(2, find, None));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(6)]
    [InlineData(120)]
    [InlineData(21)] // linkedTo 0 in the data: the companion list, not the link, ties it to Winter Veil
    [InlineData(18)]
    [InlineData(999)] // not an event at all
    public void NoOtherEvent_ReachesARoot(ushort id)
        => Assert.Null(PlayerbotHolidayCatalog.RootOf(id, Finder(Z2815()), None));

    [Fact]
    public void ALinkChain_IsFollowedForFourLinks_AndNoFurther()
    {
        // 5 -> 4 -> 3 -> 2 -> 1 (the root): the fourth link reaches it. 6 -> 5 needs a fifth.
        var events = new List<GameEventDefinition>
        {
            Event(1, 324),
            Event(2, 0, linkedTo: 1), Event(3, 0, linkedTo: 2), Event(4, 0, linkedTo: 3), Event(5, 0, linkedTo: 4), Event(6, 0, linkedTo: 5),
        };
        Func<ushort, GameEventDefinition?> find = Finder(events);

        Assert.Equal(4, PlayerbotHolidayCatalog.MaxLinks);
        Assert.Equal((ushort)1, PlayerbotHolidayCatalog.RootOf(2, find, None));
        Assert.Equal((ushort)1, PlayerbotHolidayCatalog.RootOf(5, find, None));
        Assert.Null(PlayerbotHolidayCatalog.RootOf(6, find, None));
    }

    [Fact]
    public void ALinkCycle_Terminates_WithoutARoot()
    {
        var events = new List<GameEventDefinition>
        {
            Event(30, 0, linkedTo: 31), Event(31, 0, linkedTo: 30), Event(32, 0, linkedTo: 32), Event(33, 0, linkedTo: 99),
        };
        Func<ushort, GameEventDefinition?> find = Finder(events);

        Assert.Null(PlayerbotHolidayCatalog.RootOf(30, find, None));
        Assert.Null(PlayerbotHolidayCatalog.RootOf(31, find, None));
        Assert.Null(PlayerbotHolidayCatalog.RootOf(32, find, None)); // linked to itself
        Assert.Null(PlayerbotHolidayCatalog.RootOf(33, find, None)); // linked to an event that does not exist
    }

    [Fact]
    public void AMemberCountsOnlyWhileItsRootRuns()
    {
        List<GameEventDefinition> events = Z2815();

        // Hallow's End is not running, but its sub-events start (the vmangos dialect has no linkedTo to stop them): not members.
        Assert.Empty(PlayerbotHolidayCatalog.ActiveMembers(events, new HashSet<ushort> { 24, 25 }, None));

        // Hallow's End runs, and so does 24: both are members of root 12; 25 is not running.
        IReadOnlyDictionary<ushort, ushort> members = PlayerbotHolidayCatalog.ActiveMembers(events, new HashSet<ushort> { 12, 24 }, None);
        Assert.Equal(new Dictionary<ushort, ushort> { [12] = 12, [24] = 12 }, members.ToDictionary(p => p.Key, p => p.Value));

        members = PlayerbotHolidayCatalog.ActiveMembers(events, new HashSet<ushort> { 12, 24, 25 }, None);
        Assert.Equal(new Dictionary<ushort, ushort> { [12] = 12, [24] = 12, [25] = 12 }, members.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void TheActiveMembers_LeaveOutTheBellsTheDayEventsTheWarEffortAndCallToArms()
    {
        List<GameEventDefinition> events = Z2815();

        IReadOnlyDictionary<ushort, ushort> members = PlayerbotHolidayCatalog.ActiveMembers(events,
            new HashSet<ushort> { 1024, 120, 18, 6, 2, 15 }, None);

        Assert.Equal(new Dictionary<ushort, ushort> { [2] = 2, [15] = 15 }, members.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public void ExcludedEvents_AreNeverActiveMembers()
    {
        IReadOnlyDictionary<ushort, ushort> members = PlayerbotHolidayCatalog.ActiveMembers(Z2815(), new HashSet<ushort> { 12, 24, 2 }, new HashSet<ushort> { 12 });

        Assert.Equal(new Dictionary<ushort, ushort> { [2] = 2 }, members.ToDictionary(p => p.Key, p => p.Value));
    }

    // --- the companion ---------------------------------------------------------------------------------------------------

    [Fact]
    public void WinterVeilGifts_IsNotAMember_AndAddsNoSpawns_InSliceOne()
    {
        // Event 21 is not linked in the data and is held for H6: it brings no anchors, so even with Winter Veil running it is not in the member map.
        IReadOnlyDictionary<ushort, ushort> members = PlayerbotHolidayCatalog.ActiveMembers(Z2815(), new HashSet<ushort> { 2, 21 }, None);
        Assert.False(members.ContainsKey(21));
        Assert.Equal((ushort)2, members[2]);
    }

    [Fact]
    public void TheCatalog_HasNoCompanionHolidayUntilH6()
        => Assert.Null(typeof(PlayerbotHolidayCatalog).GetMethod("CompanionHoliday", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic));

    [Fact]
    public void TheAreaDoc_DoesNotMisstateEvent21_OrTheAllocationCost()
    {
        string doc = File.ReadAllText(Path.Combine(ArcaneCore.TestSupport.RepositorySource.RequireRoot(), "docs", "areas", "playbots-social.md"));
        Assert.DoesNotContain("companion event 21 counts as holiday 141", doc);
        Assert.DoesNotContain("no per-bot allocation in the steady state", doc);
        Assert.Contains("Winter Veil: Gifts", doc);
    }

    // --- the flavour -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(341u, "midsummer", "Midsummer")]
    [InlineData(141u, "winterveil", "Winter Veil")]
    [InlineData(327u, "lunar", "Lunar Festival")]
    [InlineData(335u, "love", "Love is in the Air")]
    [InlineData(181u, "noblegarden", "Noblegarden")]
    [InlineData(201u, "children", "Children's Week")]
    [InlineData(324u, "hallows", "Hallow's End")]
    [InlineData(321u, "harvest", "Harvest Festival")]
    [InlineData(374u, "darkmoon", "Darkmoon Faire")]
    [InlineData(375u, "darkmoon", "Darkmoon Faire")]
    [InlineData(301u, "fishing", "Fishing Extravaganza")]
    public void TheHolidayIds_MapToTheirFlavourKeyAndName(uint holiday, string key, string name)
    {
        PlayerbotFestivalFlavour flavour = PlayerbotHolidayCatalog.Flavour(holiday, "whatever the data calls it");

        Assert.Equal(key, flavour.Key);
        Assert.Equal(name, flavour.Name);
    }

    [Fact]
    public void OnlyTheFishingExtravaganza_DoesNotGather()
    {
        foreach (uint holiday in new[] { 341u, 141u, 327u, 335u, 181u, 201u, 324u, 321u, 374u, 375u })
            Assert.True(PlayerbotHolidayCatalog.Flavour(holiday, "x").Gather, holiday.ToString());
        Assert.False(PlayerbotHolidayCatalog.Flavour(301, "x").Gather);
    }

    [Fact]
    public void TheEmoteSets_AreTheDesignsOnes_FromThe112TextEmotes()
    {
        static uint[] Set(uint holiday) => [.. PlayerbotHolidayCatalog.Flavour(holiday, "x").Emotes.OrderBy(e => e)];
        static uint[] Of(params uint[] emotes) => [.. emotes.OrderBy(e => e)];

        Assert.Equal(Of(TextEmotes.Dance, TextEmotes.Cheer), Set(341));
        Assert.Equal(Of(TextEmotes.Cheer, TextEmotes.Applaud), Set(141));
        Assert.Equal(Of(TextEmotes.Bow, TextEmotes.Cheer), Set(327));
        Assert.Equal(Of(TextEmotes.Laugh, TextEmotes.Cheer), Set(324));
        Assert.Equal(Of(TextEmotes.Cheer, TextEmotes.Applaud, TextEmotes.Laugh), Set(374));
        Assert.Equal(Of(TextEmotes.Cheer, TextEmotes.Applaud, TextEmotes.Laugh), Set(375));
        foreach (uint other in new[] { 335u, 181u, 201u, 321u })
            Assert.Equal(Of(TextEmotes.Cheer, TextEmotes.Wave, TextEmotes.Dance), Set(other));
    }

    [Fact]
    public void AnUnknownHoliday_FallsBackToAFestivalNamedByTheEventsDescription_AndGathers()
    {
        PlayerbotFestivalFlavour unknown = PlayerbotHolidayCatalog.Flavour(9999, "Brewfest");

        Assert.Equal("festival", unknown.Key);
        Assert.Equal("Brewfest", unknown.Name);
        Assert.True(unknown.Gather);
        Assert.NotEmpty(unknown.Emotes);

        // 62 and 376 are not in the z2815 data and not in the table.
        Assert.Equal("festival", PlayerbotHolidayCatalog.Flavour(62, "Test").Key);
        Assert.Equal("festival", PlayerbotHolidayCatalog.Flavour(376, "Test").Key);
    }

    [Theory]
    [InlineData("midsummer")]
    [InlineData("winterveil")]
    [InlineData("lunar")]
    [InlineData("love")]
    [InlineData("noblegarden")]
    [InlineData("children")]
    [InlineData("hallows")]
    [InlineData("harvest")]
    [InlineData("darkmoon")]
    [InlineData("fishing")]
    [InlineData("festival")]
    public void EveryFlavourKey_HasAnArrivalTemplate(string key)
    {
        Assert.True(PlayerbotSocialTemplates.Lines.TryGetValue("event." + key + ".arrive", out string[]? lines), key);
        Assert.NotEmpty(lines!);
        Assert.All(lines!, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }
}
