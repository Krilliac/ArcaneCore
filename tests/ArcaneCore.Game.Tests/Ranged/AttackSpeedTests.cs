using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Ranged;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Ranged;

/// <summary>
/// Attack speed auras 9, 138, 140 and 141 and the base/hasted attack time split (ranged lane S01), after vmangos
/// SpellAuras.cpp:5092-5166, Unit.cpp:9678-9697 (ApplyAttackTimePercentMod), Unit.h:406-413 (GetAttackTime / SetAttackTime)
/// and Unit.cpp:529-532 (ResetAttackTimer). Aura values are the real ones: Rapid Fire 3045 is +40 aura 140, Quick Shots
/// 6150 +30, Cripple style slows are negative aura 138 (classic-db spell_template). vmangos keeps the attack time as a
/// float32, so every expected number is derived here with the same float32 sequence, never typed by hand.
/// </summary>
public sealed class AttackSpeedTests
{
    private const uint RapidFire = 940001;   // aura 140, +40
    private const uint QuickShots = 940002;  // aura 140, +30
    private const uint Cripple = 940003;     // aura 138, -45
    private const uint Haste9 = 940004;      // aura 9, +20
    private const uint Quiver = 940005;      // aura 141, +10 (spell 14824 shaped)
    private const uint AimedShot = 940006;   // 3000 ms ranged ability
    private const uint Haste100 = 940007;    // aura 140, +100

    private const uint Bow = 94101;
    private const uint Wand = 94102;
    private const uint Thrown = 94103;
    private const uint Gun = 94104;
    private const uint Bullet = 94110;

    private static readonly ItemTemplate[] Templates =
    [
        new() { Entry = Bow, Class = 2, SubClass = 2, DisplayId = 300, InventoryType = 15, Delay = 2500, MaxDurability = 40, AmmoType = 2 },
        new() { Entry = Gun, Class = 2, SubClass = 3, DisplayId = 301, InventoryType = 26, Delay = 2800, MaxDurability = 40, AmmoType = 3 },
        new() { Entry = Wand, Class = 2, SubClass = 19, DisplayId = 302, InventoryType = 26, Delay = 1500, MaxDurability = 40, AmmoType = 0 },
        new() { Entry = Thrown, Class = 2, SubClass = 16, DisplayId = 303, InventoryType = 25, Delay = 2000, Stackable = 200, AmmoType = 4 },
        new() { Entry = Bullet, Class = 6, SubClass = 3, DisplayId = 5998, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(4, 5, 0)] },
    ];

    private static SpellInfo Buff(uint id, int amount, AuraType aura) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: aura)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellTestKit Kit(ISpellModifiers? modifiers = null)
    {
        var kit = new SpellTestKit(
            Buff(RapidFire, 40, AuraType.ModRangedHaste),
            Buff(QuickShots, 30, AuraType.ModRangedHaste),
            Buff(Cripple, -45, AuraType.ModMeleeHaste),
            Buff(Haste9, 20, AuraType.ModAttackspeed),
            Buff(Quiver, 10, AuraType.ModRangedAmmoHaste),
            Buff(Haste100, 100, AuraType.ModRangedHaste),
            Spell(AimedShot, Effect(SpellEffectName.WeaponDamage, 5, SpellImplicitTarget.UnitEnemy)) with
            {
                Attributes = SpellAttributes.UsesRangedSlot | SpellAttributes.IsAbility,
                DamageClass = SpellDamageClass.Ranged,
                CastTime = new SpellCastTime(3000, 0, 0),
                RangeIndex = 4,
                Range = new SpellRange(5, 35),
                StartRecoveryCategory = 0,
                StartRecoveryTime = 0,
            });
        if (modifiers is not null)
        {
            kit.System.SpellModifiers = modifiers;
        }

        return kit;
    }

    private static Player NewPlayer(SpellTestKit kit, uint guid = 1, float x = 0)
    {
        (Player player, _) = kit.AddPlayer(guid, x);
        player.Inventory.Templates = new ItemTemplateStore([.. ItemTestData.Templates, .. Templates]);
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
        player.SetUInt32(UpdateFields.UnitFieldBaseattacktime + 1, 1500);
        player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2800);
        return player;
    }

    private static void Equip(Player player, uint entry)
    {
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(entry, 1, out Item? item));
        Assert.Equal(InventoryResult.Ok, player.Inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, item!.Template, item, swap: false));
        player.Inventory.RemoveItem(InventorySlots.Bag0, item.Slot);
        player.Inventory.EquipItem(dest, item);
    }

    private static void Cast(SpellTestKit kit, Player player, uint spell)
        => kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true);

    private static uint Field(Player player, WeaponAttackType type) => player.GetUInt32(UpdateFields.UnitFieldBaseattacktime + (int)type);

    /// <summary>vmangos ApplyPercentModFloatValue / ApplyPercentModFloatVar for positive haste: value * 100 / (100 + percent), float32.</summary>
    private static float Hasten(float value, float percent) => value * (100.0f / (100.0f + percent));

    private static float Unhasten(float value, float percent) => value * ((100.0f + percent) / 100.0f);

    // --- aura 140 ----------------------------------------------------------------------------

    [Fact]
    public void RapidFire_ShortensTheRangedField_KeepsTheBaseTime_AndRestoresOnRemoval()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);

        Cast(kit, player, RapidFire);

        float hastedField = Hasten(2800f, 40f);
        float pct = Hasten(1.0f, 40f);
        Assert.Equal((uint)hastedField, Field(player, WeaponAttackType.RangedAttack));
        // Unit::GetAttackTime is the unhasted time: the float field divided by the multiplier (vmangos float32 artefact included).
        Assert.Equal((uint)(hastedField / pct), player.Combat.GetAttackTime(WeaponAttackType.RangedAttack));
        Assert.InRange(player.Combat.GetAttackTime(WeaponAttackType.RangedAttack), 2799u, 2800u);

        player.Combat.ResetAttackTimer(WeaponAttackType.RangedAttack);
        Assert.Equal((uint)((uint)(hastedField / pct) * pct), player.Combat.GetAttackTimer(WeaponAttackType.RangedAttack));
        Assert.InRange(player.Combat.GetAttackTimer(WeaponAttackType.RangedAttack), 1999u, 2000u);

        kit.System.RemoveAuras(player, RapidFire);
        Assert.InRange(Field(player, WeaponAttackType.RangedAttack), 2799u, 2800u);
        Assert.Equal(1.0f, player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack), 0.0001f);
    }

    [Fact]
    public void Aura140_MovesOnlyTheRangedSlot_Aura138OnlyMainAndOffHand_Aura9AllThree()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);

        Cast(kit, player, RapidFire);
        Assert.Equal(2000u, Field(player, WeaponAttackType.BaseAttack));
        Assert.Equal(1500u, Field(player, WeaponAttackType.OffAttack));
        Assert.True(Field(player, WeaponAttackType.RangedAttack) < 2800u);
        kit.System.RemoveAuras(player, RapidFire);

        Cast(kit, player, Cripple); // -45: the time stretches by 1.45
        Assert.Equal((uint)Unhasten(2000f, 45f), Field(player, WeaponAttackType.BaseAttack));
        Assert.Equal((uint)Unhasten(1500f, 45f), Field(player, WeaponAttackType.OffAttack));
        Assert.InRange(Field(player, WeaponAttackType.RangedAttack), 2799u, 2800u);
        kit.System.RemoveAuras(player, Cripple);

        Cast(kit, player, Haste9);
        Assert.Equal((uint)Hasten(2000f, 20f), Field(player, WeaponAttackType.BaseAttack));
        Assert.Equal((uint)Hasten(1500f, 20f), Field(player, WeaponAttackType.OffAttack));
        Assert.Equal((uint)Hasten(2800f, 20f), Field(player, WeaponAttackType.RangedAttack));
    }

    [Fact]
    public void TwoHasteAuras_Multiply_AndRemovalOrderDoesNotMatter()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);

        Cast(kit, player, RapidFire);
        Cast(kit, player, QuickShots);
        float expected = Hasten(Hasten(2800f, 40f), 30f);
        Assert.Equal((uint)expected, Field(player, WeaponAttackType.RangedAttack));
        Assert.NotEqual((uint)Hasten(2800f, 70f), Field(player, WeaponAttackType.RangedAttack)); // +40% and +30% are not +70%
        Assert.Equal(Hasten(Hasten(1f, 40f), 30f), player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack), 0.00001f);

        kit.System.RemoveAuras(player, RapidFire);
        kit.System.RemoveAuras(player, QuickShots);
        Assert.InRange(Field(player, WeaponAttackType.RangedAttack), 2798u, 2800u);
        Assert.Equal(1.0f, player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack), 0.0001f);
    }

    [Fact]
    public void Haste100_HalvesTheTime()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);

        Cast(kit, player, Haste100);

        Assert.Equal(1400u, Field(player, WeaponAttackType.RangedAttack));
        Assert.Equal(0.5f, player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack), 0.0001f);
    }

    [Fact]
    public void ARawFieldWriteWhileHasted_IsTakenAsTheHastedValue()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);
        Cast(kit, player, RapidFire);

        // Someone writes the uint field directly (creature spawn, tests, other lanes): the float shadow must follow it.
        player.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2000);
        kit.System.RemoveAuras(player, RapidFire);

        Assert.InRange(Field(player, WeaponAttackType.RangedAttack), 2799u, 2800u);
    }

    [Fact]
    public void SetAttackTime_UnderHaste_StoresTheHastedField_AndGetAttackTimeReadsTheBase()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);
        Cast(kit, player, RapidFire);

        player.Combat.SetAttackTime(WeaponAttackType.RangedAttack, 3000);

        Assert.Equal((uint)(3000u * Hasten(1f, 40f)), Field(player, WeaponAttackType.RangedAttack));
        Assert.InRange(player.Combat.GetAttackTime(WeaponAttackType.RangedAttack), 2999u, 3000u);
    }

    // --- haste does not change damage per hit ------------------------------------------------

    [Fact]
    public void NormalizedWeaponDamage_UsesTheUnhastedSpeed_SoHasteDoesNotChangeDamagePerHit()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);
        player.SetFloat(UpdateFields.UnitFieldMinrangeddamage, 100f);
        player.SetFloat(UpdateFields.UnitFieldMaxrangeddamage, 100f);
        player.SetInt32(UpdateFields.UnitFieldRangedAttackPower, 280);

        float before = kit.System.WeaponDamageRoll(player, WeaponAttackType.RangedAttack, normalized: true);
        Cast(kit, player, RapidFire);
        Assert.True(Field(player, WeaponAttackType.RangedAttack) < 2800u, "Rapid Fire must be applied for this test to mean anything");
        float during = kit.System.WeaponDamageRoll(player, WeaponAttackType.RangedAttack, normalized: true);

        Assert.Equal(before, during, 0.5f); // 2.8 s normalized against a 2.8 s weapon: no attack power term, hasted or not
    }

    // --- ranged ability cast time ------------------------------------------------------------

    [Fact]
    public void AimedShotCastTime_FollowsTheRangedHaste()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);
        (Player target, _) = kit.AddPlayer(2, 20);
        target.Health = 5000;
        Equip(player, Gun);
        Assert.Equal(InventoryResult.Ok, player.Inventory.AddItem(Bullet, 20, out _));
        Assert.True(PlayerAmmo.SetAmmo(player, Bullet));
        kit.Spellbook.Teach(player, AimedShot);
        Cast(kit, player, RapidFire);

        SpellCastResult result = kit.System.CastSpell(player, AimedShot, SpellCastTargets.ForUnit(target.Guid), triggered: false);

        Assert.Equal(SpellCastResult.CastOk, result);
        SpellCast cast = kit.System.GetState(player.Guid)!.CurrentCast!;
        // SpellEntry::GetCastTime: ranged abilities scale by m_modAttackSpeedPct[RANGED], then +500 for the ranged slot.
        Assert.Equal((int)(3000 * Hasten(1f, 40f)) + 500, cast.CastTime);
    }

    // --- aura 141 ----------------------------------------------------------------------------

    [Theory]
    [InlineData(Bow, true)]
    [InlineData(Gun, true)]
    [InlineData(Thrown, true)]  // thrown weapons carry ammo_type 4, so quivers speed them up (retail quirk)
    [InlineData(Wand, false)]   // ammo_type 0: HandleRangedAmmoHaste refuses
    [InlineData(0u, false)]     // nothing wielded
    public void QuiverAura_AppliesOnlyWithAWeaponThatTakesAmmo(uint weapon, bool applies)
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);
        if (weapon != 0)
        {
            Equip(player, weapon);
        }

        uint before = Field(player, WeaponAttackType.RangedAttack);
        Cast(kit, player, Quiver);

        if (applies)
        {
            Assert.Equal((uint)Hasten(before, 10f), Field(player, WeaponAttackType.RangedAttack));
        }
        else
        {
            Assert.Equal(before, Field(player, WeaponAttackType.RangedAttack));
        }

        // Removal takes back exactly what was applied: a refused apply must not speed anything up on removal.
        kit.System.RemoveAuras(player, Quiver);
        Assert.InRange(Field(player, WeaponAttackType.RangedAttack), before - 1, before);
        Assert.Equal(1.0f, player.Combat.GetAttackSpeedPct(WeaponAttackType.RangedAttack), 0.0001f);
    }

    [Fact]
    public void QuiverAura_IsEvaluatedOnceAtApply_AWeaponSwapDoesNotReEvaluateIt()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);
        Equip(player, Bow);
        Cast(kit, player, Quiver);
        uint hasted = Field(player, WeaponAttackType.RangedAttack);
        Assert.True(hasted < 2800u);

        // vmangos applies it once and never revisits it when the weapon changes (SpellAuras.cpp:5141-5166).
        player.Inventory.RemoveItem(InventorySlots.Bag0, InventorySlots.Ranged);
        Equip(player, Wand);
        Assert.Equal(hasted, Field(player, WeaponAttackType.RangedAttack));
        kit.System.RemoveAuras(player, Quiver);
        Assert.InRange(Field(player, WeaponAttackType.RangedAttack), 2799u, 2800u);
    }

    [Fact]
    public void QuiverAndRapidFire_StackMultiplicatively()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);
        Equip(player, Bow);

        Cast(kit, player, Quiver);
        Cast(kit, player, RapidFire);

        Assert.Equal((uint)Hasten(Hasten(2800f, 10f), 40f), Field(player, WeaponAttackType.RangedAttack));
    }

    // --- SPELLMOD_HASTE ----------------------------------------------------------------------

    private sealed class DoubleHaste : ISpellModifiers
    {
        public float Apply(Unit caster, SpellInfo spell, SpellModOp op, float value) => op == SpellModOp.Haste ? value * 2 : value;
    }

    [Fact]
    public void SpellModHaste_ModifiesTheAppliedAmount_AndRemovalReversesTheModifiedAmount()
    {
        using var kit = Kit(new DoubleHaste());
        Player player = NewPlayer(kit);

        Cast(kit, player, RapidFire);
        Assert.Equal((uint)Hasten(2800f, 80f), Field(player, WeaponAttackType.RangedAttack));

        kit.System.RemoveAuras(player, RapidFire);
        Assert.InRange(Field(player, WeaponAttackType.RangedAttack), 2799u, 2800u);
    }

    // --- no haste: bit-identical to the old behaviour ----------------------------------------

    [Fact]
    public void WithoutHaste_TheFieldBaseTimeAndTimerAreExactlyTheRawValues()
    {
        using var kit = Kit();
        Player player = NewPlayer(kit);

        foreach (WeaponAttackType type in new[] { WeaponAttackType.BaseAttack, WeaponAttackType.OffAttack, WeaponAttackType.RangedAttack })
        {
            uint raw = Field(player, type);
            Assert.Equal(raw, player.Combat.GetAttackTime(type));
            player.Combat.ResetAttackTimer(type);
            Assert.Equal(raw, player.Combat.GetAttackTimer(type));
        }
    }
}
