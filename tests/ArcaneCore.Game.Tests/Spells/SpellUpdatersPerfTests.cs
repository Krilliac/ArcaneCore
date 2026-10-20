using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Crafting.Enchanting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Interrupts;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Hot-path behavior and allocation checks for <c>LiquidAuraInterruptUpdater</c>,
/// <c>ReactiveUpdater</c> and <c>EnchantUpdater</c>.
/// </summary>
public sealed class SpellUpdatersPerfTests
{
    private sealed class CountingProbe : ILiquidProbe
    {
        public bool High { get; set; }

        public int Calls { get; private set; }

        public bool IsHighLiquid(Map map, Player player)
        {
            Calls++;
            return High;
        }
    }

    [Fact]
    public void LiquidAuraInterruptUpdater_StationaryPlayers_DoNotReprobe_AndTransitionEdgesFire()
    {
        SpellInfo underWaterAura = SpellTestKit.Spell(9101, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            AuraInterruptFlags = (SpellAuraInterruptFlags)AuraInterruptMask.UnderWater,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = 1,
        };
        using var kit = new SpellTestKit(underWaterAura);
        var probe = new CountingProbe { High = false };
        (Player player, _) = kit.AddPlayer(1, 10, 10);
        var updater = new LiquidAuraInterruptUpdater(() => kit.System, probe);
        Map map = kit.World.GetMap(0);
        map.AddUpdater(updater);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, 9101, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(player, 9101));

        updater.Update(map, 50);
        Assert.Equal(1, probe.Calls);
        Assert.True(kit.System.HasAura(player, 9101));

        // Stationary tick does not call the probe again.
        updater.Update(map, 50);
        Assert.Equal(1, probe.Calls);

        // Moving into high liquid triggers the UnderWater interrupt edge.
        probe.High = true;
        player.SetPosition(11, 10, 10, 0);
        updater.Update(map, 50);
        Assert.Equal(2, probe.Calls);
        Assert.False(kit.System.HasAura(player, 9101));
    }

    [Fact]
    public void ReactiveUpdater_TicksWindows_AndUpdatesLowHealthAuraState()
    {
        using var kit = new SpellTestKit();
        var states = new AuraStateService(kit.System, _ => []);
        var combos = new ComboPointService(kit.System, (_, _) => null);
        var reactives = new ReactiveService(states, () => combos);
        var updater = new ReactiveUpdater(reactives);
        Map map = kit.World.GetMap(0);
        map.AddUpdater(updater);

        (Player warrior, _) = kit.AddPlayer(1, 10, 10);
        (Player target, _) = kit.AddPlayer(2, 12, 10);

        reactives.OnAttackAvoided(warrior, target, AttackAvoidance.Block);
        Assert.True(reactives.IsReactiveOpen(target, ReactiveType.Defense));
        Assert.True(states.HasAuraState(target, AuraState.Defense));

        target.SetUInt32(UpdateFields.UnitFieldMaxhealth, 100);
        target.SetUInt32(UpdateFields.UnitFieldHealth, 15);

        updater.Update(map, 2000);
        Assert.True(reactives.IsReactiveOpen(target, ReactiveType.Defense));
        Assert.True(states.HasAuraState(target, AuraState.Healthless20Percent));

        updater.Update(map, 2000);
        Assert.False(reactives.IsReactiveOpen(target, ReactiveType.Defense));
        Assert.False(states.HasAuraState(target, AuraState.Defense));
    }

    [Fact]
    public void EnchantUpdater_ExpiresTemporaryEnchantment_AndPreservesTrackedBagTimer()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1, 10, 10);
        var catalog = new EnchantCatalog([
            new SpellItemEnchantment(
                Id: 100,
                Types: [(uint)EnchantEffectType.Damage, 0, 0],
                Amounts: [5, 0, 0],
                Args: [0, 0, 0],
                Name: "+5 Damage",
                VisualId: 0,
                Flags: 0),
        ]);
        var enchantments = new PlayerEnchantments(player, catalog, kit.System);
        player.AttachEnchantments(enchantments);
        var weaponTemplate = new ItemTemplate { Entry = 500, Class = (byte)ItemClass.Weapon, InventoryType = (byte)InventoryType.Weapon, Delay = 2000 };
        var weapon = new Item(1, weaponTemplate, player.Guid) { Slot = InventorySlots.MainHand, Inventory = player.Inventory };
        ItemEnchantments.Set(weapon, EnchantSlots.Temporary, 100, duration: 1500, charges: 0);

        enchantments.Apply(weapon, EnchantSlots.Temporary, apply: true);
        Assert.Equal(1, enchantments.TrackedCount);

        var updater = new EnchantUpdater();
        Map map = kit.World.GetMap(0);
        updater.Update(map, 1000);
        Assert.Equal(500u, enchantments.LeftMs(weapon, EnchantSlots.Temporary));

        updater.Update(map, 600);
        Assert.Equal(0, enchantments.TrackedCount);
        Assert.Equal(0u, ItemEnchantments.Id(weapon, EnchantSlots.Temporary));
    }
}
