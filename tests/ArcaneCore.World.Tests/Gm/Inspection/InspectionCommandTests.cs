using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Tests.Creatures;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Inspection;

public sealed class InspectionCommandTests
{
    [Fact]
    public void CommandsAreGameMasterOnly()
    {
        CommandTable table = ChatCommands.CreateTable();
        // .guid is the lookup lane's, a level 2 (Moderator) command as in the cores it follows.
        foreach (string name in new[] { "distance", "angle", "pinfo" })
        {
            Assert.Null(table.Resolve(name, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(name, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task DistanceAngleAndPinfoWorkForTheCallerWithoutSelection_GuidNeedsASelection()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPGM", "Inspgm", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        // vmangos HandleGUIDCommand reads the selection only (LANG_NO_SELECTION without one).
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".guid");
        Assert.Equal("No selection.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".distance");
        Assert.Contains("Distance 0.000", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".angle");
        Assert.Contains("Angle 0.000", (await gm.ReadChatAsync()).Text);
        // .pinfo is the GM audit lane's (AuditCommandTests covers its lines); its first line names the caller.
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pinfo");
        Assert.Contains("(online, guid:", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidArgumentsReturnUsageInsteadOfReadingSelection()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPBAD", "Inspbad", AccountSecurity.GameMaster);
        await gm.CollectAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".distance extra");
        Assert.StartsWith("Syntax:", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task InspectionCommandsReadASelectedCreature()
    {
        await using WorldTestHost host = StartWithWolf(out _);
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPCR", "Inspcr", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Creature creature = await host.OnWorldAsync(() =>
        {
            CreatureWorldFeature feature = host.WorldServices.GetRequiredService<CreatureWorldFeature>();
            return feature.FindSystem(0)!.Creatures.Last();
        });
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Inspcr")!.Selection = creature.Guid);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".guid");
        string guid = (await gm.ReadChatAsync()).Text;
        Assert.Contains($"{creature.Guid.Value:X16}", guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".distance");
        Assert.StartsWith("Distance ", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".angle");
        Assert.StartsWith("Angle ", (await gm.ReadChatAsync()).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PinfoRefusesHigherSecuritySelectionWithoutLeakingState()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("INSPSEC", "Inspsec", AccountSecurity.GameMaster);
        await using WorldTestClient administrator = await host.EnterWorldAsync("INSPADM", "Inspadm", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await administrator.CollectAsync();
        Player target = await host.PlayerAsync("Inspadm");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Inspsec")!.Selection = target.Guid);

        // .guid shows any selection's GUID (vmangos HandleGUIDCommand has no security check), so it is not in this list.
        foreach (string command in new[] { ".distance", ".angle", ".pinfo" })
        {
            await gm.SendChatAsync(ChatType.Say, Language.Common, command);
            string reply = (await gm.ReadChatAsync()).Text;
            Assert.Equal("You have low security level for this.", reply);
            Assert.DoesNotContain("GUID", reply, StringComparison.Ordinal);
            Assert.DoesNotContain("type player", reply, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(await administrator.CollectAsync(TimeSpan.FromMilliseconds(200)),
            packet => packet.Opcode == WorldOpcode.SmsgMessagechat);
    }


    private static WorldTestHost StartWithWolf(out CreatureTestContext context)
    {
        var wolf = new CreatureTemplate
        {
            Entry = 299, Name = "Young Wolf", MinLevel = 2, MaxLevel = 2,
            DisplayIds = [903], Faction = 32, CreatureType = 1, Family = 1,
            MinLevelHealth = 55, MaxLevelHealth = 55,
        };
        var spawn = new CreatureSpawn { Guid = 4242, Entry = 299, MapId = 0, X = -8940f, Y = -132f, Z = 83.5f };
        context = new CreatureTestContext(new CreatureContent([wolf], [spawn], [], [], []));
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
