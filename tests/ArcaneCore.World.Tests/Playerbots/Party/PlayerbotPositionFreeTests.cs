using System.Globalization;
using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.GridTerrain;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// The <c>at $map $x $y $z</c> forms of <c>.playerbot send</c> and <c>.playerbot defend</c>: the same result as <c>here</c> at the
/// same spot, from the server console (RA runs the same executor, no invoking player), and a clear refusal for bad input.
/// </summary>
public sealed class PlayerbotPositionFreeTests
{
    private static string At(float x, float y, float z) => string.Create(CultureInfo.InvariantCulture, $"at 0 {x} {y} {z}");

    [Fact]
    public async Task SendAt_FromTheConsole_PlacesTheBotsAsSendHereDoes()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("PFSENDGM", "Pfsendgm");
        await world.AddBotAsync("Pfsendaa", 100);
        await world.AddBotAsync("Pfsendab", 110);
        await world.AddBotAsync("Pfsendba", 120);
        await world.AddBotAsync("Pfsendbb", 130);

        List<string> here = await FleetWorld.SayAsync(gm, ".playerbot send Pfsenda* here");
        string[] at = await world.Console.RunAsync($".playerbot send Pfsendb* {At(FleetWorld.BaseX, FleetWorld.BaseY, FleetWorld.BaseZ)}");

        Assert.Equal(["Playerbot send: matched=2 moved=2 skipped=0"], here);
        Assert.Equal(["Playerbot send: matched=2 moved=2 skipped=0"], at);
        await world.WaitAroundAsync(["Pfsendaa", "Pfsendab"], "Pfsendgm");
        await world.WaitAroundAsync(["Pfsendba", "Pfsendbb"], "Pfsendgm");
        foreach (string name in new[] { "Pfsendaa", "Pfsendba" })
            Assert.Equal(FleetWorld.BaseZ, (await world.PositionAsync(name)).Z, 1);
    }

    [Fact]
    public async Task DefendAt_FromTheConsole_SetsTheSamePostAsDefendHere()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("PFDEFGM", "Pfdefgm");
        Guid a = await world.AddBotAsync("Pfdefaa", 30);
        Guid b = await world.AddBotAsync("Pfdefba", 35);

        List<string> here = await FleetWorld.SayAsync(gm, ".playerbot defend Pfdefaa here 40");
        string[] at = await world.Console.RunAsync($".playerbot defend Pfdefba {At(FleetWorld.BaseX, FleetWorld.BaseY, FleetWorld.BaseZ)} 40");

        Assert.Equal(["Playerbot defend: matched=1 defending=1 skipped=0"], here);
        Assert.Equal(["Playerbot defend: matched=1 defending=1 skipped=0"], at);
        (PlayerbotDefendPost? viaHere, PlayerbotDefendPost? viaAt) = await world.Host.OnWorldAsync(() => (world.Bots.FindBrain(a)!.Defend, world.Bots.FindBrain(b)!.Defend));
        Assert.NotNull(viaHere);
        Assert.NotNull(viaAt);
        Assert.Equal(viaHere.MapId, viaAt.MapId);
        Assert.Equal(viaHere.Radius, viaAt.Radius);
        Assert.Equal(FleetWorld.BaseX, viaAt.Centre.X, 1f);
        Assert.Equal(FleetWorld.BaseY, viaAt.Centre.Y, 1f);
        Assert.Equal(FleetWorld.BaseZ, viaAt.Centre.Z, 1f);
        Assert.Equal(viaHere.Centre.X, viaAt.Centre.X, 1f);
        Assert.Equal(viaHere.Centre.Y, viaAt.Centre.Y, 1f);

        string[] clear = await world.Console.RunAsync(".playerbot defend Pfdef* clear");
        Assert.Equal(["Playerbot defend: matched=2 cleared=2 skipped=0"], clear);
    }

    [Fact]
    public async Task DefendAt_ClampsTheRadiusAndHonoursForce()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(options => options.MaxBulkCommand = 1);
        Guid a = await world.AddBotAsync("Pfclaaa", 30);
        await world.AddBotAsync("Pfclaab", 33);

        string[] refused = await world.Console.RunAsync($".playerbot defend Pfclaa* {At(-8000f, 1000f, 100f)} 500");
        string[] forced = await world.Console.RunAsync($".playerbot defend Pfclaa* {At(-8000f, 1000f, 100f)} 500 force");

        Assert.Equal(["Playerbot defend: 2 bots match; add 'force' above 1."], refused);
        Assert.Equal(["Playerbot defend: matched=2 defending=2 skipped=0"], forced);
        Assert.Equal(200f, (await world.Host.OnWorldAsync(() => world.Bots.FindBrain(a)!.Defend))!.Radius);
    }

    [Theory]
    [InlineData("send Pfbadaa at 9999 1 2 3", "Playerbot send: unknown map 9999.")]
    [InlineData("send Pfbadaa at x 1 2 3", "Playerbot send: the map must be a map id.")]
    [InlineData("send Pfbadaa at 0 NaN 2 3", "Playerbot send: x, y and z must be finite numbers.")]
    [InlineData("send Pfbadaa at 0 1 Infinity 3", "Playerbot send: x, y and z must be finite numbers.")]
    [InlineData("send Pfbadaa at 0 1 2 abc", "Playerbot send: x, y and z must be finite numbers.")]
    [InlineData("send Pfbadaa at 0 99999 2 3", "Playerbot send: the position is outside the map.")]
    [InlineData("send Pfbadaa at 0 1 -99999 3", "Playerbot send: the position is outside the map.")]
    [InlineData("send Pfbadaa at 0 1 2 1e9", "Playerbot send: the position is outside the map.")]
    [InlineData("defend Pfbadaa at 9999 1 2 3 40", "Playerbot defend: unknown map 9999.")]
    [InlineData("defend Pfbadaa at 0 NaN 2 3 40", "Playerbot defend: x, y and z must be finite numbers.")]
    [InlineData("defend Pfbadaa at 0 99999 2 3 40", "Playerbot defend: the position is outside the map.")]
    public async Task AtWithBadInput_IsRefusedWithAClearMessage_AndNothingChanges(string command, string expected)
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        Guid id = await world.AddBotAsync("Pfbadaa", 30);
        (float X, float Y, float Z) before = await world.PositionAsync("Pfbadaa");

        string[] reply = await world.Console.RunAsync(".playerbot " + command);

        Assert.Equal([expected], reply);
        Assert.Equal(before, await world.PositionAsync("Pfbadaa"));
        Assert.Null(await world.Host.OnWorldAsync(() => world.Bots.FindBrain(id)!.Defend));
    }

    [Theory]
    [InlineData("defend Pfsynaa at 0 1 2 3")]
    [InlineData("defend Pfsynaa at 0 1 2 3 abc")]
    [InlineData("defend Pfsynaa at 0 1 2 3 40 to Nobody")]
    public async Task AtWithTheWrongShape_ShowsTheHelp(string command)
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Pfsynaa", 30);

        string[] reply = await world.Console.RunAsync(".playerbot " + command);

        Assert.StartsWith("Syntax: .playerbot " + command.Split(' ')[0], reply[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HereFromTheConsole_StillNeedsAPlayer()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Pfherea", 30);

        string[] send = await world.Console.RunAsync(".playerbot send Pfherea here");
        string[] defend = await world.Console.RunAsync(".playerbot defend Pfherea here 40");

        Assert.Equal([ArcaneCore.World.Gm.Core.GmStrings.ConsoleNeedsPlayer], send);
        Assert.Equal([ArcaneCore.World.Gm.Core.GmStrings.ConsoleNeedsPlayer], defend);
    }

    [Theory]
    [InlineData("at 0 1 2")]
    [InlineData("at 0 1 2 3 4")]
    [InlineData("at 0 1 2 3 to Nobody")]
    public async Task SendWithAnAtThatIsNotTheFourNumberForm_IsTakenAsAPlaceName(string tail)
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Pfnameaa", 30);

        string[] reply = await world.Console.RunAsync(".playerbot send Pfnameaa " + tail);

        Assert.Equal([GmStrings.TeleNotFound], reply);
    }

    [Fact]
    public void TheParser_TakesAtAsThePositionFormOnlyWithExactlyFourWords()
    {
        Assert.True(PlayerbotFleetSelection.TryParse("Bot at the docks", FleetVerb.Send, out FleetRequest place));
        Assert.Null(place.At);
        Assert.Equal("at the docks", place.Head);
        Assert.True(PlayerbotFleetSelection.TryParse("Bot at 0 1 2 3", FleetVerb.Send, out FleetRequest spot));
        Assert.Equal(["0", "1", "2", "3"], spot.At!);
        Assert.Equal("at", spot.Head);
        Assert.True(PlayerbotFleetSelection.TryParse("Bot at 0 1 2 3 force", FleetVerb.Send, out FleetRequest forced));
        Assert.True(forced.Force);
        Assert.Equal(["0", "1", "2", "3"], forced.At!);
    }

    [Fact]
    public async Task SendAt_TakesTheSlotHeightsFromTheCollisionFloorNearTheGivenZ_AsHereDoes()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.Host.World.InvokeAsync(() =>
        {
            // Two floors above each other at every point (an upper deck and a lower one): the one the search from the given z finds counts.
            WorldCollision.Of(world.Host.World).Install(lineOfSight: new TwoFloors(100f, 60f));
            return true;
        });
        await world.AddBotAsync("Pfflraa", 100);
        await world.AddBotAsync("Pfflrab", 110);

        string[] low = await world.Console.RunAsync(".playerbot send Pfflraa at 0 -8000 1000 62");
        string[] high = await world.Console.RunAsync(".playerbot send Pfflrab at 0 -8000 1100 98");

        Assert.Equal(["Playerbot send: matched=1 moved=1 skipped=0"], low);
        Assert.Equal(["Playerbot send: matched=1 moved=1 skipped=0"], high);
        await world.WaitNearAsync("Pfflraa", 0, -8000f, 1000f, 60f, 19f);
        await world.WaitNearAsync("Pfflrab", 0, -8000f, 1100f, 100f, 19f);
    }

    [Fact]
    public async Task SendAt_ToABattlegroundMap_IsRefused_AsForAGameTele()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(configureServices: s => s.AddSingleton<IMapDataStore, MapsWithABattleground>());
        await world.AddBotAsync("Pfbgaaa", 30);
        (float X, float Y, float Z) before = await world.PositionAsync("Pfbgaaa");

        string[] reply = await world.Console.RunAsync(".playerbot send Pfbgaaa at 489 1 2 3");

        Assert.Equal([TeleportCommandsText.Battleground], reply);
        Assert.Equal(before, await world.PositionAsync("Pfbgaaa"));
    }

    [Fact]
    public async Task DefendAt_ToABattlegroundMap_IsRefused_AndSetsNoPost()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(configureServices: s => s.AddSingleton<IMapDataStore, MapsWithABattleground>());
        Guid id = await world.AddBotAsync("Pfbgbaa", 30);

        string[] reply = await world.Console.RunAsync(".playerbot defend Pfbgbaa at 489 1 2 3 40");

        Assert.Equal([TeleportCommandsText.Battleground], reply);
        Assert.Null(await world.Host.OnWorldAsync(() => world.Bots.FindBrain(id)!.Defend));
    }

    [Fact]
    public async Task SendAt_ToAnInstanceMap_SkipsTheBotsWithoutAnInstance()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await world.AddBotAsync("Pfinsaa", 30);
        (float X, float Y, float Z) before = await world.PositionAsync("Pfinsaa");

        string[] reply = await world.Console.RunAsync(".playerbot send Pfinsaa at 36 -16 -383 61");

        Assert.Equal("Playerbot send: matched=1 moved=0 skipped=1 (instance=1)", reply[0]);
        Assert.Equal(before, await world.PositionAsync("Pfinsaa"));
    }

    [Fact]
    public async Task SendAt_AboveTheBulkCap_NeedsForce()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(options => options.MaxBulkCommand = 1);
        await world.AddBotAsync("Pffoaaa", 30);
        await world.AddBotAsync("Pffoaab", 33);

        string[] refused = await world.Console.RunAsync(".playerbot send Pffoaa* at 0 -8000 1200 100");
        string[] forced = await world.Console.RunAsync(".playerbot send Pffoaa* at 0 -8000 1200 100 force");

        Assert.Equal(["Playerbot send: 2 bots match; give a count <= 1 or add 'force'."], refused);
        Assert.Equal(["Playerbot send: matched=2 moved=2 skipped=0"], forced);
        await world.WaitNearAsync("Pffoaaa", 0, -8000f, 1200f, 100f, 19f);
        await world.WaitNearAsync("Pffoaab", 0, -8000f, 1200f, 100f, 19f);
    }

    private static class TeleportCommandsText
    {
        public const string Battleground = "You cannot teleport to a battleground map.";
    }

    private sealed class MapsWithABattleground : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(InMemoryMapDataStore.Content with
            {
                Maps = [.. InMemoryMapDataStore.Content.Maps, new MapTemplate(489, 0, MapType.Battleground, 3277, 20, 0, -1, 0, 0, "Warsong Gulch", "")],
            });
    }

    /// <summary>Two model floors above each other: the highest one at or below the probe height (within the search) answers.</summary>
    private sealed class TwoFloors(float upper, float lower) : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance)
            => z >= upper && z - upper <= maxSearchDistance ? upper : z >= lower && z - lower <= maxSearchDistance ? lower : null;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }
}
