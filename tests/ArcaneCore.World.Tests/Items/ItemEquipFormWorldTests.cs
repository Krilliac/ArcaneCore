using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Game;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

public sealed class ItemEquipFormWorldTests
{
    private const uint Battle = 49220;
    private const uint Defensive = 49221;
    private const uint EquipAura = 49222;
    private const uint ItemEntry = 49223;

    [Fact]
    public async Task StanceCast_ReconcilesEquippedItemAuraByFormAndKeepsOwner()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate { Entry = ItemEntry, Class = 4, InventoryType = 12, Stackable = 1,
            Spells = [new ItemSpell(EquipAura, 1, 0, 0, 0, 0, 0)] });
        items.Templates.StartingItems.Add(new StartingItem(1, 1, ItemEntry, 1));
        SpellContent baseContent = SpellTestServices.Content();
        SpellTemplateRow stance(uint id, string name, int form) => new()
        {
            // DurationIndex 21 (-1, permanent) as the real stances and Equip: spells. Without a SpellDuration row these auras lasted 0 ms and
            // expired on the next aura update: the waits raced a one-tick state, and "defensive removes item aura" passed only because the
            // Battle Stance aura had already expired (it hid ItemEquipSpells.FitsForm keeping the item aura in Defensive Stance).
            Id = id, SpellName = name, RangeIndex = 1, DurationIndex = 21, Effect1 = 6, EffectBasePoints1 = 0,
            EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1,
            EffectApplyAuraName1 = (uint)AuraType.ModShapeshift, EffectMiscValue1 = form,
            StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        SpellTemplateRow equip = new()
        {
            Id = EquipAura, SpellName = "Equip Aura", RangeIndex = 1, DurationIndex = 21, Effect1 = 6, EffectBasePoints1 = 0,
            EffectBaseDice1 = 1, EffectDieSides1 = 1, EffectImplicitTargetA1 = 1,
            EffectApplyAuraName1 = (uint)AuraType.Dummy, Stances = 1u << ((int)ShapeshiftForm.BattleStance - 1),
            StartRecoveryCategory = 0, StartRecoveryTime = 0,
        };
        SpellContent content = baseContent with
        {
            Spells = [.. baseContent.Spells, stance(Battle, "Battle", (int)ShapeshiftForm.BattleStance),
                stance(Defensive, "Defensive", (int)ShapeshiftForm.DefensiveStance), equip],
            CreateSpells = [.. baseContent.CreateSpells,
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = Battle },
                new PlayerCreateSpellRow { Race = 1, Class = 1, Spell = Defensive }],
        };

        WorldTestHost host;
        using (items.Use())
        {
            host = WorldTestHost.Start(configureServices: services => services.AddSingleton<ISpellContentStore>(new Store(content)));
        }

        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("EQUIPFORM", "Equipform"))
        {
            ulong itemGuid = await host.PlayerStateAsync("Equipform", p => p.Inventory.Equipped.Single().Item.Guid.Value);
            await client.CollectAsync();
            await client.SendAsync(WorldOpcode.CmsgCastSpell, Cast(Battle));
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.GetAuras(host.World.FindOnlinePlayer("Equipform")!).Count(h => h.Spell.Id == EquipAura && h.ItemGuid.Value == itemGuid) == 1, "battle item aura");

            await client.SendAsync(WorldOpcode.CmsgCastSpell, Cast(Defensive));
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.GetAuras(host.World.FindOnlinePlayer("Equipform")!).All(h => h.Spell.Id != EquipAura), "defensive removes item aura");

            await client.SendAsync(WorldOpcode.CmsgCastSpell, Cast(Battle));
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.GetAuras(host.World.FindOnlinePlayer("Equipform")!).Count(h => h.Spell.Id == EquipAura && h.ItemGuid.Value == itemGuid) == 1, "battle restores one owner");
        }
    }

    private static byte[] Cast(uint spell)
    {
        var writer = new PacketWriter(6);
        writer.WriteUInt32(spell);
        SpellCastTargets.ForSelf().Write(writer);
        return writer.ToArray();
    }

    private sealed class Store(SpellContent content) : ISpellContentStore
    {
        public Task<SpellContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
        public Task ReplaceDbcTablesAsync(SpellDbcContent dbc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
