using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Gm.Manipulate;
using ArcaneCore.World.Tests.Gm.Inspect;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Manipulate;

public sealed class ManipulateCommandTests
{
    [Fact]
    public void AlterAndAiAreGameMasterGroups_AlterFieldIsAdministrator()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "alter health", "alter power", "alter unitflag", "alter standstate", "alter level", "alter react", "alter threat", "alter home", "alter respawn",
            "ai evade", "ai attack", "ai stop", "ai home", "ai idle", "ai come", "ai follow", "ai flee", "ai cast", "ai event", "ai callhelp", "ai reset", "creature clone" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }

        Assert.Null(table.Resolve("alter field", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("alter field", AccountSecurity.Administrator));
    }

    [Theory]
    [InlineData("Stunned", (uint)UnitFlags.Stunned)]
    [InlineData("notselectable", (uint)UnitFlags.NotSelectable)]
    [InlineData("0x00040000", (uint)UnitFlags.Stunned)]
    public void UnitFlagsParseByNameOrSingleHexBit(string text, uint expected) => Assert.Equal(expected, AlterCommands.ParseUnitFlag(text));

    [Theory]
    [InlineData("None")]
    [InlineData("0x3")]
    [InlineData("4")]
    [InlineData("bogus")]
    public void UnitFlagsRejectNoneMultiBitAndUnknown(string text) => Assert.Null(AlterCommands.ParseUnitFlag(text));

    [Theory]
    [InlineData("50%", 200u, 100u)]
    [InlineData("150", 200u, 150u)]
    [InlineData("100%", 55u, 55u)]
    public void AmountsTakeValuesAndPercentages(string text, uint max, uint expected)
    {
        Assert.True(AlterCommands.TryAmount(text, max, out uint value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public async Task SettersChangeTheSelectedCreature()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("SETWOLF", "Setwolf", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await SelectWolfAsync(host, "Setwolf");

        Assert.Contains(await RunAsync(gm, ".alter health 50%"), l => l.EndsWith("health set to 28/55.", StringComparison.Ordinal));
        Assert.Equal(28u, await host.OnWorldAsync(() => wolf.Health));
        Assert.Contains(await RunAsync(gm, ".alter level 12"), l => l.EndsWith("level set to 12.", StringComparison.Ordinal));
        Assert.Equal(12, await host.OnWorldAsync(() => wolf.Level));
        Assert.Contains(await RunAsync(gm, ".alter react passive"), l => l.EndsWith("react state set to Passive.", StringComparison.Ordinal));
        Assert.Equal(CreatureReactState.Passive, await host.OnWorldAsync(() => wolf.ReactState));
        Assert.Contains(await RunAsync(gm, ".alter unitflag stunned on"), l => l.Contains("unit flag Stunned set", StringComparison.Ordinal));
        Assert.True(await host.OnWorldAsync(() => (wolf.UnitFlags & UnitFlags.Stunned) != 0));
        Assert.Contains(await RunAsync(gm, ".alter unitflag stunned off"), l => l.Contains("unit flag Stunned cleared", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter standstate sit"), l => l.EndsWith("stand state set to Sit.", StringComparison.Ordinal));
        Assert.Equal(StandState.Sit, await host.OnWorldAsync(() => wolf.StandState));
        Assert.Contains(await RunAsync(gm, ".alter home"), l => l.Contains("home set to", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter respawn 30"), l => l.EndsWith("respawns 30 s after its next death.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter unitflag bogus on"), l => l.StartsWith("Unknown unit flag 'bogus'", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".alter react furious"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThreatAttackEvadeAndCloneDriveTheCreature()
    {
        await using WorldTestHost host = InspectCommandTests.StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("AIWOLF", "Aiwolf", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await SelectWolfAsync(host, "Aiwolf");
        Player me = await host.PlayerAsync("Aiwolf");

        Assert.Contains(await RunAsync(gm, ".alter threat 500"), l => l.Contains("is now 500.0", StringComparison.Ordinal));
        Assert.Equal(500f, await host.OnWorldAsync(() => wolf.Combat.Threat.GetThreat(me)));
        Assert.Contains(await RunAsync(gm, ".ai evade"), l => l.EndsWith("evades.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai attack"), l => l.Contains("attacks Aiwolf", StringComparison.Ordinal) || l.Contains("cannot attack", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai stop"), l => l.EndsWith("stops fighting.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai idle"), l => l.EndsWith("is idle.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai follow 3"), l => l.EndsWith("follows you at 3 yards.", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai callhelp 5"), l => l.Contains("called for help within 5 yards", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".ai event 5"), l => l.Contains("got AI event 5", StringComparison.Ordinal));

        int before = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Count);
        Assert.Contains(await RunAsync(gm, ".creature clone 3"), l => l == "Placed 3 temporary Young Wolf (entry 299).");
        Assert.Equal(before + 3, await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Count));
        Assert.Contains(await RunAsync(gm, ".ai reset"), l => l.EndsWith("was respawned with a fresh AI.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SetHealthAndPowerWorkOnTheInvoker()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SETSELF", "Setself", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Player me = await host.PlayerAsync("Setself");
        Assert.Contains(await RunAsync(gm, ".alter health 1"), l => l.StartsWith("Setself health set to 1/", StringComparison.Ordinal));
        Assert.Equal(1u, await host.OnWorldAsync(() => me.Health));
        Assert.NotEmpty(await RunAsync(gm, ".alter power 0"));
        Assert.Contains(await RunAsync(gm, ".alter level 5"), l => l == "Select a creature first.");
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
