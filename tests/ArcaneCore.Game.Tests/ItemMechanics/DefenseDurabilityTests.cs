using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.ItemMechanics;

/// <summary>
/// Durability loss when a player defends: <c>Items:DurabilityLossChanceParry</c> wears the parrying weapon (main hand),
/// <c>Items:DurabilityLossChanceBlock</c> the blocking shield (off hand), <c>Items:DurabilityLossChanceAbsorb</c> a worn armor piece when an absorb
/// aura takes part of a swing. The three chances are the mangos <c>DurabilityLossChance.Parry/Block/Absorb</c> settings (mangos-classic
/// World.cpp:460-462, mangoszero WorldConfig.cpp:233-235; mangos defaults 0.05, 0.05, 0.5 percent). vmangos, the fidelity reference, has none of
/// them, so they default to 0 (off); the slots they wear are the documented reconstruction (docs/areas/items.md, "Combat durability").
/// </summary>
public sealed class DefenseDurabilityTests
{
    private const uint Durable = 1000;
    private const uint SwordEntry = 93_400;
    private const uint ShieldEntry = 93_401;
    private const uint ChestEntry = 93_402;
    private const uint AbsorbAura = 93_410;

    private static readonly ItemTemplateStore Store = new(
        [
            .. Templates,
            new ItemTemplate { Entry = SwordEntry, Class = 2, SubClass = 7, Name = "Parrying Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, MaxDurability = Durable, Damages = [new ItemDamage(1, 3, 0)] },
            new ItemTemplate { Entry = ShieldEntry, Class = 4, SubClass = 6, Name = "Blocking Shield", DisplayId = 1, InventoryType = 14, Armor = 5, Block = 1, MaxDurability = Durable },
            new ItemTemplate { Entry = ChestEntry, Class = 4, SubClass = 1, Name = "Absorbing Chest", DisplayId = 1, InventoryType = 5, Armor = 5, MaxDurability = Durable },
        ], []);

    private sealed class DefenseStats(bool parry, bool block) : ICombatStatSource
    {
        public bool? HasOffhandWeapon(Unit unit) => null;
        public bool? PlayerCanParry(Player player) => parry;
        public bool? PlayerCanBlock(Player player) => block;
        public uint? ShieldBlockValue(Unit unit) => 10;
    }

    private sealed record Scene(SpellTestKit Kit, MapCombat Combat, Player Attacker, Player Victim, ScriptedRandom Random) : IDisposable
    {
        public void Dispose() => Kit.Dispose();

        public uint Lost(byte slot) => Durable - Victim.Inventory.GetItem(InventorySlots.Bag0, slot)!.Durability;
    }

    private static Scene Create(bool parry, bool block, double parryChance, double blockChance, double absorbChance, int attackerDamage = 50)
    {
        var kit = new SpellTestKit(RuleTestSupport.Grant(AbsorbAura, AuraType.SchoolAbsorb, 1000, (int)SpellSchoolMasks.Of(SpellSchool.Normal)));
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        MapCombat combat = attacker.Map!.Combat;
        combat.SpellMitigation = kit.System;
        combat.Stats = new DefenseStats(parry, block);
        attacker.SetFloat(UpdateFields.UnitFieldMindamage, attackerDamage);
        attacker.SetFloat(UpdateFields.UnitFieldMaxdamage, attackerDamage);
        victim.Health = victim.MaxHealth = 100_000;
        victim.SetFloat(UpdateFields.PlayerParryPercentage, parry ? 100f : 0f);
        victim.SetFloat(UpdateFields.PlayerBlockPercentage, block ? 100f : 0f);
        victim.SetFloat(UpdateFields.PlayerDodgePercentage, 0f);
        victim.SetByte(UpdateFields.UnitFieldBytes2, 0, 1); // weapons drawn
        victim.Relocate(2, 0, victim.Z, MathF.PI, 0);      // faces the attacker
        Wire(victim.Inventory);
        victim.Inventory.Templates = Store;
        victim.Inventory.Load(
        [
            Row(InventorySlots.MainHand, SwordEntry), Row(InventorySlots.OffHand, ShieldEntry), Row(InventorySlots.Chest, ChestEntry),
        ]);
        ItemMechanicsOptions options = victim.Inventory.Options;
        options.DurabilityLossChanceDamage = 0;   // only the defense rolls under test
        options.DurabilityLossChanceParry = parryChance;
        options.DurabilityLossChanceBlock = blockChance;
        options.DurabilityLossChanceAbsorb = absorbChance;
        var random = new ScriptedRandom();         // the percent roll is frand(0, 100) = 0, so any positive chance passes
        combat.Random = random;
        return new Scene(kit, combat, attacker, victim, random);
    }

    private static InventoryItemData Row(byte slot, uint entry)
        => new(0, slot, new ItemInstanceData { Guid = 7000u + slot, Entry = entry, Durability = Durable, Charges = [0, 0, 0, 0, 0], Enchantments = new uint[21] });

    private static MeleeDamageInfo Swing(Scene s, int roll)
    {
        s.Random.Ints.Enqueue(roll);
        return s.Combat.AttackerStateUpdate(s.Attacker, s.Victim, WeaponAttackType.BaseAttack)!;
    }

    [Fact]
    public void Parry_WearsTheParryingWeapon()
    {
        using Scene s = Create(parry: true, block: false, parryChance: 100, blockChance: 100, absorbChance: 100);
        MeleeDamageInfo info = Swing(s, 9000);
        Assert.Equal(MeleeHitOutcome.Parry, info.Outcome);
        Assert.Equal(1u, s.Lost(InventorySlots.MainHand));
        Assert.Equal(0u, s.Lost(InventorySlots.OffHand));
        Assert.Equal(0u, s.Lost(InventorySlots.Chest));
    }

    [Fact]
    public void Block_WearsTheShield()
    {
        using Scene s = Create(parry: false, block: true, parryChance: 100, blockChance: 100, absorbChance: 100);
        MeleeDamageInfo info = Swing(s, 9000);
        Assert.Equal(MeleeHitOutcome.Block, info.Outcome);
        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
        Assert.Equal(1u, s.Lost(InventorySlots.OffHand));
    }

    [Fact]
    public void Absorb_WearsAWornArmorPiece()
    {
        using Scene s = Create(parry: false, block: false, parryChance: 100, blockChance: 100, absorbChance: 100);
        RuleTestSupport.Apply(s.Kit, s.Victim, AbsorbAura);
        MeleeDamageInfo info = Swing(s, 9999);
        Assert.Equal(MeleeHitOutcome.Normal, info.Outcome);
        Assert.True(info.Absorbed > 0);
        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
        // The pool is the worn armor with durability (shield and chest, slot order); the scripted pick takes the last one.
        Assert.Equal(1u, s.Lost(InventorySlots.OffHand) + s.Lost(InventorySlots.Chest));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ZeroChance_WearsNothing_AndDoesNotRoll(bool parry, bool block)
    {
        using Scene s = Create(parry, block, parryChance: 0, blockChance: 0, absorbChance: 0);
        s.Random.Floats.Enqueue(-1f); // would pass any positive chance; it must stay queued
        Swing(s, 9000);
        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
        Assert.Equal(0u, s.Lost(InventorySlots.OffHand));
        Assert.Equal(0u, s.Lost(InventorySlots.Chest));
    }

    [Fact]
    public void DurabilityLossDisabled_OverridesTheDefenseChances()
    {
        using Scene s = Create(parry: true, block: false, parryChance: 100, blockChance: 100, absorbChance: 100);
        s.Victim.Inventory.Options.DurabilityLossEnable = false;
        Swing(s, 9000);
        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
    }

    [Fact]
    public void Defaults_AreVmangos_WhichHasNoDefenseWear()
    {
        // vmangos reads only DurabilityLossChance.Damage (World.cpp:554); Parry, Block and Absorb are mangos settings it dropped. The defaults
        // are therefore 0 (off); the mangos values (0.05, 0.05, 0.5) are an operator's choice.
        var options = new ItemMechanicsOptions();
        Assert.Equal(0, options.DurabilityLossChanceParry);
        Assert.Equal(0, options.DurabilityLossChanceBlock);
        Assert.Equal(0, options.DurabilityLossChanceAbsorb);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void WithTheDefaultChances_AParryABlockOrAnAbsorb_WearsNothing(bool parry, bool block, bool absorb)
    {
        var defaults = new ItemMechanicsOptions();
        using Scene s = Create(parry, block, defaults.DurabilityLossChanceParry, defaults.DurabilityLossChanceBlock, defaults.DurabilityLossChanceAbsorb);
        if (absorb)
        {
            RuleTestSupport.Apply(s.Kit, s.Victim, AbsorbAura);
        }

        MeleeDamageInfo info = Swing(s, absorb ? 9999 : 9000);

        Assert.Equal(parry ? MeleeHitOutcome.Parry : block ? MeleeHitOutcome.Block : MeleeHitOutcome.Normal, info.Outcome);
        Assert.Equal(0u, s.Lost(InventorySlots.MainHand));
        Assert.Equal(0u, s.Lost(InventorySlots.OffHand));
        Assert.Equal(0u, s.Lost(InventorySlots.Chest));
    }
}
