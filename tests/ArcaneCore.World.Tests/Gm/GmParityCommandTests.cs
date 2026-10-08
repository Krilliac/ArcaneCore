using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Creatures;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

public sealed class GmParityCommandTests
{
    [Fact]
    public void NewCommands_UseTheVmangosRetailLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "die", "damage", "additemset", "modify scale", "unaura", "npc set flag" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }

        ChatCommand aura = Assert.Single(table.Roots, command => command.Name == "aura");
        Assert.Equal(4, aura.RequiredLevel(table.Gm));
        Assert.Equal("aura", table.Lookup("aura", AccountSecurity.Administrator).Path);
        Assert.Equal("modify speed", table.Lookup("modify speed", AccountSecurity.GameMaster).Path);
        Assert.Equal(2, table.Lookup("modify speed", AccountSecurity.GameMaster).Command?.RequiredLevel(table.Gm));
    }

    [Fact]
    public async Task Die_KillsTheSelectedPlayer_AndRefusesHigherRank()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("PARITYGM", "Paritygm", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await host.EnterWorldAsync("PARITYV", "Parityvictim");
        await using WorldTestClient admin = await host.EnterWorldAsync("PARITYA", "Parityadmin", AccountSecurity.Administrator);
        Player victimPlayer = await host.PlayerAsync("Parityvictim");
        Player adminPlayer = await host.PlayerAsync("Parityadmin");

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Paritygm")!.Selection = adminPlayer.Guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".die");
        Assert.Equal("You have low security level for this.", (await gm.ReadChatAsync()).Text);
        Assert.True(await host.PlayerStateAsync("Parityadmin", p => p.IsAlive));

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Paritygm")!.Selection = victimPlayer.Guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".die");
        await host.WaitForWorldAsync(() => !host.World.FindOnlinePlayer("Parityvictim")!.IsAlive, "selected player's death");
    }

    [Fact]
    public async Task ModifyScale_ChangesSelectedPlayersScale_AndRejectsOutOfRange()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SCALEGM", "Scalegm", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await host.EnterWorldAsync("SCALEV", "Scalevictim");
        Player target = await host.PlayerAsync("Scalevictim");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Scalegm")!.Selection = target.Guid);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify scale 2");
        Assert.Contains("2", (await gm.ReadChatAsync()).Text);
        Assert.Equal(2f, await host.PlayerStateAsync("Scalevictim", p => p.GetFloat(UpdateFields.ObjectFieldScaleX)));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify scale 101");
        Assert.Equal("Incorrect values.", (await gm.ReadChatAsync()).Text);
        Assert.Equal(2f, await host.PlayerStateAsync("Scalevictim", p => p.GetFloat(UpdateFields.ObjectFieldScaleX)));
    }

    [Fact]
    public async Task AuraAndUnaura_ChangeSelectedPlayersAuras_WithRankCheck()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("AURAPARITY", "Auraparity", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("AURAPV", "Aurapvictim");
        Player target = await host.PlayerAsync("Aurapvictim");
        await host.OnWorldAsync(() =>
        {
            host.WorldServices.GetRequiredService<SpellFeature>().System.Store = new SpellStore([AuraSpell()], [], []);
            host.World.FindOnlinePlayer("Auraparity")!.Selection = target.Guid;
        });

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".aura 991931");
        await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(target, 991931), "aura application");
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".unaura 991931");
        await host.WaitForWorldAsync(() => !host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(target, 991931), "aura removal");
    }

    [Fact]
    public async Task AddItemSet_GrantsEachMatchingItem_AndLeavesOtherSetsAlone()
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 991941, Name = "Set helm", Class = 4, Quality = 1, SetId = 711 },
            new ItemTemplate { Entry = 991942, Name = "Set boots", Class = 4, Quality = 1, SetId = 711 },
            new ItemTemplate { Entry = 991943, Name = "Other helm", Class = 4, Quality = 1, SetId = 712 },
        ]);
        using (content.Use())
        {
            await using WorldTestHost host = WorldTestHost.Start();
            await using WorldTestClient gm = await host.EnterWorldAsync("SETGM", "Setgm", AccountSecurity.GameMaster);
            await gm.SendChatAsync(ChatType.Say, Language.Common, ".additemset 711");
            await host.WaitForWorldAsync(() =>
                host.World.FindOnlinePlayer("Setgm")!.Inventory.GetItemCount(991941) == 1
                && host.World.FindOnlinePlayer("Setgm")!.Inventory.GetItemCount(991942) == 1,
                "both item-set pieces");
            Assert.Equal(0u, await host.PlayerStateAsync("Setgm", p => p.Inventory.GetItemCount(991943)));
        }
    }

    [Fact]
    public async Task Damage_ReducesSelectedPlayersHealth_AndChecksRank()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DMGGM", "Dmggm", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await host.EnterWorldAsync("DMGV", "Dmgvictim");
        await using WorldTestClient admin = await host.EnterWorldAsync("DMGA", "Dmgadmin", AccountSecurity.Administrator);
        Player target = await host.PlayerAsync("Dmgvictim");
        Player higher = await host.PlayerAsync("Dmgadmin");
        uint before = await host.PlayerStateAsync("Dmgvictim", p => p.Health);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Dmggm")!.Selection = higher.Guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".damage 10");
        Assert.Equal("You have low security level for this.", (await gm.ReadChatAsync()).Text);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Dmggm")!.Selection = target.Guid);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".damage 10");
        Assert.NotEmpty(await gm.ReadUntilAsync(WorldOpcode.SmsgAttackerstateupdate));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Dmgvictim")!.Health <= before - 10, "damage to the selected player");
    }

    [Fact]
    public async Task ModifySpeed_OrdersAClientRunSpeedChange_AndCapsGameMasterRate()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("SPEEDGM", "Speedgm", AccountSecurity.GameMaster);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify speed 8");
        byte[] order = await gm.ReadUntilAsync(WorldOpcode.SmsgForceRunSpeedChange);
        Assert.Equal(Unit.BaseRunSpeed * 4, BitConverter.ToSingle(order.AsSpan(order.Length - 4)));
    }

    [Fact]
    public async Task NpcSetFlag_ChangesTheSelectedCreatureOnly()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("NPCFLAG", "Npcflag", AccountSecurity.GameMaster);
        Creature creature = await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Npcflag")!;
            Creature created = host.WorldServices.GetRequiredService<CreatureWorldFeature>().GetOrCreateSystem(player.Map!)
                .SpawnTemporary(new CreatureTemplate { Entry = 991951, Name = "Flag target", MinLevel = 1, MaxLevel = 1, DisplayIds = [903], Faction = 35 },
                    player.X, player.Y, player.Z, player.Orientation);
            player.Selection = created.Guid;
            return created;
        });
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".npc set flag 17");
        Assert.Contains("17", (await gm.ReadChatAsync()).Text);
        Assert.Equal(17u, await host.OnWorldAsync(() => creature.NpcFlags));
    }

    private static SpellInfo AuraSpell() => new()
    {
        Id = 991931,
        Name = "Parity Aura",
        Duration = new SpellDuration(60_000, 0, 60_000),
        Effects = [new SpellEffectInfo
        {
            Effect = SpellEffectName.ApplyAura,
            AuraType = AuraType.ModStat,
            BasePoints = 3,
            BaseDice = 1,
            DieSides = 1,
            TargetA = SpellImplicitTarget.UnitCaster,
        }, new(), new()],
    };
}
