using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Manipulate;
using ArcaneCore.World.Tests.Gm.Inspect;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Manipulate;

/// <summary>
/// Characterises the "enum by name but not by number" guard behind <c>.alter unitflag/standstate/react</c> so the shared
/// name-parse helper cannot drift: numeric text is only accepted by the numeric branch of each site, never by the name branch.
/// </summary>
public sealed class EnumNameGuardCharacterisationTests
{
    [Theory]
    [InlineData("Stunned,Stunned", (uint)UnitFlags.Stunned)]
    [InlineData("stunned , STUNNED", (uint)UnitFlags.Stunned)]
    [InlineData("0X00040000", (uint)UnitFlags.Stunned)]
    [InlineData("0x80000000", (uint)UnitFlags.Immune)]
    [InlineData("Immune", (uint)UnitFlags.Immune)]
    [InlineData("Stunned,None", (uint)UnitFlags.Stunned)]
    public void UnitFlagAcceptsSingleBitNamesAndHex(string text, uint expected) => Assert.Equal(expected, AlterCommands.ParseUnitFlag(text));

    [Theory]
    [InlineData("Stunned,Pvp")]
    [InlineData("0x0")]
    [InlineData("0x80000001")]
    [InlineData("0xZZ")]
    [InlineData("0x")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("-1")]
    [InlineData("+4")]
    [InlineData(" 4")]
    [InlineData("4 ")]
    [InlineData("262144")]
    [InlineData("4294967295")]
    public void UnitFlagRejectsCombinationsBadHexAndSmallIntegerText(string text) => Assert.Null(AlterCommands.ParseUnitFlag(text));

    // Pins today's behaviour: decimal text above int.MaxValue slips past the int.TryParse guard and Enum.TryParse
    // reads it as the uint value 0x80000000 (a single bit). Item 1j changes this in its own tested commit; update then.
    [Fact]
    public void UnitFlagCurrentlyAcceptsDecimalTextAboveInt32Max() => Assert.Equal((uint)UnitFlags.Immune, AlterCommands.ParseUnitFlag("2147483648"));

    [Fact]
    public async Task ReactRejectsNumbersAndUndefinedNamesAndAcceptsNamesCaseInsensitively()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("ENUMREACT", "Enumreact", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await SelectWolfAsync(host, "Enumreact");

        Assert.Contains(await RunAsync(gm, ".alter react PASSIVE"), l => l.EndsWith("react state set to Passive.", StringComparison.Ordinal));
        foreach (string rejected in new[] { "0", "1", "2", "3", "255", "256", "-1", "+1", "nope" })
        {
            Assert.Contains(await RunAsync(gm, $".alter react {rejected}"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
            Assert.Equal(CreatureReactState.Passive, await host.OnWorldAsync(() => wolf.ReactState));
        }

        Assert.Contains(await RunAsync(gm, ".alter react aggressive"), l => l.EndsWith("react state set to Aggressive.", StringComparison.Ordinal));
        Assert.Equal(CreatureReactState.Aggressive, await host.OnWorldAsync(() => wolf.ReactState));
    }

    [Fact]
    public async Task StandStateTakesInRangeNumbersAndNamesButRejectsUndefinedValues()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("ENUMSTAND", "Enumstand", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await SelectWolfAsync(host, "Enumstand");

        Assert.Contains(await RunAsync(gm, ".alter standstate 3"), l => l.EndsWith("stand state set to Sleep.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter standstate 9"), l => l.EndsWith("stand state set to Custom.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter standstate KNEEL"), l => l.EndsWith("stand state set to Kneel.", StringComparison.Ordinal));
        Assert.Equal(StandState.Kneel, await host.OnWorldAsync(() => wolf.StandState));

        foreach (string rejected in new[] { "10", "255", "256", "-1", "nope" })
        {
            Assert.Contains(await RunAsync(gm, $".alter standstate {rejected}"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
            Assert.Equal(StandState.Kneel, await host.OnWorldAsync(() => wolf.StandState));
        }
    }

    private static async Task<Creature> SelectWolfAsync(WorldTestHost host, string name)
    {
        Creature wolf = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Last());
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(name)!.Selection = wolf.Guid);
        return wolf;
    }

    private static async Task<string[]> RunAsync(WorldTestClient gm, string command)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        return await gm.CollectChatLinesAsync();
    }
}
