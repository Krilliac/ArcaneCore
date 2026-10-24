using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots.Commands;
using ArcaneCore.World.Playerbots.Control;
using ArcaneCore.World.Tests.Playerbots.Party;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Commands;

/// <summary>The bot selector grammar shared by <c>.playerbot</c> and <c>.botctl</c> (I11), against a fake bot source.</summary>
public sealed class BotSelectorTests
{
    private static readonly Guid Alice = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Bobby = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Carol = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    private sealed class FakeSource : IBotSelectorSource
    {
        private readonly List<PlayerbotStatus> _bots =
        [
            Status(Carol, "Carol", PlayerbotGoalKind.Grind, ManagedPlayerbotState.Stopped, map: 1),
            Status(Alice, "Alice", PlayerbotGoalKind.Quest, ManagedPlayerbotState.Running, map: 0),
            Status(Bob, "Bob", PlayerbotGoalKind.Grind, ManagedPlayerbotState.Running, map: 0),
            Status(Bobby, "Bobby", PlayerbotGoalKind.Rest, ManagedPlayerbotState.Running, map: 1),
        ];

        public HashSet<Guid> Controlled { get; } = [Bob];

        public Dictionary<string, string[]> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            ["pair"] = ["Alice", "Bob"],
            ["loop"] = ["@loop"],
            ["wide"] = ["where:map=1"],
        };

        public IReadOnlyList<PlayerbotStatus> Snapshot() => _bots;

        public bool TryResolve(string idOrName, out Guid botId, out string name)
        {
            PlayerbotStatus? match = Guid.TryParse(idOrName, out Guid id)
                ? _bots.FirstOrDefault(b => b.BotId == id)
                : _bots.FirstOrDefault(b => b.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            botId = match?.BotId ?? Guid.Empty;
            name = match?.Name ?? string.Empty;
            return match is not null;
        }

        public bool IsRunning(Guid botId) => _bots.First(b => b.BotId == botId).State == ManagedPlayerbotState.Running;

        public bool IsControlled(Guid botId) => Controlled.Contains(botId);

        /// <summary>Controllers whose bot is not in <see cref="Snapshot"/> (the hub names them anyway).</summary>
        public List<BotPick> Orphans { get; } = [];

        IReadOnlyList<BotPick> IBotSelectorSource.Controlled()
            => [.. _bots.Where(b => Controlled.Contains(b.BotId)).Select(b => new BotPick(b.BotId, b.Name)), .. Orphans];

        public IReadOnlyList<string>? Alias(string name) => Aliases.GetValueOrDefault(name);

        public BotAttributes Attributes(PlayerbotStatus bot) => bot.Name switch
        {
            "Alice" => new BotAttributes(0, 12, 20, "Mage", "Human"),
            "Bob" => new BotAttributes(0, 12, 35, "Warrior", "Orc"),
            "Bobby" => new BotAttributes(1, 17, 60, "Warrior", "Orc"),
            _ => new BotAttributes(bot.MapId, null, null, null, null),
        };
    }

    private static PlayerbotStatus Status(Guid id, string name, PlayerbotGoalKind goal, ManagedPlayerbotState state, uint map)
        => new(id, name, state, true, goal, 0, 0, map, 100, null);

    private static string[] Names(string selector, out BotSelection selection)
    {
        string? error = BotSelector.TryResolve(selector, new FakeSource(), out selection);
        Assert.Null(error);
        return [.. selection.Bots.Select(b => b.Name)];
    }

    private static string Error(string selector)
    {
        string? error = BotSelector.TryResolve(selector, new FakeSource(), out BotSelection selection);
        Assert.NotNull(error);
        Assert.Empty(selection.Bots);
        return error;
    }

    [Theory]
    [InlineData("Alice", "Alice")]
    [InlineData("$Alice", "Alice")]
    [InlineData("$alice", "Alice")]
    [InlineData("00000000-0000-0000-0000-00000000000b", "Bob")]
    [InlineData("$00000000-0000-0000-0000-00000000000b", "Bob")]
    public void ANameOrIdNamesOneBotWithOrWithoutTheDollar(string selector, string expected)
    {
        Assert.Equal([expected], Names(selector, out BotSelection selection));
        Assert.False(selection.Broad);
    }

    [Fact]
    public void AllIsEveryBotByNameAndBroad()
    {
        Assert.Equal(["Alice", "Bob", "Bobby", "Carol"], Names("all", out BotSelection selection));
        Assert.True(selection.Broad);
        Assert.Equal(["Alice", "Bob", "Bobby", "Carol"], Names("ALL", out _));
    }

    [Fact]
    public void APrefixMatchesByStartAndIsBroad()
    {
        Assert.Equal(["Bob", "Bobby"], Names("$bo*", out BotSelection selection));
        Assert.True(selection.Broad);
        Assert.Equal(["Alice"], Names("Al*", out _));
    }

    [Fact]
    public void ControlledAndRunningFollowTheSource()
    {
        Assert.Equal(["Bob"], Names("controlled", out BotSelection controlled));
        Assert.True(controlled.Broad);
        Assert.Equal(["Bob"], Names("mine", out _));
        Assert.Equal(["Alice", "Bob", "Bobby"], Names("running", out _));
    }

    [Fact]
    public void AListKeepsSelectorOrderAndDropsRepeats()
    {
        Assert.Equal(["Carol", "Alice", "Bob", "Bobby"], Names("Carol, $Alice,Bo*,Alice", out BotSelection selection));
        Assert.True(selection.Broad);
        Assert.Equal(["Carol", "Alice"], Names("Carol,Alice,Carol", out BotSelection narrow));
        Assert.False(narrow.Broad);
    }

    [Fact]
    public void WhereFiltersOnEveryKeyAndIsBroad()
    {
        Assert.Equal(["Alice", "Bob"], Names("where:map=0", out BotSelection selection));
        Assert.True(selection.Broad);
        Assert.Equal(["Bob", "Bobby"], Names("where:class=warrior", out _));
        Assert.Equal(["Bob"], Names("where:class=warrior&map=0", out _));
        Assert.Equal(["Bob"], Names("where:level=30-40", out _));
        Assert.Equal(["Alice"], Names("where:level=20", out _));
        Assert.Equal(["Alice", "Bob"], Names("where:zone=12", out _));
        Assert.Equal(["Bob", "Bobby"], Names("where:race=orc", out _));
        Assert.Equal(["Bob"], Names("where:controlled=1", out _));
        Assert.Equal(["Alice", "Bobby", "Carol"], Names("where:controlled=0", out _));
        Assert.Equal(["Carol"], Names("where:running=0", out _));
        Assert.Equal(["Alice", "Bob", "Bobby"], Names("where:state=running", out _));
        Assert.Equal(["Bob", "Carol"], Names("where:goal=grind", out _));
        Assert.Equal(["Bob", "Bobby"], Names("where:name=ob", out _));
        Assert.Equal(["Alice"], Names("where:map=0&goal=quest", out _));
    }

    [Fact]
    public void ABotWithoutLiveAttributesNeverMatchesALiveFilter()
    {
        // Carol is not in the world: map comes from her status, the rest are unknown.
        Assert.Equal(["Bobby", "Carol"], Names("where:map=1", out _));
        string error = Error("where:level=1-99&map=1&name=ca");
        Assert.StartsWith("no-match", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAliasExpandsItsMembersAndIsBroad()
    {
        Assert.Equal(["Alice", "Bob"], Names("@pair", out BotSelection selection));
        Assert.True(selection.Broad);
        Assert.Equal(["Carol", "Alice", "Bob"], Names("Carol,@pair", out _));
        Assert.Equal(["Bobby", "Carol"], Names("@wide", out _));
        Assert.Equal(["Alice", "Bob"], Names("@pair,Alice", out _));
    }

    [Theory]
    [InlineData("@nope", "no-alias:")]
    [InlineData("@loop", "bad-selector: aliases nest too deep")]
    [InlineData("zed", "no-match: no playerbot 'zed'")]
    [InlineData("$zed", "no-match: no playerbot 'zed'")]
    [InlineData("Alice,zed", "no-match: no playerbot 'zed'")]
    [InlineData("$", "bad-selector:")]
    [InlineData("*", "bad-selector: a prefix needs at least one letter before '*'")]
    [InlineData("where:foo=1", "bad-selector: unknown filter key 'foo'")]
    [InlineData("where:map", "bad-selector: filter term 'map' is not key=value")]
    [InlineData("where:map=9", "no-match: no playerbot matches 'where:map=9'")]
    [InlineData("", "no-match: no playerbot matches ''")]
    [InlineData("   ", "no-match: no playerbot matches ''")]
    [InlineData("Zz*", "no-match: no playerbot matches 'Zz*'")]
    public void AFailureIsACodeAndAMessage(string selector, string expectedStart)
        => Assert.StartsWith(expectedStart, Error(selector), StringComparison.Ordinal);

    [Theory]
    [InlineData("where:map=0&map=1")]
    [InlineData("where:level=40-30")]
    [InlineData("where:level=5-x")]
    [InlineData("where:level=x")]
    [InlineData("where:map=abc")]
    [InlineData("where:map=-1")]
    [InlineData("where:class=nope")]
    [InlineData("where:state=nope")]
    [InlineData(",")]
    public void AFilterThatCanNeverMatchIsNoMatchNotAnException(string selector)
        => Assert.StartsWith("no-match", Error(selector), StringComparison.Ordinal);

    [Fact]
    public void FilterKeysAndKeywordsIgnoreCase()
    {
        Assert.Equal(["Bob"], Names("WHERE:CLASS=Warrior&Map=0", out BotSelection where));
        Assert.True(where.Broad);
        Assert.Equal(["Bob"], Names("CONTROLLED", out _));
        Assert.Equal(["Alice", "Bob", "Bobby"], Names("RUNNING", out _));
        Assert.Equal(["Alice", "Bob"], Names("@PAIR", out _));
    }

    [Fact]
    public void ADollarPartIsAlwaysANameNeverAKeywordAliasOrFilter()
    {
        foreach (string keyword in new[] { "$all", "$running", "$controlled", "$mine", "$where:map=0", "$@pair" })
        {
            Assert.StartsWith("no-match", Error(keyword), StringComparison.Ordinal);
        }

        Assert.Equal(["Alice"], Names("$alice", out BotSelection byName));
        Assert.False(byName.Broad);
        Assert.Equal(["Bob", "Bobby"], Names("$bo*", out BotSelection byPrefix));
        Assert.True(byPrefix.Broad);
    }

    [Fact]
    public void ABroadPartMakesTheWholeListBroadEvenWhenAnEarlierPartWasNot()
    {
        Assert.Equal(["Alice", "Bob"], Names("Alice,Bob", out BotSelection narrow));
        Assert.False(narrow.Broad);
        Names("Alice,all", out BotSelection wide);
        Assert.True(wide.Broad);
        Names("Alice,where:map=0", out BotSelection filtered);
        Assert.True(filtered.Broad);
    }

    [Theory]
    [InlineData("where:map=0&zone=12&level=30-40&class=warrior&race=orc&controlled=1&running=1&state=running&goal=grind&name=bo", "Bob")]
    [InlineData("where:level=60", "Bobby")]
    [InlineData("where:level=20-20", "Alice")]
    [InlineData("where:controlled=true", "Bob")]
    [InlineData("where:controlled=yes", "Bob")]
    [InlineData("where:controlled=on", "Bob")]
    public void WhereTermsAreAndedAndAFlagTakesTheUsualTruthWords(string selector, string expected)
        => Assert.Equal([expected], Names(selector, out _));

    [Fact]
    public void AnEmptySnapshotIsNoMatchForEveryBroadForm()
    {
        var empty = new EmptySource();
        foreach (string selector in new[] { "all", "running", "controlled", "Bo*", "where:map=0" })
        {
            string? error = BotSelector.TryResolve(selector, empty, out BotSelection selection);
            Assert.StartsWith("no-match", error, StringComparison.Ordinal);
            Assert.Empty(selection.Bots);
            Assert.False(selection.Broad);
        }
    }

    [Fact]
    public void AnAliasMemberThatFailsFailsTheWholeSelectorAndAnAliasCanNestWithinTheLimit()
    {
        var source = new FakeSource();
        source.Aliases["bad"] = ["Alice", "zed"];
        source.Aliases["one"] = ["@two"];
        source.Aliases["two"] = ["@three"];
        source.Aliases["three"] = ["Carol"];
        source.Aliases["a1"] = ["@a2"];
        source.Aliases["a2"] = ["@a3"];
        source.Aliases["a3"] = ["@a4"];
        source.Aliases["a4"] = ["@a5"];
        source.Aliases["a5"] = ["Carol"];

        string? bad = BotSelector.TryResolve("@bad", source, out BotSelection none);
        Assert.StartsWith("no-match: no playerbot 'zed'", bad, StringComparison.Ordinal);
        Assert.Empty(none.Bots);

        Assert.Null(BotSelector.TryResolve("@one", source, out BotSelection nested));
        Assert.Equal(["Carol"], nested.Bots.Select(b => b.Name));
        Assert.True(nested.Broad);

        Assert.StartsWith("bad-selector: aliases nest too deep", BotSelector.TryResolve("@a1", source, out _), StringComparison.Ordinal);
    }

    [Fact]
    public void TheResolvedPickCarriesTheBotIdAndName()
    {
        Names("$bob", out BotSelection selection);
        Assert.Equal(new BotPick(Bob, "Bob"), Assert.Single(selection.Bots));
    }

    private sealed class EmptySource : IBotSelectorSource
    {
        public IReadOnlyList<PlayerbotStatus> Snapshot() => [];

        public bool TryResolve(string idOrName, out Guid botId, out string name)
        {
            botId = Guid.Empty;
            name = string.Empty;
            return false;
        }

        public bool IsRunning(Guid botId) => false;

        public bool IsControlled(Guid botId) => false;

        public IReadOnlyList<BotPick> Controlled() => [];

        public IReadOnlyList<string>? Alias(string name) => null;

        public BotAttributes Attributes(PlayerbotStatus bot) => new(0, null, null, null, null);
    }

    [Fact]
    public void ControlledNamesTheControllersEvenForABotMissingFromTheSnapshot()
    {
        var source = new FakeSource();
        var ghost = new BotPick(Guid.Parse("00000000-0000-0000-0000-0000000000ee"), "Ghost");
        source.Orphans.Add(ghost);

        Assert.Null(BotSelector.TryResolve("controlled", source, out BotSelection selection));

        // By controller, sorted by name: the .botctl behaviour from before the selector was shared.
        Assert.Equal(["Bob", "Ghost"], selection.Bots.Select(b => b.Name));
        Assert.True(selection.Broad);
        Assert.Equal(ghost, selection.Bots[1]);
    }

    [Fact]
    public async Task TheFeatureSourceResolvesTheSameSetsAsTheOldBotctlCodeAgainstARealHubAndFeature()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        Guid first = await world.AddBotAsync("Selfaaa", 10);
        Guid second = await world.AddBotAsync("Selfaab", 12);
        await world.AddBotAsync("Selfbba", 14);
        await world.CreateStoppedBotAsync("Selfcca");
        BotControlHub hub = BotControlHub.For(world.Bots);
        (BotController? controller, string? takeError) = await hub.TakeAsync(second, "Selfaab", "test", start: false, force: false);
        Assert.Null(takeError);
        Assert.NotNull(controller);

        async Task<string[]> Resolve(string selector) => (await ResolveSelection(selector)).Names;

        async Task<(string[] Names, bool Broad)> ResolveSelection(string selector)
            => await world.Host.OnWorldAsync<(string[] Names, bool Broad)>(() =>
            {
                string? error = BotSelector.TryResolve(selector, new FeatureBotSelectorSource(world.Bots, hub), out BotSelection selection);
                Assert.Null(error);
                return ([.. selection.Bots.Select(b => b.Name)], selection.Broad);
            });

        (string[] allNames, bool broad) = await ResolveSelection("all");
        Assert.Equal(["Selfaaa", "Selfaab", "Selfbba", "Selfcca"], allNames);
        Assert.True(broad);
        Assert.Equal(["Selfaaa", "Selfaab", "Selfbba"], await Resolve("running"));
        Assert.Equal(["Selfaab"], await Resolve("controlled"));
        Assert.Equal(["Selfaab"], await Resolve("mine"));
        Assert.Equal(["Selfaaa", "Selfaab"], await Resolve("selfaa*"));
        Assert.Equal(["Selfaaa", "Selfaab"], await Resolve("$SELFAA*"));
        Assert.Equal(["Selfaab"], await Resolve("where:controlled=1"));
        Assert.Equal(["Selfcca"], await Resolve("where:running=0"));
        Assert.Equal(["Selfbba"], await Resolve("where:name=bb"));
        (string[] oneName, bool single) = await ResolveSelection(first.ToString("D"));
        Assert.Equal(["Selfaaa"], oneName);
        Assert.False(single);
        Assert.Equal(["Selfbba", "Selfaab"], await Resolve("Selfbba,$selfaab,selfbba"));
        uint mapId = await world.Host.OnWorldAsync(() => world.Host.World.FindOnlinePlayer("Selfaaa")!.MapId);
        Assert.Equal(["Selfaaa", "Selfaab", "Selfbba"], await Resolve($"where:map={mapId}&running=1"));

        hub.SetGroup("duo", ["Selfaaa", "Selfbba"]);
        Assert.Equal(["Selfaaa", "Selfbba"], await Resolve("@duo"));
    }

    [Fact]
    public void TheFilterKeysAreTheDocumentedOnes()
        => Assert.Equal(["map", "zone", "level", "class", "race", "controlled", "running", "state", "goal", "name"], BotSelector.FilterKeys);

    [Fact]
    public void ANullSourceIsRefused()
        => Assert.Throws<ArgumentNullException>(() => BotSelector.TryResolve("all", null!, out _));
}
