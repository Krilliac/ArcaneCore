using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Locomotion;

/// <summary>
/// The rest of vmangos' environmental damage path: Spell::EffectEnvironmentalDMG (SpellEffects.cpp:286-304) hurts a player through
/// Player::EnvironmentalDamage(DAMAGE_FIRE) and deals any other unit nothing, and Player::EnvironmentalDamage runs
/// SpellCaster::DealDamageMods (SpellCaster.cpp:1774-1787) before its log (Player.cpp:756).
/// </summary>
public sealed class EnvironmentalDamageParityTests
{
    private const uint NatureFlames = 962_001;
    private const uint FireFlames = 962_002;
    private const uint FireWard = 962_003;
    private const uint NatureWard = 962_004;

    private static readonly int Fire = (int)SpellSchoolMasks.Of(SpellSchool.Fire);
    private static readonly int Nature = (int)SpellSchoolMasks.Of(SpellSchool.Nature);

    private static SpellInfo Flames(uint id, SpellSchool school) =>
        Spell(id, Effect(SpellEffectName.EnvironmentalDamage, 100, SpellImplicitTarget.UnitEnemy))
        with { School = school, RangeIndex = 4, Range = new SpellRange(0, 30), StartRecoveryCategory = 0, StartRecoveryTime = 0 };

    /// <summary>The test spells, in a world whose environmental damage uses the kit's spell system.</summary>
    private static SpellTestKit Kit()
    {
        var kit = new SpellTestKit(
            Flames(NatureFlames, SpellSchool.Nature),
            Flames(FireFlames, SpellSchool.Fire),
            RuleTestKit.Grant(FireWard, AuraType.SchoolAbsorb, 30, Fire),
            RuleTestKit.Grant(NatureWard, AuraType.SchoolAbsorb, 30, Nature),
            RuleTestKit.Grant(SpellSystem.SpiritOfRedemptionSpell, AuraType.Dummy, 0));
        LocomotionEnvironment.RegisterMitigation(kit.World, new SpellEnvironmentalMitigation(kit.System));
        return kit;
    }

    /// <summary>A player with 1000 health.</summary>
    private static (Player Player, FakeSession Session) AddPlayer(SpellTestKit kit, uint guid, float x = 0)
    {
        (Player player, FakeSession session) = kit.AddPlayer(guid, x);
        player.MaxHealth = 1000;
        player.Health = 1000;
        return (player, session);
    }

    private static List<byte[]> Logs(FakeSession session) => Packets(session, WorldOpcode.SmsgEnvironmentaldamagelog);

    // --- Spell::EffectEnvironmentalDMG ------------------------------------------------------

    [Fact]
    public void APlayer_TakesTheSpellAsFireFromTheWorld_WhateverItsSchool()
    {
        using SpellTestKit kit = Kit();
        (Player caster, FakeSession casterSession) = AddPlayer(kit, 1);
        (Player target, FakeSession targetSession) = AddPlayer(kit, 2, 2);
        RuleTestKit.Apply(kit, target, FireWard);
        RuleTestKit.Apply(kit, target, NatureWard);
        casterSession.Clear();
        targetSession.Clear();

        kit.System.CastSpell(caster, NatureFlames, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        // EnvironmentalDamage(DAMAGE_FIRE): the fire ward absorbs 30 of the nature spell and the nature ward nothing (Player.cpp:723-728).
        Assert.Equal(EnvironmentalDamage.BuildLog(target.Guid.Value, EnvironmentalDamageType.Fire, 70, 30, 0), Assert.Single(Logs(targetSession)));
        Assert.Empty(Packets(targetSession, WorldOpcode.SmsgSpellnonmeleedamagelog));
        Assert.Empty(Packets(casterSession, WorldOpcode.SmsgSpellnonmeleedamagelog));
        Assert.Equal(930u, target.Health);
        Assert.False(kit.System.HasAura(target, FireWard));
        Assert.True(kit.System.HasAura(target, NatureWard));
    }

    [Fact]
    public void ALethalHit_IsAnEnvironmentalDeath_ThatWearsTheEquipment_EvenFromAPlayer()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = AddPlayer(kit, 1);
        (Player target, FakeSession targetSession) = AddPlayer(kit, 2, 2);
        Wire(target.Inventory);
        target.Inventory.Load([new InventoryItemData(0, InventorySlots.MainHand, new ItemInstanceData { Guid = 4000, Entry = WornShortsword, Durability = 20 })]);
        target.Health = 50;
        targetSession.Clear();

        kit.System.CastSpell(caster, FireFlames, SpellCastTargets.ForUnit(target.Guid), triggered: true);

        // Player::EnvironmentalDamage charges the 10% itself, whoever cast (Player.cpp:765-771); a kill by a player would spare it (Unit.cpp:1191-1202).
        Assert.False(target.IsAlive);
        Assert.Equal(EnvironmentalDamageType.Fire, target.LastEnvironmentalDeath);
        Assert.Equal(18u, target.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability);
        Assert.Single(Packets(targetSession, WorldOpcode.SmsgDurabilityDamageDeath));
    }

    [Fact]
    public void AnyOtherUnit_TakesNoDamage_ButItsAbsorbsAreSpent_AndTheHitIsLogged()
    {
        using SpellTestKit kit = Kit();
        kit.System.CombatRules = new FixedRules { ResistFraction = 0.25f };
        kit.System.Units = new MapObjectResolver();
        (Player caster, FakeSession casterSession) = AddPlayer(kit, 1);
        Map map = kit.World.GetMap(0);
        var creature = new CombatTestUnit(health: 1000);
        creature.Relocate(3, 0, caster.Z, 0, 0);
        map.AddObject(creature);
        map.Combat.Track(creature);
        RuleTestKit.Apply(kit, creature, FireWard);
        casterSession.Clear();

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, FireFlames, SpellCastTargets.ForUnit(creature.Guid), triggered: true));

        // CalculateDamageAbsorbAndResist with the spell: 25 resisted, the ward takes 30 of the 75 left, and the log carries the 45 that
        // SendSpellNonMeleeDamageLog computes (SpellCaster.cpp:1098-1104), but nothing is dealt.
        Assert.Equal(SpellPackets.BuildSpellNonMeleeDamageLog(creature.Guid, caster.Guid, FireFlames, 45, SpellSchool.Fire, absorbed: 30, resisted: 25),
            Assert.Single(Packets(casterSession, WorldOpcode.SmsgSpellnonmeleedamagelog)));
        Assert.False(kit.System.HasAura(creature, FireWard));
        Assert.Equal(1000u, creature.Health);
    }

    // --- DealDamageMods in Player::EnvironmentalDamage ----------------------------------------

    [Fact]
    public void OnATaxiFlight_NothingIsTaken_AndTheLogShowsItAllAbsorbed()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = AddPlayer(kit, 1);
        RuleTestKit.Apply(kit, player, FireWard);
        player.UnitFlags |= UnitFlags.TaxiFlight;
        session.Clear();

        // The ward still takes its 30 (Player.cpp:728); DealDamageMods then adds the other 70 to the absorb (SpellCaster.cpp:1779, 1783-1785).
        Assert.Equal(0u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Lava, 100));
        Assert.Equal(EnvironmentalDamage.BuildLog(player.Guid.Value, EnvironmentalDamageType.Lava, 0, 100, 0), Assert.Single(Logs(session)));
        Assert.Equal(1000u, player.Health);
        Assert.False(kit.System.HasAura(player, FireWard));
    }

    [Fact]
    public void APriestInTheSpiritOfRedemption_TakesNothing_ButAnotherClassWithTheAuraDoes()
    {
        using SpellTestKit kit = Kit();
        (Player player, FakeSession session) = AddPlayer(kit, 1);
        player.Map!.Combat.SpellMitigation = kit.System;
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Priest);
        RuleTestKit.Apply(kit, player, SpellSystem.SpiritOfRedemptionSpell);
        session.Clear();

        Assert.Equal(0u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Drowning, 100));
        Assert.Equal(EnvironmentalDamage.BuildLog(player.Guid.Value, EnvironmentalDamageType.Drowning, 0, 100, 0), Assert.Single(Logs(session)));
        Assert.Equal(1000u, player.Health);

        // The rule asks the dealer's class too (GetClass() == CLASS_PRIEST, SpellCaster.cpp:1781).
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warrior);
        Assert.Equal(100u, EnvironmentalDamage.Apply(kit.World, player, EnvironmentalDamageType.Drowning, 100));
        Assert.Equal(900u, player.Health);
    }

    [Fact]
    public void DealDamageMods_LetsAnOrdinaryHitThrough_ButNotOneOnAnEvadingCreatureOrADeadVictim()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = AddPlayer(kit, 1);
        Map map = kit.World.GetMap(0);
        var creature = new CombatTestUnit();
        creature.Spawn(map, 3, 0, player.Z);
        uint damage = 100;
        uint absorb = 5;

        map.Combat.DealDamageMods(player, creature, ref damage, ref absorb);
        Assert.Equal((100u, 5u), (damage, absorb));

        creature.IsInEvadeMode = true; // SpellCaster.cpp:1780
        map.Combat.DealDamageMods(player, creature, ref damage, ref absorb);
        Assert.Equal((0u, 105u), (damage, absorb));

        creature.IsInEvadeMode = false;
        map.Combat.Kill(player, creature); // SpellCaster.cpp:1778
        (damage, absorb) = (100u, 0u);
        map.Combat.DealDamageMods(player, creature, ref damage, ref absorb);
        Assert.Equal((0u, 100u), (damage, absorb));
    }
}
