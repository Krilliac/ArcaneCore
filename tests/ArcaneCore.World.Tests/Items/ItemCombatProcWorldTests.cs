using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class ItemCombatProcWorldTests
{
    [Fact]
    public async Task WeaponHit_TriggersItemCombatSpellWithExactCastItemGuid()
    {
        const uint proc = 49411;
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = 49412, Class = 2, SubClass = 7, InventoryType = 21, Delay = 1000, Stackable = 1,
            Spells = [new ItemSpell(proc, 2, 3, 0, 1000, 77, 1000)] });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, 49412, 1));
        SpellContent baseContent = SpellTestServices.Content();
        var row = new SpellTemplateRow { Id = proc, SpellName = "Combat Proc", RangeIndex = 4, Effect1 = 3, EffectBasePoints1 = -1,
            EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 6, ProcChance = 100, StartRecoveryCategory = 0, StartRecoveryTime = 0 };
        WorldTestHost host;
        using (items.Use()) host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(new Store(baseContent with { Spells = [.. baseContent.Spells, row] })));
        await using (host)
        await using (WorldTestClient attacker = await host.EnterWorldAsync("PROCATT", "Procatt"))
        await using (WorldTestClient victim = await host.EnterWorldAsync("PROCVIC", "Procvic"))
        {
            await host.OnWorldAsync(() =>
            {
                var hooks = new HostileHooks();
                CombatHooks.Register(host.World, hooks);
                host.World.GetMap(0).Combat.Hooks = hooks;
            });
            ulong guid = await host.PlayerStateAsync("Procatt", p => p.Inventory.Equipped.Single().Item.Guid.Value);
            ulong victimGuid = await host.PlayerStateAsync("Procvic", p => p.Guid.Value);
            await attacker.CollectAsync();
            await attacker.SendAsync(WorldOpcode.CmsgAttackswing, BitConverter.GetBytes(victimGuid));
            byte[] go = await attacker.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            Assert.Equal(guid, new PacketReader(go).ReadPackedGuid());
        }
    }

    private sealed class HostileHooks : CombatHooks
    {
        public override bool IsFriendly(Unit a, Unit b) => false;
        public override bool CanAttack(Unit attacker, Unit victim)
            => !ReferenceEquals(attacker, victim) && victim.IsInWorld && ReferenceEquals(attacker.Map, victim.Map);
    }

    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
