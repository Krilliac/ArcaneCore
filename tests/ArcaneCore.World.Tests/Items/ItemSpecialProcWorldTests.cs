using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class ItemSpecialProcWorldTests
{
    [Fact]
    public async Task CombatRangeCast_ProducesSpecialItemProcWithExactWeaponGuid()
    {
        const uint special = 49530;
        const uint proc = 49531;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 49532, Class = 2, SubClass = 7, InventoryType = 21,
            Delay = 1000, Stackable = 1, Spells = [new ItemSpell(proc, 2, 3, 0, 1000, 77, 1000)] });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, 49532, 1));
        SpellContent baseContent = SpellTestServices.Content();
        SpellTemplateRow specialRow = new()
        {
            Id = special, SpellName = "Special weapon cast", RangeIndex = SpellConstants.RangeIndexCombat,
            EquippedItemClass = 2, Effect1 = 3, EffectBasePoints1 = -1,
            EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 6,
            StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        SpellTemplateRow procRow = new()
        {
            Id = proc, SpellName = "Special weapon proc", RangeIndex = SpellConstants.RangeIndexCombat,
            Effect1 = 3, EffectBasePoints1 = -1,
            EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 6,
            ProcChance = 100, StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        WorldTestHost host;
        using (items.Use())
        {
            SpellContent content = baseContent with
            {
                Spells = [.. baseContent.Spells, specialRow, procRow],
                CreateSpells = [.. baseContent.CreateSpells, new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = special }],
            };
            host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(new Store(content)));
        }

        await using (host)
        await using (WorldTestClient attacker = await host.EnterWorldAsync("SPECIALATT", "Specialatt"))
        await using (WorldTestClient victim = await host.EnterWorldAsync("SPECIALVIC", "Specialvic"))
        {
            await host.OnWorldAsync(() =>
            {
                var hooks = new HostileHooks();
                CombatHooks.Register(host.World, hooks);
                host.World.GetMap(0).Combat.Hooks = hooks;
            });
            ulong weaponGuid = await host.PlayerStateAsync("Specialatt", p => p.Inventory.Equipped.Single().Item.Guid.Value);
            ulong victimGuid = await host.PlayerStateAsync("Specialvic", p => p.Guid.Value);
            await attacker.CollectAsync();
            await attacker.SendAsync(WorldOpcode.CmsgCastSpell, Cast(special, victimGuid));

            byte[] firstGo = await attacker.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            byte[] secondGo = await attacker.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            bool procSeen = new PacketReader(firstGo).ReadPackedGuid() == weaponGuid
                || new PacketReader(secondGo).ReadPackedGuid() == weaponGuid;
            Assert.True(procSeen);
            Assert.Equal(3, await host.PlayerStateAsync("Specialatt", p => p.Inventory.Equipped.Single().Item.GetInt32(UpdateFields.ItemFieldSpellCharges)));
        }
    }

    private static byte[] Cast(uint spell, ulong target)
    {
        var writer = new PacketWriter(32);
        writer.WriteUInt32(spell);
        SpellCastTargets.ForUnit(new ObjectGuid(target)).Write(writer);
        return writer.ToArray();
    }

    private sealed class HostileHooks : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => false;
        public override bool CanAttack(Unit attacker, Unit victim)
            => !ReferenceEquals(attacker, victim) && attacker.IsInWorld && victim.IsInWorld && ReferenceEquals(attacker.Map, victim.Map);
    }

    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
