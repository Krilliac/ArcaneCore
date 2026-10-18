using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Inspect;

public sealed class InspectCommandTests
{
    private static readonly string[] Subcommands = ["unit", "stats", "combat", "threat", "ai", "motion", "save", "fields", "map", "spell"];

    [Fact]
    public void InspectIsAGameMasterGroup()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string name in Subcommands)
        {
            Assert.Null(table.Resolve("inspect " + name, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve("inspect " + name, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task UnitGettersReadTheInvokerWithoutASelection()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPSELF", "Inspself", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        string[] unit = await RunAsync(gm, ".inspect unit");
        Assert.StartsWith("Inspself: player", unit[0], StringComparison.Ordinal);
        Assert.Contains(unit, l => l.StartsWith("unit flags 0x", StringComparison.Ordinal));
        Assert.Contains(unit, l => l.StartsWith("speeds: walk", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect stats"), l => l.StartsWith("Inspself stats: str", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect combat"), l => l.StartsWith("Inspself: in combat False", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect threat"), l => l.StartsWith("Inspself is on 0 threat list(s)", StringComparison.Ordinal));
        string[] fields = await RunAsync(gm, ".inspect fields 0 3");
        Assert.Equal(4, fields.Length);
        Assert.StartsWith("  [0] 0x", fields[1], StringComparison.Ordinal);
        Assert.Contains(await RunAsync(gm, ".inspect save"), l => l.StartsWith("Inspself (character ", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect map"), l => l.StartsWith("map 0 instance 0", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect spell 999999"), l => l == "Spell 999999 does not exist.");
        Assert.Contains(await RunAsync(gm, ".inspect ai"), l => l == "Select a creature first.");
        Assert.Contains(await RunAsync(gm, ".inspect fields x"), l => l.StartsWith("Syntax:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatureGettersReadAISelectionMotionAndThreat()
    {
        await using WorldTestHost host = StartWithWolf();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPWOLF", "Inspwolf", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        Creature wolf = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<CreatureWorldFeature>().FindSystem(0)!.Creatures.Last());
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Inspwolf")!.Selection = wolf.Guid);

        string[] unit = await RunAsync(gm, ".inspect unit");
        Assert.StartsWith("Young Wolf: creature entry 299", unit[0], StringComparison.Ordinal);
        string[] ai = await RunAsync(gm, ".inspect ai");
        Assert.Contains(ai, l => l.StartsWith("Young Wolf (entry 299, guid", StringComparison.Ordinal) && l.Contains(" AI ", StringComparison.Ordinal));
        Assert.Contains(ai, l => l.StartsWith("react ", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect motion"), l => l.Contains("motion: current", StringComparison.Ordinal));
        Assert.Contains(await RunAsync(gm, ".inspect threat"), l => l == "Young Wolf has an empty threat list.");
        Assert.Contains(await RunAsync(gm, ".inspect stats"), l => l.StartsWith("rates: health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHigherSecurityPlayerIsRefused()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPLOW", "Insplow", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("INSPHIGH", "Insphigh", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();
        Player target = await host.PlayerAsync("Insphigh");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Insplow")!.Selection = target.Guid);
        foreach (string command in new[] { ".inspect unit", ".inspect stats", ".inspect fields", ".inspect save" })
        {
            Assert.Contains(await RunAsync(gm, command), l => l == "You have low security level for this.");
        }
    }

    private static async Task<string[]> RunAsync(WorldTestClient gm, string command)
    {
        await gm.SendChatAsync(ChatType.Say, Language.Common, command);
        return await gm.CollectChatLinesAsync();
    }

    internal static WorldTestHost StartWithWolf()
    {
        var wolf = new CreatureTemplate
        {
            Entry = 299, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2,
            DisplayIds = [903], Faction = 32, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        var spawn = new CreatureSpawn { Guid = 4242, Entry = 299, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        var context = new CreatureTestContext(new CreatureContent([wolf], [spawn], [], [], []));
        CreatureTestStore.Current.Value = context;
        try
        {
            return WorldTestHost.Start();
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }
    }
}
