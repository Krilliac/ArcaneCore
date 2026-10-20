using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Party;

/// <summary>
/// <c>.playerbot defend</c> against a real world: an Administrator client types the command, managed bots take a post in memory.
/// Every bot of one command shares a post id; the radius is clamped; a big selection needs force.
/// </summary>
public sealed class PlayerbotDefendCommandTests
{
    [Fact]
    public async Task Here_SetsThePostAtTheInvokersPosition_WithOneIdForTheCommand_AndClearEndsIt()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("DEFCMDGM", "Defcmdgm");
        Guid a = await world.AddBotAsync("Defcmdaa", 30);
        Guid b = await world.AddBotAsync("Defcmdab", 35);

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot defend Defcmda* here 40");

        Assert.Equal(["Playerbot defend: matched=2 defending=2 skipped=0"], reply);
        (PlayerbotDefendPost? first, PlayerbotDefendPost? second) = await world.Host.OnWorldAsync(() => (world.Bots.FindBrain(a)!.Defend, world.Bots.FindBrain(b)!.Defend));
        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(40f, first.Radius);
        Assert.Equal(FleetWorld.BaseX, first.Centre.X, 1f);
        Assert.Equal(FleetWorld.BaseY, first.Centre.Y, 1f);

        reply = await FleetWorld.SayAsync(gm, ".playerbot defend Defcmdaa here 60");
        Assert.Equal(["Playerbot defend: matched=1 defending=1 skipped=0"], reply);
        PlayerbotDefendPost? other = await world.Host.OnWorldAsync(() => world.Bots.FindBrain(a)!.Defend);
        Assert.NotEqual(first.PostId, other!.PostId);

        reply = await FleetWorld.SayAsync(gm, ".playerbot defend Defcmda* clear");
        Assert.Equal(["Playerbot defend: matched=2 cleared=2 skipped=0"], reply);
        Assert.Null(await world.Host.OnWorldAsync(() => world.Bots.FindBrain(a)!.Defend));
        Assert.Null(await world.Host.OnWorldAsync(() => world.Bots.FindBrain(b)!.Defend));
    }

    [Theory]
    [InlineData("500", 200f)]
    [InlineData("3", 10f)]
    public async Task TheRadiusIsClamped(string requested, float expected)
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("DEFRADGM", "Defradgm");
        Guid id = await world.AddBotAsync("Defradaa", 30);

        List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot defend Defradaa here " + requested);

        Assert.Equal(["Playerbot defend: matched=1 defending=1 skipped=0"], reply);
        Assert.Equal(expected, (await world.Host.OnWorldAsync(() => world.Bots.FindBrain(id)!.Defend))!.Radius);
    }

    [Fact]
    public async Task ASelectionAboveTheBulkCap_NeedsForce()
    {
        await using FleetWorld world = await FleetWorld.StartAsync(options => options.MaxBulkCommand = 2);
        await using WorldTestClient gm = await world.AddGmAsync("DEFFORGM", "Defforgm");
        Guid[] ids = [await world.AddBotAsync("Defforaa", 30), await world.AddBotAsync("Defforab", 33), await world.AddBotAsync("Defforac", 36)];

        List<string> refused = await FleetWorld.SayAsync(gm, ".playerbot defend Deffora* here 40");
        Assert.Equal(["Playerbot defend: 3 bots match; add 'force' above 2."], refused);
        foreach (Guid id in ids) Assert.Null(await world.Host.OnWorldAsync(() => world.Bots.FindBrain(id)!.Defend));

        List<string> forced = await FleetWorld.SayAsync(gm, ".playerbot defend Deffora* here 40 force");
        Assert.Equal(["Playerbot defend: matched=3 defending=3 skipped=0"], forced);
    }

    [Fact]
    public async Task BadSyntaxShowsTheHelp_AndAnUnknownBotIsReported()
    {
        await using FleetWorld world = await FleetWorld.StartAsync();
        await using WorldTestClient gm = await world.AddGmAsync("DEFSYNGM", "Defsyngm");
        await world.AddBotAsync("Defsynaa", 30);

        foreach (string bad in new[] { "Defsynaa", "Defsynaa here", "Defsynaa here abc", "Defsynaa there 40", "Defsynaa here 40 to Defsyngm" })
        {
            List<string> reply = await FleetWorld.SayAsync(gm, ".playerbot defend " + bad, lines: 3);
            Assert.StartsWith("Syntax: .playerbot defend", reply[0], StringComparison.Ordinal);
            Assert.StartsWith(".playerbot defend", reply[1], StringComparison.Ordinal);
        }

        Assert.Equal(["No playerbot matches 'Nobodyhere'."], await FleetWorld.SayAsync(gm, ".playerbot defend Nobodyhere here 40"));
    }
}
