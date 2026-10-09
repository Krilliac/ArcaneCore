using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.Creatures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Character;

public sealed class FactionCommandTests
{
    private static FactionTemplateCatalog Catalog() => new([new FactionTemplateRecord(35, 35, 0, 0, 0, 0),
        new FactionTemplateRecord(77, 77, 0, 0, 0, 0)]);

    [Fact]
    public void Commands_UseTheVmangosGmRank()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "modify faction", "npc set faction" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task ModifyFaction_ValidatesTheDbcRow_AndChecksSelectedPlayerRank()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services => services.AddSingleton(Catalog()));
        await using WorldTestClient gm = await host.EnterWorldAsync("FACGM", "Facgm", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await host.EnterWorldAsync("FACVICTIM", "Facvictim");
        await using WorldTestClient admin = await host.EnterWorldAsync("FACADMIN", "Facadmin", AccountSecurity.Administrator);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Facgm")!.Selection = host.World.FindOnlinePlayer("Facadmin")!.Guid);
        uint adminFaction = await host.PlayerStateAsync("Facadmin", p => p.FactionTemplate);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify faction 77");
        Assert.Equal("You have low security level for this.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(adminFaction, await host.PlayerStateAsync("Facadmin", p => p.FactionTemplate));

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Facgm")!.Selection = host.World.FindOnlinePlayer("Facvictim")!.Guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify faction 999");
        Assert.Equal("Faction template 999 does not exist.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify faction 77 1 2 3");
        Assert.Equal("Faction updated to 77.", (await gm.ReadChatAsync()).Text);
        Assert.Equal((77u, 1u, 2u, 3u), await host.PlayerStateAsync("Facvictim", p =>
            (p.FactionTemplate, p.GetUInt32(UpdateFields.UnitFieldFlags), p.GetUInt32(UpdateFields.UnitNpcFlags),
                p.GetUInt32(UpdateFields.UnitDynamicFlags))));
    }

    [Fact]
    public async Task NpcSetFaction_ChangesTheSelectedCreatureForItsCurrentLife()
    {
        var creature = new CreatureTemplate { Entry = 990077, Name = "Faction Target", Faction = 35, MinLevel = 1, MaxLevel = 1 };
        var spawn = new CreatureSpawn { Guid = 990077, Entry = creature.Entry, MapId = 0, X = -8948f, Y = -132f, Z = 83.5f };
        var testContext = new CreatureTestContext(new CreatureContent([creature], [spawn], [], [], []));
        CreatureTestStore.Current.Value = testContext;
        WorldTestHost host;
        try
        {
            host = WorldTestHost.Start(configureServices: services => services.AddSingleton(Catalog()));
        }
        finally
        {
            CreatureTestStore.Current.Value = null;
        }

        await using (host)
        {
            await using WorldTestClient gm = await host.EnterWorldAsync("NPCFACGM", "Npcfacgm", AccountSecurity.GameMaster);
            await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Npcfacgm")!.Selection =
                ObjectGuid.WithEntry(HighGuid.Unit, creature.Entry, spawn.Guid));
            await gm.SendChatAsync(ChatType.Say, Language.Common, ".npc set faction 77");
            Assert.Equal("Faction updated to 77.", (await gm.ReadChatAsync()).Text);
            Assert.Equal(77u, await host.OnWorldAsync(() => testContext.Feature!.FindSystem(0)!
                .FindCreature(ObjectGuid.WithEntry(HighGuid.Unit, creature.Entry, spawn.Guid))!.FactionTemplate));
        }
    }
}
