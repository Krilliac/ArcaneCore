using System.Globalization;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
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
    [InlineData("send Pfsynaa at 0 1 2")]
    [InlineData("send Pfsynaa at 0 1 2 3 4")]
    [InlineData("send Pfsynaa at 0 1 2 3 to Nobody")]
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
}
