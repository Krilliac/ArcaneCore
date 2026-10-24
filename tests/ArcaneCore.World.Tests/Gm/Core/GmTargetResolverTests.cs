using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Core;
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
    [InlineData("#123", 123UL)]
    [InlineData("123", 0UL)]
    [InlineData("0xF130000123000001", 0xF130000123000001UL)]
    [InlineData("0Xff", 0xFFUL)]
    [InlineData("#0x10", 0x10UL)]
    public void AGuidIsDecimalAfterAHashOrHexAfter0x(string text, ulong expected)
    {
        bool parsed = GmSelector.TryParse(text, out GmSelector selector, out _);
        if (expected == 0)
        {
            // A bare number is no selector: it is an argument (the default chain), so a whole-text parse refuses the leftover.
            Assert.False(parsed);
            return;
        }

        Assert.True(parsed);
        Assert.Equal(GmSelectorKind.Guid, selector.Kind);
        Assert.Equal(expected, selector.Value);
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

        Assert.True(GmSelector.TryParse("entry #299 #1", out GmSelector withMap, out _));
        Assert.Equal(299u, withMap.Entry);
        Assert.Equal(1u, withMap.MapId);
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

        Assert.True(GmSelector.TryTakeLeading("entry #299 #1 page #2", out selector, out rest, out _));
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
    [InlineData("entry #299 #1 #2", GmSelectorKind.Entry, "#2")]
    [InlineData("spawn #7 #8", GmSelectorKind.Spawn, "#8")]
    [InlineData("offline $bob 5", GmSelectorKind.Offline, "5")]
    [InlineData("#5 spawn #7", GmSelectorKind.Guid, "spawn #7")]
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
    [InlineData("#18446744073709551616")]
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

        foreach (string selector in new[] { $"#{wolf.Guid.Value}", $"0x{wolf.Guid.Value:X16}", $"spawn #{WolfSpawn}", $"entry #{WolfEntry}", "nearest", "nearest #10 creature", $"entry #{WolfEntry} #0"})
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
    public async Task AWriteRefusesATargetOnAnotherMapButAReadAllowsIt()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVMAPA", "Rslvmapa", AccountSecurity.GameMaster);
        await using WorldTestClient far = await host.EnterWorldAsync("RSLVMAPB", "Rslvmapb");
        await gm.CollectAsync();
        await far.CollectAsync();
        Player farPlayer = await host.PlayerAsync("Rslvmapb");
        await host.OnWorldAsync(() => Assert.True(((WorldSession)farPlayer.Session).Services.GetRequiredService<TeleportFeature>()
            .Teleports.TeleportTo(farPlayer, 1, -441.8f, -2596f, 96f, 0)));
        await far.ReadUntilAsync(WorldOpcode.SmsgNewWorld);
        await far.SendAsync(WorldOpcode.MsgMoveWorldportAck, []);
        await far.ReadUntilAsync(WorldOpcode.SmsgInitWorldStates);
        await host.WaitForWorldAsync(() => farPlayer.Map?.MapId == 1, "far player on map 1");
        await gm.CollectAsync();

        Resolved read = await ResolveAsync(host, gm, "Rslvmapa", "$rslvmapb", GmTargetRequest.Reading);
        Assert.Equal("Rslvmapb", read.Target?.Player?.Name);

        Resolved write = await ResolveAsync(host, gm, "Rslvmapa", "$rslvmapb", GmTargetRequest.Writing);
        Assert.Null(write.Target);
        Assert.Equal([GmTargetResolver.OtherMap], write.Replies);
        Assert.Equal("other-map", write.Failure);

        // A console issuer has no map, so the same-map rule does not apply to it.
        var consoleOutput = new List<string>();
        (GmTarget? viaConsole, string? consoleFailure) = await host.OnWorldAsync(() =>
        {
            Player any = host.World.FindOnlinePlayer("Rslvmapa")!;
            CommandContext console = CommandContext.ForConsole((WorldSession)any.Session, ChatCommands.CreateTable(), consoleOutput.Add);
            return (GmTargetResolver.Resolve(console, "$rslvmapb", GmTargetRequest.Writing), console.Failure);
        });
        Assert.Equal("Rslvmapb", viaConsole?.Player?.Name);
        Assert.Null(consoleFailure);
        Assert.Empty(consoleOutput);
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
        foreach (string selector in new[] { $"#{high.Guid.Value}", $"0x{high.Guid.Value:X16}", "nearest #10 player" })
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
        Resolved down = await ResolveAsync(host, boss, "Rslvrkh", $"#{low.Guid.Value}", GmTargetRequest.Writing);
        Assert.Equal("Rslvrkl", down.Target?.Player?.Name);
    }

    [Fact]
    public async Task TheUnitTargetIsTheFallbackWhenNothingIsSelected()
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
        Assert.Same(wolf, viaTarget.Target?.Creature);

        // A set selection wins over the target.
        Player self = await host.PlayerAsync("Rslvtgt");
        await host.OnWorldAsync(() => self.Selection = self.Guid);
        Resolved viaSelection = await ResolveAsync(host, gm, "Rslvtgt", "", GmTargetRequest.Reading);
        Assert.Equal("Rslvtgt", viaSelection.Target?.Player?.Name);
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
        Resolved byGuid = await ResolveAsync(host, gm, "Rslvknds", $"#{wolf.Guid.Value}", playersOnly);
        Assert.Null(byGuid.Target);
        Assert.Equal("wrong-kind", byGuid.Failure);

        foreach (string selector in new[] { $"spawn #{WolfSpawn}", $"entry #{WolfEntry}", "nearest #10", "nearest #10 creature" })
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
        var output = new List<string>();

        (GmTarget? named, string? failure, string[] replies) = await host.OnWorldAsync(() =>
        {
            CommandContext console = ConsoleOf(host, output);
            GmTarget? found = GmTargetResolver.Resolve(console, "$rslvcons", GmTargetRequest.Writing);
            return (found, console.Failure, output.ToArray());
        });
        Assert.Equal("Rslvcons", named?.Player?.Name);
        Assert.Null(failure);
        Assert.Empty(replies);

        foreach (string selector in new[] { "#1", "spawn #1", "entry #1", "nearest" })
        {
            output.Clear();
            string[] answer = await host.OnWorldAsync(() =>
            {
                Assert.Null(GmTargetResolver.Resolve(ConsoleOf(host, output), selector, GmTargetRequest.Reading));
                return output.ToArray();
            });
            Assert.Equal([GmTargetResolver.NeedsMap], answer);
        }

        output.Clear();
        string[] none = await host.OnWorldAsync(() =>
        {
            Assert.Null(GmTargetResolver.Resolve(ConsoleOf(host, output), "", GmTargetRequest.Reading));
            return output.ToArray();
        });
        Assert.Equal([GmTargetResolver.NoTarget], none);
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

        (bool ok, string? name, string rest) = await host.OnWorldAsync(() =>
        {
            Player issuer = host.World.FindOnlinePlayer("Rslvlead")!;
            CommandContext context = new((WorldSession)issuer.Session, issuer, ChatCommands.CreateTable());
            bool resolved = GmTargetResolver.TryResolveLeading(context, "$rslvleadb 5 fast", GmTargetRequest.Writing, out GmTarget target, out string remainder);
            return (resolved, target?.Player?.Name, remainder);
        });
        Assert.True(ok);
        Assert.Equal("Rslvleadb", name);
        Assert.Equal("5 fast", rest);

        bool failed = await host.OnWorldAsync(() =>
        {
            Player issuer = host.World.FindOnlinePlayer("Rslvlead")!;
            CommandContext context = new((WorldSession)issuer.Session, issuer, ChatCommands.CreateTable());
            return GmTargetResolver.TryResolveLeading(context, "$nobody 5", GmTargetRequest.Writing, out _, out _);
        });
        Assert.False(failed);
        Assert.Equal([GmTargetResolver.NotFound("$nobody")], await gm.CollectChatLinesAsync());
    }

    [Fact]
    public async Task ResolvingOffTheWorldThreadIsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RSLVTHRD", "Rslvthrd", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Player player = await host.PlayerAsync("Rslvthrd");
        CommandContext context = new((WorldSession)player.Session, player, ChatCommands.CreateTable());

        Assert.Throws<InvalidOperationException>(() => GmTargetResolver.Resolve(context, "", GmTargetRequest.Reading));
    }

    private static CommandContext ConsoleOf(WorldTestHost host, List<string> output)
    {
        Player any = host.World.FindOnlinePlayer("Rslvcons")!;
        return CommandContext.ForConsole((WorldSession)any.Session, ChatCommands.CreateTable(), output.Add);
    }

    private static Task<Creature> WolfAsync(WorldTestHost host)
        => host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Last());

    private sealed record Resolved(GmTarget? Target, string? Failure, string[] Replies);

    /// <summary>Resolve <paramref name="selector"/> for the player <paramref name="issuer"/> on the world thread; the replies are read from its client.</summary>
    private static async Task<Resolved> ResolveAsync(WorldTestHost host, WorldTestClient client, string issuer, string selector, GmTargetRequest request)
    {
        await client.CollectAsync();
        (GmTarget? target, string? failure) = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(issuer)!;
            var context = new CommandContext((WorldSession)player.Session, player, ChatCommands.CreateTable());
            GmTarget? found = GmTargetResolver.Resolve(context, selector, request);
            return (found, context.Failure);
        });
        // A failed resolution always replies, so wait for that line; a successful one is silent.
        string[] replies = failure is not null
            ? await client.CollectChatLinesAsync()
            : [.. (await client.CollectAsync()).Where(p => p.Opcode == WorldOpcode.SmsgMessagechat).Select(p => ChatMessage.Parse(p.Payload).Text)];
        return new Resolved(target, failure, replies);
    }
}
