using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using static ArcaneCore.World.Tests.Spells.SpellTestServices;

namespace ArcaneCore.World.Tests.Items;

public sealed class FoodDrinkWorldTests
{
    [Fact]
    public async Task UseFoodAndDrinkWhileSitting_RegeneratesAndStandingCancelsTheAura()
    {
        const uint itemEntry = 99041;
        const uint foodSpell = 99042;
        var content = new ItemTestContent();
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = itemEntry, Class = (uint)ItemClass.Consumable, Stackable = 1,
            Spells = [new ItemSpell(foodSpell, 0, 1, 0, 0, 0, 0)],
        });
        content.Templates.StartingItems.Add(new StartingItem(1, 1, itemEntry, 1));
        WorldTestHost host;
        using (content.Use()) host = WorldTestHost.Start();
        await using (host)
        await using (WorldTestClient client = await host.EnterWorldAsync("FOOD", "FoodUser"))
        {
            await host.OnWorldAsync(() =>
            {
                SpellFeature spells = host.WorldServices.GetRequiredService<SpellFeature>();
                spells.System.Store = new SpellStore([.. spells.System.Store.All, new SpellInfo
                {
                    Id = foodSpell, Name = "Synthetic Food", Duration = new SpellDuration(10_000, 0, 10_000),
                    Attributes = SpellAttributes.AllowWhileSitting,
                    Effects = [new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 49, BaseDice = 1, DieSides = 1, Amplitude = 1000,
                        AuraType = AuraType.ModRegen, TargetA = SpellImplicitTarget.UnitCaster },
                        new SpellEffectInfo { Effect = SpellEffectName.ApplyAura, BasePoints = 49, BaseDice = 1, DieSides = 1,
                            AuraType = AuraType.ModPowerRegen, MiscValue = (int)PowerType.Mana, TargetA = SpellImplicitTarget.UnitCaster }],
                    AuraInterruptFlags = SpellAuraInterruptFlags.StandingCancels,
                }], [], []);
                Player player = host.World.FindOnlinePlayer("FoodUser")!;
                player.MaxHealth = 500;
                player.Health = 100;
                player.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 0);
                player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 500);
                player.SetUInt32(UpdateFields.UnitFieldPower1, 0);
                player.Combat.NoteManaUsed();
                player.SetStandState(StandState.Sit);
            });
            await client.CollectAsync();
            await client.SendAsync(WorldOpcode.CmsgUseItem, [InventorySlots.Bag0, InventorySlots.ItemStart, 0, 0, 0]);
            await client.ReadUntilAsync(WorldOpcode.SmsgSpellGo);
            Assert.True(await host.PlayerStateAsync("FoodUser", player =>
                host.WorldServices.GetRequiredService<SpellFeature>().System.HasAura(player, foodSpell)));
            await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("FoodUser")!.Health >= 200
                && host.World.FindOnlinePlayer("FoodUser")!.GetUInt32(UpdateFields.UnitFieldPower1) >= 20, "food and drink regeneration");
            await client.SendAsync(WorldOpcode.CmsgStandstatechange, BitConverter.GetBytes(0u));
            await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System.GetAuras(host.World.FindOnlinePlayer("FoodUser")!).All(h => h.Spell.Id != foodSpell), "standing food interrupt");
        }
    }
}
