using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.Tests.Gm.Inspect;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>The selector grammar of the debug commands (plan section 1.3) and the one resolver behind it (I1).</summary>
public sealed class GmTargetResolverTests
{
    private const uint WolfEntry = 299;
    private const uint WolfSpawn = 4242;

    // --- grammar (pure) ----------------------------------------------------------------------------------------

    [Fact]
    public void AnEmptySelectorIsTheDefaultChain()
    {
        Assert.True(GmSelector.TryParse("  ", out GmSelector selector, out _));
        Assert.Equal(GmSelectorKind.Default, selector.Kind);
    }

    [Fact]
    public void ANameIsNormalised()
    {
        Assert.True(GmSelector.TryParse("$aRTHAS", out GmSelector selector, out _));
        Assert.Equal(new GmSelector(GmSelectorKind.Name, "$aRTHAS", Name: "Arthas"), selector);
    }

    [Theory]
    [InlineData("0xF130000123000001", 0xF130000123000001UL)]
    [InlineData("0Xff", 0xFFUL)]
    [InlineData("0x10", 0x10UL)]
    public void AFullGuidIsHexAfter0x(string text, ulong expected)
    {
        Assert.True(GmSelector.TryParse(text, out GmSelector selector, out _));
        Assert.Equal(GmSelectorKind.Guid, selector.Kind);
        Assert.Equal(expected, selector.Value);
    }

    [Theory]
    [InlineData("#123")]
    [InlineData("123")]
    [InlineData("#0x10")]
    public void ABareNumberIsNoSelectorSoItStaysAnArgument(string text)
    {
        // The plan writes numeric arguments as #n, so a leading #n must never be taken for a guid.
        Assert.False(GmSelector.TryParse(text, out _, out _));
        Assert.True(GmSelector.TryTakeLeading(text + " fast", out GmSelector selector, out string rest, out _));
        Assert.Equal(GmSelectorKind.Default, selector.Kind);
        Assert.Equal(text + " fast", rest);
    }

    [Theory]
    [InlineData("guid #77", 77UL, null)]
    [InlineData("GUID 77", 77UL, null)]
    [InlineData("guid #0xFFFFFF", 0xFFFFFFUL, null)]
    [InlineData("guid #77 map #1", 77UL, 1u)]
    [InlineData("guid #77 MAP 530", 77UL, 530u)]
    public void AGuidCounterTakesAnOptionalMap(string text, ulong low, uint? map)
    {
        Assert.True(GmSelector.TryParse(text, out GmSelector selector, out _));
        Assert.Equal(GmSelectorKind.Counter, selector.Kind);
        Assert.Equal(low, selector.Value);
        Assert.Equal(map, selector.MapId);
    }

    [Theory]
    [InlineData("spawn #4242", 4242UL)]
    [InlineData("SPAWN 4242", 4242UL)]
    [InlineData("spawn 0x10", 16UL)]
    public void ASpawnTakesTheSpawnGuid(string text, ulong expected)
    {
        Assert.True(GmSelector.TryParse(text, out GmSelector selector, out _));
        Assert.Equal(GmSelectorKind.Spawn, selector.Kind);
        Assert.Equal(expected, selector.Value);
    }

    [Fact]
    public void AnEntryTakesAnOptionalMap()
    {
        Assert.True(GmSelector.TryParse("entry #299", out GmSelector plain, out _));
        Assert.Equal(299u, plain.Entry);
        Assert.Null(plain.MapId);

        Assert.True(GmSelector.TryParse("entry #299 map #1", out GmSelector withMap, out _));
        Assert.Equal(299u, withMap.Entry);
        Assert.Equal(1u, withMap.MapId);

        // The old "entry #n #m" shape is not the grammar: the second number is a leftover argument.
        Assert.False(GmSelector.TryParse("entry #299 #1", out _, out _));
    }

    [Fact]
    public void NearestTakesAnOptionalRadiusAndKind()
    {
        Assert.True(GmSelector.TryParse("nearest", out GmSelector bare, out _));
        Assert.Equal(GmSelector.DefaultYards, bare.Yards);
        Assert.Equal(GmTargetKinds.Any, bare.Kinds);

        Assert.True(GmSelector.TryParse("nearest #12.5 gobject", out GmSelector both, out _));
        Assert.Equal(12.5f, both.Yards);
        Assert.Equal(GmTargetKinds.GameObject, both.Kinds);

        Assert.True(GmSelector.TryParse("nearest transport #40", out GmSelector reversed, out _));
        Assert.Equal(40f, reversed.Yards);
        Assert.Equal(GmTargetKinds.Transport, reversed.Kinds);
    }

    [Fact]
    public void OfflineTakesAName()
    {
        Assert.True(GmSelector.TryParse("offline $bob", out GmSelector selector, out _));
        Assert.Equal(GmSelectorKind.Offline, selector.Kind);
        Assert.Equal("Bob", selector.Name);
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$waytoolongaplayername")]
    [InlineData("#")]
    [InlineData("#abc")]
    [InlineData("#0")]
    [InlineData("0x")]
    [InlineData("0xZZ")]
    [InlineData("spawn")]
    [InlineData("spawn #0")]
    [InlineData("spawn x")]
    [InlineData("entry")]
    [InlineData("entry #5000000000")]
    [InlineData("entry #5 map")]
    [InlineData("entry #5 map #x")]
    [InlineData("entry #5 map #4294967296")]
    [InlineData("guid")]
    [InlineData("guid #0")]
    [InlineData("guid #16777216")]
    [InlineData("guid #5 map")]
    [InlineData("nearest #0")]
    [InlineData("nearest #501")]
    [InlineData("offline")]
    [InlineData("offline bob")]
    [InlineData("$bob extra")]
    public void ABadSelectorIsRefusedWithAReason(string text)
    {
        Assert.False(GmSelector.TryParse(text, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ALeadingSelectorLeavesTheRest()
    {
        Assert.True(GmSelector.TryTakeLeading("$bob 5 fast", out GmSelector selector, out string rest, out _));
        Assert.Equal("Bob", selector.Name);
        Assert.Equal("5 fast", rest);

        Assert.True(GmSelector.TryTakeLeading("entry #299 map #1 page #2", out selector, out rest, out _));
        Assert.Equal(1u, selector.MapId);
        Assert.Equal("page #2", rest);

        // A plain number after the selector stays an argument.
        Assert.True(GmSelector.TryTakeLeading("nearest 20", out selector, out rest, out _));
        Assert.Equal(GmSelectorKind.Nearest, selector.Kind);
        Assert.Equal("20", rest);

        Assert.True(GmSelector.TryTakeLeading("spawn #7", out selector, out rest, out _));
        Assert.Equal(string.Empty, rest);
    }

    [Fact]
    public void ATextThatIsNoSelectorIsTheDefaultAndStaysTheRest()
    {
        Assert.True(GmSelector.TryTakeLeading("  25 fast ", out GmSelector selector, out string rest, out _));
        Assert.Equal(GmSelectorKind.Default, selector.Kind);
        Assert.Equal("25 fast", rest);
    }

    [Theory]
    [InlineData("nearest creature player", GmSelectorKind.Nearest, "player")]
    [InlineData("nearest #10 #20", GmSelectorKind.Nearest, "#20")]
    [InlineData("nearest #-5", GmSelectorKind.Nearest, "#-5")]
    [InlineData("nearest gobject 3", GmSelectorKind.Nearest, "3")]
    [InlineData("entry #299 abc", GmSelectorKind.Entry, "abc")]
    [InlineData("entry #299 #1", GmSelectorKind.Entry, "#1")]
    [InlineData("entry #299 map #1 #2", GmSelectorKind.Entry, "#2")]
    [InlineData("guid #7 map #1 fast", GmSelectorKind.Counter, "fast")]
    [InlineData("guid #7 #1", GmSelectorKind.Counter, "#1")]
    [InlineData("spawn #7 #8", GmSelectorKind.Spawn, "#8")]
    [InlineData("offline $bob 5", GmSelectorKind.Offline, "5")]
    [InlineData("#5 spawn #7", GmSelectorKind.Default, "#5 spawn #7")]
    [InlineData("#5", GmSelectorKind.Default, "#5")]
    [InlineData("0x10 #5", GmSelectorKind.Guid, "#5")]
    public void ALeadingSelectorTakesOnlyTheWordsTheGrammarGivesIt(string text, GmSelectorKind kind, string expectedRest)
    {
        Assert.True(GmSelector.TryTakeLeading(text, out GmSelector selector, out string rest, out _));
        Assert.Equal(kind, selector.Kind);
        Assert.Equal(expectedRest, rest);
    }

    [Theory]
    [InlineData("nearest #1e3")]
    [InlineData("nearest #NaN1")]
    [InlineData("spawn #4294967296")]
    [InlineData("entry #0")]
    [InlineData("0x10000000000000000")]
    [InlineData("offline $")]
    public void AnOutOfRangeNumberIsABadSelectorNotAnException(string text)
    {
        bool parsed = GmSelector.TryTakeLeading(text, out _, out string rest, out string error);

        // "nearest #NaN1" is not a radius word, so nearest stops and the word is left over; the others are refused outright.
        Assert.Equal(text == "nearest #NaN1", parsed);
        Assert.Equal(parsed, error.Length == 0);
        if (parsed)
        {
            Assert.Equal("#NaN1", rest);
        }
    }

    [Fact]
    public void ANullSelectorIsTheDefaultChain()
    {
        Assert.True(GmSelector.TryParse(null, out GmSelector selector, out _));
        Assert.Equal(GmSelectorKind.Default, selector.Kind);
        Assert.True(GmSelector.TryTakeLeading(null, out _, out string rest, out _));
        Assert.Equal(string.Empty, rest);
    }

    [Fact]
    public void TheNearestRadiusIsBoundedAtTheDocumentedMaximum()
    {
        Assert.True(GmSelector.TryParse("nearest #500", out GmSelector top, out _));
        Assert.Equal(GmSelector.MaxYards, top.Yards);
        Assert.False(GmSelector.TryParse("nearest #500.5", out _, out _));
    }

    // --- resolution (world) ------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDefaultChainIsTheSelectionThenSelf()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVSELF", "Rslvself", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await WolfAsync(host);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rslvself")!.Selection = default);

        Resolved self = await ResolveAsync(host, gm, "Rslvself", "", GmTargetRequest.Reading);
        Assert.Equal("Rslvself", self.Target?.Player?.Name);
        Assert.Empty(self.Replies);

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rslvself")!.Selection = wolf.Guid);
        Resolved selected = await ResolveAsync(host, gm, "Rslvself", "", GmTargetRequest.Reading);
        Assert.Same(wolf, selected.Target?.Creature);
        Assert.Empty(selected.Replies);

        // Self is not offered to a command that cannot act on players, and a stale selection never falls back to self.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rslvself")!.Selection = default);
        Resolved noPlayer = await ResolveAsync(host, gm, "Rslvself", "", new GmTargetRequest(GmTargetKinds.Creature));
        Assert.Null(noPlayer.Target);
        Assert.Equal([GmTargetResolver.NoTarget], noPlayer.Replies);
        Assert.Equal("no-target", noPlayer.Failure);

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rslvself")!.Selection = new ObjectGuid(0xF130_0000_0000_0001));
        Resolved stale = await ResolveAsync(host, gm, "Rslvself", "", GmTargetRequest.Reading);
        Assert.Null(stale.Target);
        Assert.Equal([GmTargetResolver.SelectionGone], stale.Replies);
    }

    [Fact]
    public async Task ANameFindsAnOnlinePlayerAndHonoursTheRankRule()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVLOW", "Rslvlow", AccountSecurity.GameMaster);
        await using WorldTestClient boss = await host.EnterWorldAsync("RSLVHIGH", "Rslvhigh", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await boss.CollectAsync();

        Resolved found = await ResolveAsync(host, gm, "Rslvlow", "$rslvhigh", new GmTargetRequest(GmTargetKinds.Player));
        // The rank refusal is in the resolver: a GameMaster may not target an Administrator.
        Assert.Null(found.Target);
        Assert.Equal("security", found.Failure);

        Resolved down = await ResolveAsync(host, boss, "Rslvhigh", "$RSLVLOW", GmTargetRequest.Writing);
        Assert.Equal("Rslvlow", down.Target?.Player?.Name);
        Assert.Empty(down.Replies);

        Resolved missing = await ResolveAsync(host, boss, "Rslvhigh", "$nobody", GmTargetRequest.Reading);
        Assert.Null(missing.Target);
        Assert.Equal([GmTargetResolver.NotFound("$nobody")], missing.Replies);
    }

    [Fact]
    public async Task AGuidASpawnAnEntryAndTheNearestFindTheCreature()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVFIND", "Rslvfind", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await WolfAsync(host);
        await host.PlaceAsync("Rslvfind", wolf.X + 3, wolf.Y, wolf.Z);

        foreach (string selector in new[]
        {
            $"0x{wolf.Guid.Value:X16}", $"guid #{wolf.Guid.Counter}", $"guid #{wolf.Guid.Counter} map #0", $"spawn #{WolfSpawn}",
            $"entry #{WolfEntry}", "nearest", "nearest #10 creature", $"entry #{WolfEntry} map #0",
        })
        {
            Resolved found = await ResolveAsync(host, gm, "Rslvfind", selector, GmTargetRequest.Writing);
            Assert.Same(wolf, found.Target?.Creature);
            Assert.Empty(found.Replies);
        }

        Resolved tooFar = await ResolveAsync(host, gm, "Rslvfind", "nearest #1 creature", GmTargetRequest.Reading);
        Assert.Null(tooFar.Target);
        Assert.Equal([GmTargetResolver.NotFound("nearest #1 creature")], tooFar.Replies);

        Resolved noPlayerAround = await ResolveAsync(host, gm, "Rslvfind", "nearest player", GmTargetRequest.Reading);
        Assert.Null(noPlayerAround.Target);

        Resolved wrongSpawn = await ResolveAsync(host, gm, "Rslvfind", "spawn #1", GmTargetRequest.Reading);
        Assert.Null(wrongSpawn.Target);
        Assert.Equal("no-target", wrongSpawn.Failure);

        Resolved wrongCounter = await ResolveAsync(host, gm, "Rslvfind", $"guid #{wolf.Guid.Counter + 1}", GmTargetRequest.Reading);
        Assert.Null(wrongCounter.Target);
        Assert.Equal([GmTargetResolver.NotFound($"guid #{wolf.Guid.Counter + 1}")], wrongCounter.Replies);

        // A map with no such creature, and a map that is not loaded.
        Assert.Null((await ResolveAsync(host, gm, "Rslvfind", $"entry #{WolfEntry} map #1", GmTargetRequest.Reading)).Target);
        Assert.Null((await ResolveAsync(host, gm, "Rslvfind", $"entry #{WolfEntry} map #9999", GmTargetRequest.Reading)).Target);
    }

    [Fact]
    public async Task APlayerOnAnotherMapSearchesTheWolfsMapFromAPostedAction()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient far = await host.EnterWorldAsync("RSLVFARA", "Rslvfara");
        await far.CollectAsync();
        Creature wolf = await WolfAsync(host);
        Player farPlayer = await host.PlayerAsync("Rslvfara");
        await MoveToKalimdorAsync(host, far, farPlayer);

        // Every one of these needs another map's collections: none may run inline on the issuer's map thread.
        foreach (string selector in new[] { $"spawn #{WolfSpawn}", $"entry #{WolfEntry} map #0", $"guid #{wolf.Guid.Counter} map #0", $"0x{wolf.Guid.Value:X16}" })
        {
            Resolved found = await ResolveAsync(host, far, "Rslvfara", selector, GmTargetRequest.Reading);
            Assert.True(found.Posted, selector + " was resolved inline");
            Assert.Same(wolf, found.Target?.Creature);
            Assert.Empty(found.Replies);
        }

        Resolved missing = await ResolveAsync(host, far, "Rslvfara", "spawn #1", GmTargetRequest.Reading);
        Assert.True(missing.Posted);
        Assert.Null(missing.Target);
        Assert.Equal([GmTargetResolver.NotFound("spawn #1")], missing.Replies);

        // A write to that creature goes through the owning map's rule too: it is delivered from a posted action.
        Resolved write = await ResolveAsync(host, far, "Rslvfara", $"spawn #{WolfSpawn}", GmTargetRequest.Writing);
        Assert.True(write.Posted);
        Assert.Same(wolf, write.Target?.Creature);
    }

    [Fact]
    public async Task ALowestGuidIsThePickOnAnotherMapWhenGuidsShareTheirHighBits()
    {
        var template = new CreatureTemplate
        {
            Entry = WolfEntry, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2,
            DisplayIds = [903], Faction = 32, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        CreatureSpawn[] spawns =
        [
            new() { Guid = 4242, Entry = WolfEntry, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f },
            new() { Guid = 4243, Entry = WolfEntry, MapId = 0, X = -8930f, Y = -132f, Z = 83.5f },
            new() { Guid = 4244, Entry = WolfEntry, MapId = 0, X = -8920f, Y = -132f, Z = 83.5f },
        ];
        CreatureTestStore.Current.Value = new CreatureTestContext(new CreatureContent([template], spawns, [], [], []));
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using WorldTestHost _ = host;
        await using WorldTestClient far = await host.EnterWorldAsync("RSLVLOWG", "Rslvlowg");
        await far.CollectAsync();
        Player farPlayer = await host.PlayerAsync("Rslvlowg");
        Creature[] wolves = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Where(c => c.Entry == WolfEntry).ToArray());
        Assert.Equal(3, wolves.Length);
        // The guids differ only in the low counter bits, which a float cannot tell apart.
        Assert.Single(wolves.Select(w => (float)w.Guid.Value).Distinct());
        await MoveToKalimdorAsync(host, far, farPlayer);

        Resolved found = await ResolveAsync(host, far, "Rslvlowg", $"entry #{WolfEntry} map #0", GmTargetRequest.Reading);

        Assert.Equal(wolves.Min(w => w.Guid.Value), found.Target?.Creature?.Guid.Value);
    }

    private static async Task MoveToKalimdorAsync(WorldTestHost host, WorldTestClient client, Player player)
    {
        await host.OnWorldAsync(() => Assert.True(((WorldSession)player.Session).Services.GetRequiredService<TeleportFeature>()
            .Teleports.TeleportTo(player, 1, -441.8f, -2596f, 96f, 0)));
        await client.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await client.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await client.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        await host.WaitForWorldAsync(() => player.Map?.MapId == 1, "player on map 1");
        await client.CollectAsync();
    }

    [Fact]
    public async Task ANearestPlayerExcludesTheIssuer()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVNEAR", "Rslvnear", AccountSecurity.GameMaster);
        await using WorldTestClient other = await host.EnterWorldAsync("RSLVOTHR", "Rslvothr");
        await gm.CollectAsync();
        await other.CollectAsync();
        await host.PlaceAsync("Rslvnear", -8940f, -132f, 83.5f);
        await host.PlaceAsync("Rslvothr", -8935f, -132f, 83.5f);

        Resolved found = await ResolveAsync(host, gm, "Rslvnear", "nearest player", GmTargetRequest.Reading);

        Assert.Equal("Rslvothr", found.Target?.Player?.Name);
    }

    [Fact]
    public async Task ATargetOfAnotherKindIsRefused()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVKIND", "Rslvkind", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await WolfAsync(host);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rslvkind")!.Selection = wolf.Guid);

        Resolved refused = await ResolveAsync(host, gm, "Rslvkind", "", new GmTargetRequest(GmTargetKinds.Player | GmTargetKinds.GameObject));

        Assert.Null(refused.Target);
        Assert.Equal([GmTargetResolver.WrongKind(GmTargetKinds.Player | GmTargetKinds.GameObject)], refused.Replies);
        Assert.Equal("wrong-kind", refused.Failure);
        Assert.Equal("That target is not valid here; this command takes: player, gobject.", refused.Replies[0]);
    }

    [Fact]
    public async Task OfflineNeedsAnAdministratorAndACommandThatAcceptsIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVOFFG", "Rslvoffg", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("RSLVOFFA", "Rslvoffa", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();
        var accepts = new GmTargetRequest(AllowOffline: true);

        Resolved notAccepted = await ResolveAsync(host, admin, "Rslvoffa", "offline $bob", GmTargetRequest.Reading);
        Assert.Equal([GmTargetResolver.OfflineNotAccepted], notAccepted.Replies);

        Resolved lowRank = await ResolveAsync(host, gm, "Rslvoffg", "offline $bob", accepts);
        Assert.Null(lowRank.Target);
        Assert.Equal([GmTargetResolver.OfflineNeedsAdministrator], lowRank.Replies);

        Resolved ok = await ResolveAsync(host, admin, "Rslvoffa", "offline $bob", accepts);
        Assert.True(ok.Target?.IsOffline);
        Assert.Equal("Bob", ok.Target?.OfflineName);
        Assert.Null(ok.Target?.Object);
    }

    [Fact]
    public async Task AReadOrWriteOfAnotherMapsPlayerIsPosted()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVMAPA", "Rslvmapa", AccountSecurity.GameMaster);
        await using WorldTestClient far = await host.EnterWorldAsync("RSLVMAPB", "Rslvmapb");
        await gm.CollectAsync();
        await far.CollectAsync();
        Player farPlayer = await host.PlayerAsync("Rslvmapb");
        await MoveToKalimdorAsync(host, far, farPlayer);
        await gm.CollectAsync();

        Resolved read = await ResolveAsync(host, gm, "Rslvmapa", "$rslvmapb", GmTargetRequest.Reading);
        Assert.Equal("Rslvmapb", read.Target?.Player?.Name);
        Assert.True(read.Posted, "a read of an object on another map must be queued with World.Post too (plan 3.2 rule 7)");
        Assert.True(read.RanOnWorldThread);
        Assert.Null(read.Failure);

        Resolved readByGuid = await ResolveAsync(host, gm, "Rslvmapa", $"0x{farPlayer.Guid.Value:X16}", GmTargetRequest.Reading);
        Assert.Equal("Rslvmapb", readByGuid.Target?.Player?.Name);
        Assert.True(readByGuid.Posted);
        Assert.True(readByGuid.RanOnWorldThread);

        Resolved write = await ResolveAsync(host, gm, "Rslvmapa", "$rslvmapb", GmTargetRequest.Writing);
        Assert.Equal("Rslvmapb", write.Target?.Player?.Name);
        Assert.True(write.Posted, "a write to an object on another map must be queued with World.Post");
        Assert.True(write.RanOnWorldThread);
        Assert.Null(write.Failure);
        Assert.Empty(write.Replies);
    }

    [Fact]
    public async Task AWriteOnTheIssuersOwnMapRunsInline()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVOWNA", "Rslvowna", AccountSecurity.GameMaster);
        await using WorldTestClient other = await host.EnterWorldAsync("RSLVOWNB", "Rslvownb");
        await gm.CollectAsync();
        await other.CollectAsync();

        Resolved self = await ResolveAsync(host, gm, "Rslvowna", "", GmTargetRequest.Writing);
        Resolved named = await ResolveAsync(host, gm, "Rslvowna", "$rslvownb", GmTargetRequest.Writing);

        Assert.False(self.Posted);
        Assert.Equal("Rslvowna", self.Target?.Player?.Name);
        Assert.False(named.Posted);
        Assert.Equal("Rslvownb", named.Target?.Player?.Name);
    }

    [Fact]
    public async Task EveryConsoleWriteIsPostedAndRunsAtTheNextTickStart()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("RSLVCWRT", "Rslvcwrt");
        await player.CollectAsync();
        var output = new List<string>();
        var ran = new List<string>();
        TickPhase? phaseAtRun = null;
        bool? onWorldThread = null;
        CommandContext? console = null;

        bool ranInline = await host.OnWorldAsync(() =>
        {
            console = ConsoleOf(host, output, "Rslvcwrt");
            GmTargetResolver.ResolveThen(console, "$rslvcwrt", GmTargetRequest.Writing, target =>
            {
                phaseAtRun = host.World.CurrentPosition.Phase;
                onWorldThread = host.World.IsMainWorldThread;
                ran.Add(target.Player!.Name);
            });
            console.Complete();
            return ran.Count != 0;
        });

        Assert.False(ranInline, "a console write must not run inside the resolve call");
        await console!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(["Rslvcwrt"], ran);
        Assert.True(onWorldThread);
        Assert.Equal(TickPhase.Commands, phaseAtRun); // the start of a tick: the posted commands run before any map updates
        Assert.Null(console.Failure);
        Assert.Empty(output);

        // A read from the console needs no hop for a name.
        ran.Clear();
        bool readInline = await host.OnWorldAsync(() =>
        {
            CommandContext reader = ConsoleOf(host, output, "Rslvcwrt");
            GmTargetResolver.ResolveThen(reader, "$rslvcwrt", GmTargetRequest.Reading, target => ran.Add(target.Player!.Name));
            reader.Complete();
            return ran.Count != 0;
        });
        Assert.True(readInline);
    }

    [Fact]
    public async Task TheRankRuleAppliesToEverySelectorFormAndReadsToo()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVRKL", "Rslvrkl", AccountSecurity.GameMaster);
        await using WorldTestClient boss = await host.EnterWorldAsync("RSLVRKH", "Rslvrkh", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await boss.CollectAsync();
        Player high = await host.PlayerAsync("Rslvrkh");
        await host.PlaceAsync("Rslvrkl", -8940f, -132f, 83.5f);
        await host.PlaceAsync("Rslvrkh", -8938f, -132f, 83.5f);

        // By guid, by hex guid, by nearest, and through the selection: each read is refused with the standard reply.
        foreach (string selector in new[] { $"0x{high.Guid.Value:X16}", "nearest #10 player" })
        {
            Resolved refused = await ResolveAsync(host, gm, "Rslvrkl", selector, GmTargetRequest.Reading);
            Assert.Null(refused.Target);
            Assert.Equal("security", refused.Failure);
            Assert.NotEmpty(refused.Replies);
        }

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rslvrkl")!.Selection = high.Guid);
        Resolved selected = await ResolveAsync(host, gm, "Rslvrkl", "", GmTargetRequest.Writing);
        Assert.Null(selected.Target);
        Assert.Equal("security", selected.Failure);

        // The issuer may always act on itself, and a higher rank may act on a lower one by guid.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rslvrkl")!.Selection = default);
        Resolved self = await ResolveAsync(host, gm, "Rslvrkl", "$rslvrkl", GmTargetRequest.Writing);
        Assert.Equal("Rslvrkl", self.Target?.Player?.Name);
        Player low = await host.PlayerAsync("Rslvrkl");
        Resolved down = await ResolveAsync(host, boss, "Rslvrkh", $"0x{low.Guid.Value:X16}", GmTargetRequest.Writing);
        Assert.Equal("Rslvrkl", down.Target?.Player?.Name);
    }

    [Fact]
    public async Task TheCombatTargetIsNotAFallbackWhenNothingIsSelected()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVTGT", "Rslvtgt", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await WolfAsync(host);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Rslvtgt")!;
            player.Selection = default;
            player.Target = wolf.Guid;
        });

        Resolved viaTarget = await ResolveAsync(host, gm, "Rslvtgt", "", GmTargetRequest.Reading);
        Assert.Equal("Rslvtgt", viaTarget.Target?.Player?.Name);
        Assert.Null(viaTarget.Target?.Creature);

        // A set selection is resolved.
        Player self = await host.PlayerAsync("Rslvtgt");
        await host.OnWorldAsync(() => self.Selection = wolf.Guid);
        Resolved viaSelection = await ResolveAsync(host, gm, "Rslvtgt", "", GmTargetRequest.Reading);
        Assert.Equal(wolf.Guid, viaSelection.Target?.Creature?.Guid);
    }

    [Fact]
    public async Task TheAcceptedKindsFilterEveryNonDefaultSelector()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVKNDS", "Rslvknds", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await WolfAsync(host);
        await host.PlaceAsync("Rslvknds", wolf.X + 2, wolf.Y, wolf.Z);
        var creaturesOnly = new GmTargetRequest(GmTargetKinds.Creature);
        var playersOnly = new GmTargetRequest(GmTargetKinds.Player);

        // A name resolves a player, which a creature-only command refuses by kind.
        Resolved named = await ResolveAsync(host, gm, "Rslvknds", "$rslvknds", creaturesOnly);
        Assert.Null(named.Target);
        Assert.Equal("wrong-kind", named.Failure);
        Assert.Equal([GmTargetResolver.WrongKind(GmTargetKinds.Creature)], named.Replies);

        // The creature selectors cannot reach a command that only takes players.
        // A guid names the object outright, so its kind is refused; the searching selectors only look at accepted kinds.
        Resolved byGuid = await ResolveAsync(host, gm, "Rslvknds", $"0x{wolf.Guid.Value:X16}", playersOnly);
        Assert.Null(byGuid.Target);
        Assert.Equal("wrong-kind", byGuid.Failure);

        foreach (string selector in new[] { $"spawn #{WolfSpawn}", $"entry #{WolfEntry}", $"guid #{wolf.Guid.Counter}", "nearest #10", "nearest #10 creature" })
        {
            Resolved none = await ResolveAsync(host, gm, "Rslvknds", selector, playersOnly);
            Assert.Null(none.Target);
            Assert.Equal("no-target", none.Failure);
        }

        // A request that names no kind (default struct) accepts any.
        Resolved any = await ResolveAsync(host, gm, "Rslvknds", $"spawn #{WolfSpawn}", default);
        Assert.Same(wolf, any.Target?.Creature);
    }

    [Fact]
    public async Task EveryOfflineRefusalSetsItsFailureCode()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVOFC", "Rslvofc", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Resolved notAccepted = await ResolveAsync(host, gm, "Rslvofc", "offline $bob", GmTargetRequest.Writing);
        Assert.Equal("offline-not-accepted", notAccepted.Failure);

        Resolved lowRank = await ResolveAsync(host, gm, "Rslvofc", "offline $bob", new GmTargetRequest(AllowOffline: true));
        Assert.Equal("security", lowRank.Failure);
    }

    [Fact]
    public async Task TheConsoleNeedsAMapForPositionalSelectors()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("RSLVCONS", "Rslvcons");
        await player.CollectAsync();

        ConsoleResult named = await ResolveConsoleAsync(host, "$rslvcons", GmTargetRequest.Writing);
        Assert.Equal("Rslvcons", named.Target?.Player?.Name);
        Assert.Null(named.Failure);
        Assert.Empty(named.Replies);

        // No position and no map: the console cannot say "near me", nor pick an entry or counter without map #m.
        foreach (string selector in new[] { "nearest", "entry #1", "guid #1" })
        {
            ConsoleResult answer = await ResolveConsoleAsync(host, selector, GmTargetRequest.Reading);
            Assert.Null(answer.Target);
            Assert.Equal([GmTargetResolver.NeedsMap], answer.Replies);
            Assert.Equal("needs-map", answer.Failure);
        }

        // These search maps, so they are answered from a posted action.
        foreach (string selector in new[] { "spawn #1", "0x1234", "entry #1 map #0", "guid #1 map #0", "entry #1 map #9999" })
        {
            ConsoleResult answer = await ResolveConsoleAsync(host, selector, GmTargetRequest.Reading);
            Assert.Null(answer.Target);
            Assert.Equal([GmTargetResolver.NotFound(selector)], answer.Replies);
            Assert.Equal("no-target", answer.Failure);
            Assert.True(answer.Posted, selector + " searched maps inline");
        }

        ConsoleResult none = await ResolveConsoleAsync(host, "", GmTargetRequest.Reading);
        Assert.Equal([GmTargetResolver.NoTarget], none.Replies);
    }

    [Fact]
    public async Task TheConsoleCanNameACreatureByEntryOrCounterOnAMap()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient player = await host.EnterWorldAsync("RSLVCONW", "Rslvconw");
        await player.CollectAsync();
        Creature wolf = await WolfAsync(host);

        foreach (string selector in new[] { $"entry #{WolfEntry} map #0", $"guid #{wolf.Guid.Counter} map #0", $"spawn #{WolfSpawn}", $"0x{wolf.Guid.Value:X16}" })
        {
            ConsoleResult found = await ResolveConsoleAsync(host, selector, GmTargetRequest.Writing, "Rslvconw");
            Assert.Same(wolf, found.Target?.Creature);
            Assert.True(found.Posted, selector + " was resolved inline");
            Assert.Null(found.Failure);
            Assert.Empty(found.Replies);
        }
    }

    [Fact]
    public async Task ABadSelectorRepliesTheReasonAndFails()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVBAD", "Rslvbad", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Resolved bad = await ResolveAsync(host, gm, "Rslvbad", "spawn", GmTargetRequest.Reading);

        Assert.Null(bad.Target);
        Assert.Equal("bad-selector", bad.Failure);
        Assert.StartsWith("spawn needs", Assert.Single(bad.Replies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLeadingFormLeavesTheRestAndRepliesOnFailure()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVLEAD", "Rslvlead", AccountSecurity.GameMaster);
        await using WorldTestClient other = await host.EnterWorldAsync("RSLVLEADB", "Rslvleadb");
        await gm.CollectAsync();
        await other.CollectAsync();

        (bool started, string? name, string? rest) = await host.OnWorldAsync(() =>
        {
            Player issuer = host.World.FindOnlinePlayer("Rslvlead")!;
            CommandContext context = new((WorldSession)issuer.Session, issuer, ChatCommands.CreateTable());
            string? seenName = null;
            string? seenRest = null;
            bool began = GmTargetResolver.ResolveLeadingThen(context, "$rslvleadb 5 fast", GmTargetRequest.Writing, (target, remainder) =>
            {
                seenName = target.Player?.Name;
                seenRest = remainder;
            });
            return (began, seenName, seenRest);
        });
        Assert.True(started);
        Assert.Equal("Rslvleadb", name);
        Assert.Equal("5 fast", rest);

        bool startedMissing = await host.OnWorldAsync(() =>
        {
            Player issuer = host.World.FindOnlinePlayer("Rslvlead")!;
            CommandContext context = new((WorldSession)issuer.Session, issuer, ChatCommands.CreateTable());
            return GmTargetResolver.ResolveLeadingThen(context, "$nobody 5", GmTargetRequest.Writing, (_, _) => throw new InvalidOperationException("must not run"));
        });
        Assert.True(startedMissing);
        Assert.Equal([GmTargetResolver.NotFound("$nobody")], await gm.CollectChatLinesAsync());

        // A malformed selector is refused before any resolution; a plain #n count is not a selector and stays in the rest.
        bool startedBad = await host.OnWorldAsync(() =>
        {
            Player issuer = host.World.FindOnlinePlayer("Rslvlead")!;
            CommandContext context = new((WorldSession)issuer.Session, issuer, ChatCommands.CreateTable());
            return GmTargetResolver.ResolveLeadingThen(context, "guid #0", GmTargetRequest.Writing, (_, _) => throw new InvalidOperationException("must not run"));
        });
        Assert.False(startedBad);
        Assert.StartsWith("guid needs", Assert.Single(await gm.CollectChatLinesAsync()), StringComparison.Ordinal);

        string? countRest = await host.OnWorldAsync(() =>
        {
            Player issuer = host.World.FindOnlinePlayer("Rslvlead")!;
            CommandContext context = new((WorldSession)issuer.Session, issuer, ChatCommands.CreateTable());
            string? seen = null;
            GmTargetResolver.ResolveLeadingThen(context, "#5 fast", GmTargetRequest.Reading, (target, remainder) =>
            {
                Assert.Equal("Rslvlead", target.Player?.Name); // no selector: the default chain, here the issuer itself
                seen = remainder;
            });
            return seen;
        });
        Assert.Equal("#5 fast", countRest);
    }

    [Fact]
    public async Task ResolvingOffTheWorldThreadIsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVTHRD", "Rslvthrd", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Player player = await host.PlayerAsync("Rslvthrd");
        CommandContext context = new((WorldSession)player.Session, player, ChatCommands.CreateTable());

        Assert.Throws<InvalidOperationException>(() => GmTargetResolver.ResolveThen(context, "", GmTargetRequest.Reading, _ => { }));
    }

    private static CommandContext ConsoleOf(WorldTestHost host, List<string> output, string sessionPlayer = "Rslvcons")
    {
        Player any = host.World.FindOnlinePlayer(sessionPlayer)!;
        return CommandContext.ForConsole((WorldSession)any.Session, ChatCommands.CreateTable(), output.Add);
    }

    private static Task<Creature> WolfAsync(WorldTestHost host)
        => host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Last());

    /// <summary>The outcome of one resolution. <c>Posted</c>: the target was delivered from a posted action, not inside the resolve call.</summary>
    private sealed record Resolved(GmTarget? Target, string? Failure, string[] Replies, bool Posted, bool RanOnWorldThread);

    private sealed record ConsoleResult(GmTarget? Target, string? Failure, string[] Replies, bool Posted);

    /// <summary>
    /// Resolve <paramref name="selector"/> for the player <paramref name="issuer"/> on the world thread, then wait until the command settled
    /// (a posted search holds it open); the replies are read from its client.
    /// </summary>
    private static async Task<Resolved> ResolveAsync(WorldTestHost host, WorldTestClient client, string issuer, string selector, GmTargetRequest request)
    {
        await client.CollectAsync();
        GmTarget? target = null;
        bool onWorldThread = false;
        bool posted = false;
        CommandContext? context = null;
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(issuer)!;
            context = new CommandContext((WorldSession)player.Session, player, ChatCommands.CreateTable());
            GmTargetResolver.ResolveThen(context, selector, request, found =>
            {
                target = found;
                onWorldThread = host.World.IsMainWorldThread;
            });
            context.Complete();
            posted = !context.WhenSettled.IsCompleted;
        });
        await context!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(20));
        // A failed resolution always replies, so wait for that line; a successful one is silent.
        string[] replies = context.Failure is not null
            ? await client.CollectChatLinesAsync()
            : [.. (await client.CollectAsync()).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text)];
        return new Resolved(target, context.Failure, replies, posted, onWorldThread);
    }

    private static async Task<ConsoleResult> ResolveConsoleAsync(WorldTestHost host, string selector, GmTargetRequest request, string sessionPlayer = "Rslvcons")
    {
        var output = new List<string>();
        GmTarget? target = null;
        bool posted = false;
        CommandContext? context = null;
        await host.OnWorldAsync(() =>
        {
            context = ConsoleOf(host, output, sessionPlayer);
            GmTargetResolver.ResolveThen(context, selector, request, found =>
            {
                target = found;
            });
            context.Complete();
            posted = !context.WhenSettled.IsCompleted;
        });
        await context!.WhenSettled.WaitAsync(TimeSpan.FromSeconds(20));
        return new ConsoleResult(target, context.Failure, [.. output], posted);
    }
}
