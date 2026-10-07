using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Items;
using ArcaneCore.Kernel.Skills;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class ItemEnchantmentCombatProcWorldTests
{
    [Fact]
    public async Task WorldLoader_UsesActiveSkillsRankChainForFirstRankPpmOverride()
    {
        const uint first = 49611;
        const uint higher = 49612;
        var items = new ItemTestContent();
        SpellContent content = SpellTestServices.Content();
        using (items.Use())
        {
            WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(new SkillCatalog([], [], [], [
                    new SkillLineAbilityRecord(1, 8, first, 0, 0, 0, higher, 0, 0, 0),
                    new SkillLineAbilityRecord(2, 8, higher, 0, 0, 0, 0, 0, 0, 0)]));
                services.AddSingleton<IItemEnchantProcStore>(new PpmStore([new ItemEnchantProc(first, 6)]));
                services.AddSingleton<ISpellContentStore>(new Store(content));
            });
            await using (host)
            {
                await host.OnWorldAsync(() => { });
                SpellFeature feature = host.WorldServices.GetRequiredService<SpellFeature>();
                Assert.Equal(6, feature.System.ItemEnchantments.PpmRate(higher));
            }
        }
    }

    [Fact]
    public async Task AttackSwing_UsesEquippedWeaponGuidTargetsVictimAndClearsTemporaryCharge()
    {
        const uint enchantment = 49602;
        const uint proc = 49601;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 49603, Class = 2, SubClass = 7, InventoryType = 21, Delay = 1000, Stackable = 1 });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, 49603, 1));
        SpellContent content = SpellTestServices.Content();
        content = content with { Spells = [.. content.Spells, new SpellTemplateRow { Id = proc, SpellName = "Enchant damage", RangeIndex = 4,
            Effect1 = 2, EffectBasePoints1 = 9, EffectImplicitTargetA1 = 6, StartRecoveryCategory = 0, StartRecoveryTime = 0 }] };
        using (items.Use())
        {
            WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<ISpellContentStore>(new Store(content));
                services.AddSingleton<IItemEnchantmentCatalog>(new ItemEnchantmentCatalog([
                    new ItemEnchantmentDefinition(enchantment, [new ItemEnchantmentEffect(1, proc, 100)])]));
            });
            await using (host)
            await using (WorldTestClient attacker = await host.EnterWorldAsync("ENCHANTATT", "Enchantatt"))
            await using (WorldTestClient victim = await host.EnterWorldAsync("ENCHANTVIC", "Enchantvic"))
            {
                await host.OnWorldAsync(() =>
                {
                    CombatHooks hooks = new HostileHooks();
                    CombatHooks.Register(host.World, hooks);
                    host.World.GetMap(0).Combat.Hooks = hooks;
                    Player player = host.World.FindOnlinePlayer("Enchantatt")!;
                    Item weapon = player.Inventory.Equipped.Single().Item;
                    weapon.SetUInt32(UpdateFields.ItemFieldEnchantment + 3, enchantment);
                    weapon.SetUInt32(UpdateFields.ItemFieldEnchantment + 4, 0);
                    weapon.SetUInt32(UpdateFields.ItemFieldEnchantment + 5, 1);
                });
                ulong weaponGuid = await host.PlayerStateAsync("Enchantatt", p => p.Inventory.Equipped.Single().Item.Guid.Value);
                ulong victimGuid = await host.PlayerStateAsync("Enchantvic", p => p.Guid.Value);
                await attacker.CollectAsync();
                await attacker.SendAsync(WorldOpcode.CmsgAttackswing, BitConverter.GetBytes(victimGuid));
                byte[] go = await attacker.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
                Assert.Equal(weaponGuid, new PacketReader(go).ReadPackedGuid());
                Assert.Equal(0u, await host.PlayerStateAsync("Enchantatt", p => p.Inventory.Equipped.Single().Item.EnchantmentId(1)));
            }
        }
    }

    private sealed class HostileHooks : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => false;
        public override bool CanAttack(Unit attacker, Unit victim) => !ReferenceEquals(attacker, victim) && victim.IsInWorld && ReferenceEquals(attacker.Map, victim.Map);
    }
    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PpmStore(IReadOnlyList<ItemEnchantProc> rows) : IItemEnchantProcStore
    {
        public Task<IReadOnlyList<ItemEnchantProc>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(rows);
    }
}
