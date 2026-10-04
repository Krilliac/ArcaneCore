using ArcaneCore.Game.Crafting;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Items.ItemUse;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Game.Tests.Crafting;

/// <summary>
/// Crafting lane, slice bandages-first-aid. The bandage spell is the classic-db row of spell 746 (First Aid rank 1): ApplyAura PERIODIC_HEAL
/// 10 per second for 8 s, implicit target 21 (friendly unit), spell mechanic 16, channeled, channel interrupt flags 0x3C0E. Recently Bandaged
/// is row 11196: ApplyAura MECHANIC_IMMUNITY misc 16, implicit target 21, 60 s. The casts are driven with explicit time steps.
/// </summary>
public sealed class FirstAidTests
{
    private const uint Bandage = 746;
    private const uint LinenBandageEntry = 95001;   // synthetic entry: the shared kit already holds 1251 without a spell

    private static SpellInfo BandageSpell(uint id = Bandage) =>
        SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 10, SpellImplicitTarget.UnitFriend, AuraType.PeriodicHeal, amplitude: 1000)) with
        {
            Mechanic = 16,
            AttributesEx = SpellAttributesEx.IsChanneled | SpellAttributesEx.IsSelfChanneled,
            Duration = new SpellDuration(8000, 0, 8000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            ChannelInterruptFlags = (SpellAuraInterruptFlags)0x3C0E,
            AuraInterruptFlags = SpellAuraInterruptFlags.Damage,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = 1,
        };

    private static SpellInfo RecentlyBandagedSpell() =>
        SpellTestKit.Spell(FirstAidObserver.RecentlyBandaged, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.MechanicImmunity, misc: 16)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellVisual = 1,
        };

    private sealed class Rig : IDisposable
    {
        public Rig(bool observer = true, params SpellInfo[] extra)
        {
            var bandageItem = new ItemTemplate
            {
                Entry = LinenBandageEntry, Class = 0, Name = "Linen Bandage", DisplayId = 2, Quality = 1, Stackable = 20,
                Spells = [new ItemSpell(Bandage, ItemSpellTriggers.OnUse, -1, 0, -1, 0, -1)],
            }.Normalized();
            Kit = new CraftingTestKit([BandageSpell(), RecentlyBandagedSpell(), .. extra], [bandageItem]);
            Kit.System.CombatRules = new VanillaSpellCombatRules();   // as the world daemon installs: immunity is part of the hit result
            Kit.System.ApplicationRules.Add(new ArcaneCore.Game.Spells.Rules.Immunity.ImmunityApplicationRule());
            if (observer)
            {
                FirstAidObserver.Install(Kit.System);
            }

            ItemUse = ItemUseService.Install(Kit.System);
            (Target, _) = Kit.Kit.AddPlayer(2, 2);
            Target.MaxHealth = 100;
            Target.Health = 10;
        }

        public CraftingTestKit Kit { get; }

        public ItemUseService ItemUse { get; }

        public Player Target { get; }

        public SpellSystem System => Kit.System;

        public bool HasRecentlyBandaged(Unit unit) => System.HasAura(unit, FirstAidObserver.RecentlyBandaged);

        public Item Bandage1() => Kit.Give(LinenBandageEntry, 5);

        public void Use(Item bandage, Unit target)
            => ItemUse.UseItem(Kit.Player, bandage.BagSlot, bandage.Slot, 0, SpellCastTargets.ForUnit(target.Guid));

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ABandage_HealsTenPerSecond_ForEightTicks_AndTheBandageIsConsumedAtTheStart()
    {
        using var rig = new Rig();
        Item bandages = rig.Bandage1();

        rig.Use(bandages, rig.Target);

        Assert.Equal(4u, rig.Kit.Inventory.GetItemCount(LinenBandageEntry));   // consumed when the channel starts (TakeCastItem after the effects)
        Assert.Equal(10u, rig.Target.Health);
        rig.Kit.Kit.Advance(1000);
        Assert.Equal(20u, rig.Target.Health);
        rig.Kit.Kit.Advance(7000);
        Assert.Equal(90u, rig.Target.Health);
    }

    [Fact]
    public void RecentlyBandaged_IsAppliedAtTheStartOfTheChannel_ToTheTarget()
    {
        // vmangos FirstAidScript::OnAfterHit runs per hit target before any tick (Spell.cpp:1541-1542), not when the channel ends.
        using var rig = new Rig();

        rig.Use(rig.Bandage1(), rig.Target);

        Assert.True(rig.HasRecentlyBandaged(rig.Target));
        Assert.False(rig.HasRecentlyBandaged(rig.Kit.Player));
    }

    [Fact]
    public void AnInterruptedBandage_KeepsTheImmunity_AndStopsHealing()
    {
        using var rig = new Rig();
        rig.Use(rig.Bandage1(), rig.Target);
        rig.Kit.Kit.Advance(2000);
        uint healed = rig.Target.Health;

        rig.System.CancelChannel(rig.Kit.Player);
        rig.Kit.Kit.Advance(3000);

        Assert.True(rig.HasRecentlyBandaged(rig.Target));
        Assert.Equal(healed, rig.Target.Health);
    }

    [Fact]
    public void ASecondBandage_OnARecentlyBandagedTarget_LandsImmune_AndHealsNothing()
    {
        using var rig = new Rig();
        Item bandages = rig.Bandage1();
        rig.Use(bandages, rig.Target);
        rig.System.CancelChannel(rig.Kit.Player);
        uint afterFirst = rig.Target.Health;

        rig.Use(bandages, rig.Target);
        rig.Kit.Kit.Advance(8000);

        Assert.Equal(afterFirst, rig.Target.Health);
        Assert.True(rig.HasRecentlyBandaged(rig.Target));
    }

    [Fact]
    public void ABandageOnYourself_AlsoAppliesRecentlyBandaged_ToYourself()
    {
        using var rig = new Rig();
        rig.Kit.Player.MaxHealth = 100;
        rig.Kit.Player.Health = 10;
        Item bandages = rig.Bandage1();

        rig.ItemUse.UseItem(rig.Kit.Player, bandages.BagSlot, bandages.Slot, 0, SpellCastTargets.ForSelf());

        Assert.True(rig.HasRecentlyBandaged(rig.Kit.Player));
    }

    [Fact]
    public void ASecondBandage_OnYourself_AlsoLandsImmune()
    {
        using var rig = new Rig();
        rig.Kit.Player.MaxHealth = 100;
        rig.Kit.Player.Health = 10;
        Item bandages = rig.Bandage1();
        rig.ItemUse.UseItem(rig.Kit.Player, bandages.BagSlot, bandages.Slot, 0, SpellCastTargets.ForSelf());
        rig.System.CancelChannel(rig.Kit.Player);
        uint afterFirst = rig.Kit.Player.Health;

        rig.ItemUse.UseItem(rig.Kit.Player, bandages.BagSlot, bandages.Slot, 0, SpellCastTargets.ForSelf());
        rig.Kit.Kit.Advance(8000);

        Assert.Equal(afterFirst, rig.Kit.Player.Health);   // a self cast skips the hit roll but not the immunity of the effect (ImmunityApplicationRule)
    }

    [Fact]
    public void WithoutTheObserver_NoImmunityIsApplied()
    {
        using var rig = new Rig(observer: false);

        rig.Use(rig.Bandage1(), rig.Target);

        Assert.False(rig.HasRecentlyBandaged(rig.Target));
    }

    [Fact]
    public void AllNineteenBandageSpellIds_AreCovered_ByTheScriptSet()
    {
        uint[] retail = [746, 1159, 3267, 3268, 7926, 7927, 10838, 10839, 18608, 18610, 20803, 23567, 23568, 23569, 23696, 24412, 24413, 24414, 30020];

        Assert.Equal(19, FirstAidObserver.BandageSpells.Count);
        foreach (uint id in retail)
        {
            Assert.True(FirstAidObserver.BandageSpells.Contains(id), $"spell {id}");
        }
    }

    [Fact]
    public void ANonBandageSpell_NeverTriggersTheImmunity()
    {
        using var rig = new Rig(true, BandageSpell(95123));   // same data, but not one of the 19 script ids

        Assert.Equal(SpellCastResult.CastOk, rig.System.CastSpell(rig.Kit.Player, 95123, SpellCastTargets.ForUnit(rig.Target.Guid), triggered: false));

        Assert.False(rig.HasRecentlyBandaged(rig.Target));
    }

    [Fact]
    public void InstallingTwice_Throws()
    {
        using var rig = new Rig();

        Assert.Throws<InvalidOperationException>(() => FirstAidObserver.Install(rig.System));
    }
}
