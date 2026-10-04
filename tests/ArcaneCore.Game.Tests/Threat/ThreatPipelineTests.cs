using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Combat.Threat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>
/// The threat formula and what a landed spell does to threat and combat (vmangos ThreatCalcHelper::CalcThreat ThreatManager.cpp:35-52,
/// Unit::DealDamage Unit.cpp:866-870, Spell::DoAllEffectOnTarget Spell.cpp:1362-1366 and :1649-1725, Spell::HandleThreatSpells :5172-5230,
/// HostileRefManager::threatAssist HostileRefManager.cpp:62-76) with the production damage sink, threat binding and a real spell system.
/// </summary>
public sealed class ThreatPipelineTests
{
    private const uint ModThreatPhysical30 = 930001;
    private const uint ModThreatPhysical50 = 930002;
    private const uint ModThreatHoly = 930003;
    private const uint FireBolt = 930004;
    private const uint ModCritThreatFire = 930005;
    private const uint NoHarmfulBolt = 930006;
    private const uint NoThreatBolt = 930007;
    private const uint FlatDebuff = 930008;
    private const uint FlatBuff = 930009;
    private const uint HealSpell = 930010;
    private const uint Debuff = 930011;
    private const uint Buff = 930012;
    private const uint NoThreatDebuff = 930013;
    private const uint NoHelpfulHeal = 930014;
    private const uint ModThreatAllSchools = 930015;

    private static readonly SpellDuration Forever = new(-1, 0, -1);

    private static SpellInfo Debuffing(uint id, SpellAttributesEx extra = 0) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(10000, 0, 10000),
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
        SpellVisual = 1,
        AttributesEx = extra,
    };

    private static SpellInfo Buffing(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with
    {
        Duration = Forever,
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
    };

    private static ThreatArena Arena() => new(
        Spell(ModThreatPhysical30, Effect(SpellEffectName.ApplyAura, 30, aura: AuraType.ModThreat, misc: 1 << (int)SpellSchool.Normal)) with { Duration = Forever },
        Spell(ModThreatPhysical50, Effect(SpellEffectName.ApplyAura, 50, aura: AuraType.ModThreat, misc: 1 << (int)SpellSchool.Normal)) with { Duration = Forever },
        Spell(ModThreatHoly, Effect(SpellEffectName.ApplyAura, -50, aura: AuraType.ModThreat, misc: 1 << (int)SpellSchool.Holy)) with { Duration = Forever },
        Spell(ModThreatAllSchools, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModThreat, misc: 0x7F)) with { Duration = Forever },
        Spell(FireBolt, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Fire },
        Spell(ModCritThreatFire, Effect(SpellEffectName.ApplyAura, 100, aura: AuraType.ModCriticalThreat, misc: 1 << (int)SpellSchool.Fire)) with { Duration = Forever },
        Spell(NoHarmfulBolt, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Fire, AttributesEx4 = 0x10 },
        Spell(NoThreatBolt, Effect(SpellEffectName.SchoolDamage, 100, SpellImplicitTarget.UnitEnemy)) with { School = SpellSchool.Fire, AttributesEx = (SpellAttributesEx)0x400 },
        Debuffing(FlatDebuff),
        Debuffing(Debuff),
        Debuffing(NoThreatDebuff, (SpellAttributesEx)0x400),
        Buffing(FlatBuff),
        Buffing(Buff),
        Spell(HealSpell, Effect(SpellEffectName.Heal, 400, SpellImplicitTarget.UnitFriend)),
        Spell(NoHelpfulHeal, Effect(SpellEffectName.Heal, 400, SpellImplicitTarget.UnitFriend)) with { AttributesEx4 = 0x08 });

    // --- damage threat ----------------------------------------------------------------------

    [Fact]
    public void MeleeThreat_IsDamageTimesTheCastersPhysicalModThreat_ForPlayers()
    {
        using ThreatArena a = Arena();
        a.Cast(a.Tank, a.Tank, ModThreatPhysical30);

        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100);

        Assert.Equal(130f, a.Wolf.Combat.Threat.GetThreat(a.Tank));
    }

    [Fact]
    public void OverlappingModThreatAurasMultiply_AndRemovalRestores()
    {
        using ThreatArena a = Arena();
        a.Cast(a.Tank, a.Tank, ModThreatPhysical30);
        a.Cast(a.Tank, a.Tank, ModThreatPhysical50);

        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100);
        Assert.Equal(195f, a.Wolf.Combat.Threat.GetThreat(a.Tank), 0.01f); // 100 x 1.3 x 1.5

        a.Kit.System.RemoveAuras(a.Tank, ModThreatPhysical50);
        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100);
        Assert.Equal(325f, a.Wolf.Combat.Threat.GetThreat(a.Tank), 0.01f); // + 130
    }

    [Fact]
    public void ModThreatOnlyCountsForTheSchoolOfTheAttack_AndOnlyForPlayers()
    {
        using ThreatArena a = Arena();
        a.Cast(a.Tank, a.Tank, ModThreatHoly);
        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100); // physical: the holy aura does not apply
        Assert.Equal(100f, a.Wolf.Combat.Threat.GetThreat(a.Tank));

        // a creature carrying the same aura has no threat modifier (Aura::HandleModThreat writes it for players only, SpellAuras.cpp:3914)
        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Wolf, a.Wolf, ModThreatAllSchools));
        var modifiers = new SpellThreatModifiers(a.Kit.System);
        Assert.Equal(1f, modifiers.TotalThreatMultiplier(a.Wolf, (int)SpellSchool.Normal));
        Assert.Equal(1f, modifiers.TotalThreatMultiplier(a.Tank, (int)SpellSchool.Normal));
        a.Cast(a.Tank, a.Tank, ModThreatAllSchools);
        Assert.Equal(2f, modifiers.TotalThreatMultiplier(a.Tank, (int)SpellSchool.Normal));
    }

    [Fact]
    public void SpellDamageThreat_UsesTheSpellThreatMultiplier_AndAZeroMultiplierStillMakesTheEntry()
    {
        using ThreatArena a = Arena();
        a.Catalog.Set(new SpellThreatEntry(FireBolt, 0, 2f));

        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100, direct: false, meleeDamage: false, threatSpell: a.Info(FireBolt));
        Assert.Equal(200f, a.Wolf.Combat.Threat.GetThreat(a.Tank));

        a.Catalog.Set(new SpellThreatEntry(FireBolt, 0, 0f));
        a.Map.Combat.DealDamage(a.Dps, a.Wolf, 100, direct: false, meleeDamage: false, threatSpell: a.Info(FireBolt));
        Assert.True(a.Wolf.Combat.Threat.Contains(a.Dps));
        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Dps));
    }

    [Fact]
    public void ACriticalHit_MultipliesSpellThreatByTheCriticalThreatAuras_OnlyOnACrit()
    {
        using ThreatArena a = Arena();
        a.Cast(a.Tank, a.Tank, ModCritThreatFire);

        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100, direct: false, meleeDamage: false, threatSpell: a.Info(FireBolt), critical: false);
        Assert.Equal(100f, a.Wolf.Combat.Threat.GetThreat(a.Tank));

        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100, direct: false, meleeDamage: false, threatSpell: a.Info(FireBolt), critical: true);
        Assert.Equal(300f, a.Wolf.Combat.Threat.GetThreat(a.Tank)); // + 100 x 2
    }

    [Fact]
    public void NoHarmfulThreatSpells_AddNothing_AndNoThreatSpellsOnlyRaiseAnExistingEntry()
    {
        using ThreatArena a = Arena();

        a.Map.Combat.DealDamage(a.Tank, a.Wolf, 100, direct: false, meleeDamage: false, threatSpell: a.Info(NoHarmfulBolt));
        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Tank)); // the creature AI still adds the attacker with zero threat (AttackedBy)

        a.Map.Combat.DealDamage(a.Dps, a.Wolf, 100, direct: false, meleeDamage: false, threatSpell: a.Info(NoThreatBolt));
        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Dps)); // no entry existed, so the spell added none

        a.Wolf.Combat.Threat.AddThreat(a.Dps, 10);
        a.Map.Combat.DealDamage(a.Dps, a.Wolf, 100, direct: false, meleeDamage: false, threatSpell: a.Info(NoThreatBolt));
        Assert.Equal(110f, a.Wolf.Combat.Threat.GetThreat(a.Dps));
    }

    // --- spell_threat flat threat ---------------------------------------------------------------

    [Fact]
    public void FlatSpellThreat_IsAddedOncePerHitTarget_OnTopOfTheZeroThreatEntry()
    {
        using ThreatArena a = Arena();
        a.Catalog.Set(new SpellThreatEntry(FlatDebuff, 100));

        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Tank, a.Wolf, FlatDebuff));

        Assert.Equal(100f, a.Wolf.Combat.Threat.GetThreat(a.Tank));
    }

    [Fact]
    public void FlatSpellThreat_GoesThroughTheCastersThreatModifiers()
    {
        using ThreatArena a = Arena();
        a.Catalog.Set(new SpellThreatEntry(FlatDebuff, 100));
        a.Cast(a.Tank, a.Tank, ModThreatAllSchools);

        a.Cast(a.Tank, a.Wolf, FlatDebuff);

        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Tank) - 200f); // +100 percent on every school
    }

    [Fact]
    public void FlatSpellThreat_IsSkipped_WhenTheOnlyEffectIsInverted()
    {
        using ThreatArena a = Arena();
        a.Catalog.Set(new SpellThreatEntry(FlatDebuff, 100, InverseEffectMask: 0x01));

        a.Cast(a.Tank, a.Wolf, FlatDebuff);

        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Tank));
    }

    [Fact]
    public void APositiveSpellsFlatThreat_IsSplitOverEveryoneWhoFightsTheTarget()
    {
        using ThreatArena a = Arena();
        Creature second = a.SpawnCreature(78, 5);
        Creature third = a.SpawnCreature(79, 6);
        Creature[] mobs = [a.Wolf, second, third];
        foreach (Creature mob in mobs)
        {
            mob.Combat.Threat.AddThreat(a.Tank, 10);
        }

        a.Catalog.Set(new SpellThreatEntry(FlatBuff, 90));
        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Healer, a.Tank, FlatBuff));

        foreach (Creature mob in mobs)
        {
            Assert.Equal(30f, mob.Combat.Threat.GetThreat(a.Healer));
        }
    }

    // --- healing threat ---------------------------------------------------------------------------

    [Fact]
    public void Healing_GivesHalfTheEffectiveHealAsThreat_ApaladinsDirectHealAQuarter_AndOverhealNothing()
    {
        using ThreatArena a = Arena();
        a.Wolf.Combat.Threat.AddThreat(a.Tank, 100);
        a.Tank.Health = a.Tank.MaxHealth - 400;

        a.Cast(a.Healer, a.Tank, HealSpell);
        Assert.Equal(200f, a.Wolf.Combat.Threat.GetThreat(a.Healer));
        Assert.True(a.Healer.Combat.IsInCombat);

        // a paladin: a quarter for a direct heal
        a.Healer.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Paladin);
        a.Tank.Health = a.Tank.MaxHealth - 400;
        a.Cast(a.Healer, a.Tank, HealSpell);
        Assert.Equal(300f, a.Wolf.Combat.Threat.GetThreat(a.Healer)); // 200 + 100

        // overheal: only the missing health counts, and a full target gives nothing
        a.Tank.Health = a.Tank.MaxHealth - 100;
        a.Cast(a.Healer, a.Tank, HealSpell);
        Assert.Equal(325f, a.Wolf.Combat.Threat.GetThreat(a.Healer)); // + 25
        a.Cast(a.Healer, a.Tank, HealSpell);
        Assert.Equal(325f, a.Wolf.Combat.Threat.GetThreat(a.Healer));
    }

    [Fact]
    public void AHealOverTime_IsHalfForEveryClass()
    {
        using ThreatArena a = Arena();
        a.Wolf.Combat.Threat.AddThreat(a.Tank, 100);
        a.Healer.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Paladin);
        a.Tank.Health = a.Tank.MaxHealth - 400;

        a.Kit.System.Damage.Heal(a.Healer, a.Tank, a.Info(HealSpell), 400, periodic: true);

        Assert.Equal(200f, a.Wolf.Combat.Threat.GetThreat(a.Healer));
    }

    [Fact]
    public void HealingThreat_IsDividedByEveryListHoldingTheTarget_TimesTheSpellThreatMultiplier()
    {
        using ThreatArena a = Arena();
        Creature second = a.SpawnCreature(78, 5);
        a.Wolf.Combat.Threat.AddThreat(a.Tank, 100);
        second.Combat.Threat.AddThreat(a.Tank, 100);
        a.Catalog.Set(new SpellThreatEntry(HealSpell, 0, 2f));
        a.Tank.Health = a.Tank.MaxHealth - 400;

        a.Cast(a.Healer, a.Tank, HealSpell);

        Assert.Equal(200f, a.Wolf.Combat.Threat.GetThreat(a.Healer)); // 400 x 0.5 x 2 / 2 lists
        Assert.Equal(200f, second.Combat.Threat.GetThreat(a.Healer));
    }

    [Fact]
    public void HealingThreat_IsZeroForAConfusedCreature_ButItsEntryExists_AndNoHelpfulThreatSpellsAddNone()
    {
        using ThreatArena a = Arena();
        a.Wolf.Combat.Threat.AddThreat(a.Tank, 100);
        a.Wolf.UnitFlags |= UnitFlags.Confused;
        a.Tank.Health = a.Tank.MaxHealth - 400;

        a.Cast(a.Healer, a.Tank, HealSpell);
        Assert.True(a.Wolf.Combat.Threat.Contains(a.Healer));
        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Healer));

        a.Wolf.UnitFlags &= ~UnitFlags.Confused;
        a.Tank.Health = a.Tank.MaxHealth - 400;
        a.Cast(a.Dps, a.Tank, NoHelpfulHeal);
        Assert.False(a.Wolf.Combat.Threat.Contains(a.Dps));
    }

    // --- combat from harmless hits -------------------------------------------------------------------

    [Fact]
    public void ADebuffThatDealsNoDamage_StillPutsBothSidesInCombat_WithAZeroThreatEntry()
    {
        using ThreatArena a = Arena();
        Assert.False(a.Wolf.Combat.IsInCombat);

        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Tank, a.Wolf, Debuff, triggered: false));

        Assert.True(a.Wolf.Combat.Threat.Contains(a.Tank));
        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Tank));
        Assert.True(a.Wolf.Combat.IsInCombat);
        Assert.True(a.Tank.Combat.IsInCombat);
    }

    [Fact]
    public void ATriggeredCastAndANoThreatSpell_StartNoCombat()
    {
        using ThreatArena a = Arena();

        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Tank, a.Wolf, Debuff, triggered: true));
        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Dps, a.Wolf, NoThreatDebuff, triggered: false));

        Assert.False(a.Wolf.Combat.Threat.Contains(a.Tank));
        Assert.False(a.Wolf.Combat.Threat.Contains(a.Dps));
        Assert.False(a.Wolf.Combat.IsInCombat);
    }

    [Fact]
    public void ABuffOnAnAllyInCombat_GivesTheCasterAZeroThreatEntryOnEveryEnemy_AndPutsItInCombat()
    {
        using ThreatArena a = Arena();
        a.Wolf.Combat.Threat.AddThreat(a.Tank, 100);
        a.Map.Combat.SetInCombatState(a.Tank, 0);

        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Healer, a.Tank, Buff, triggered: false));

        Assert.True(a.Wolf.Combat.Threat.Contains(a.Healer));
        Assert.Equal(0f, a.Wolf.Combat.Threat.GetThreat(a.Healer));
        Assert.True(a.Healer.Combat.IsInCombat);
    }

    [Fact]
    public void ABuffOnAnAllyOutOfCombat_StartsNothing()
    {
        using ThreatArena a = Arena();

        Assert.Equal(SpellCastResult.CastOk, a.Cast(a.Healer, a.Tank, Buff, triggered: false));

        Assert.False(a.Healer.Combat.IsInCombat);
        Assert.True(a.Wolf.Combat.Threat.IsEmpty);
    }
}
