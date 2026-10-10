using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Threat;

/// <summary>
/// CreatureAI::EnterEvadeMode (vmangos AI/CreatureAI.cpp:323-346): no instant heal (the creature regenerates, Creature::RegenerateAll
/// Objects/Creature.cpp:1087-1161), RemoveAurasAtReset (:3611-3630), charmed creatures keep their auras.
/// </summary>
public sealed class EvadeFidelityTests
{
    private const uint NegativeAura = 940001;
    private const uint TimedBuff = 940002;
    private const uint PermanentBuff = 940003;
    private const uint OwnBuff = 940004;
    private const uint Sheep = 940005;
    private const uint Thrash = 8876; // on cMaNGOS IsSpellRemovedOnEvade's keep list

    private static ThreatArena Arena() => new(
        Spell(NegativeAura, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), SpellVisual = 1 },
        Spell(TimedBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), SpellVisual = 1 },
        Spell(PermanentBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 },
        Spell(OwnBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), SpellVisual = 1 },
        Spell(Sheep, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.ModConfuse),
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Transform, misc: 1)) with
        { Duration = new SpellDuration(60000, 0, 60000), SpellVisual = 1, SpellFamilyName = 3, PreventionType = 1 },
        Spell(Thrash, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 });

    /// <summary>The development switch that restores the old instant evade snap (<c>Creatures:Movement:EvadeRestoresFullHealth</c>).</summary>
    private static CreatureOptions WithEvadeSnap()
    {
        var options = new CreatureOptions();
        options.Movement.EvadeRestoresFullHealth = true;
        return options;
    }

    private static Creature Hurt(ThreatArena a, uint guid, Func<CreatureTemplate, CreatureTemplate>? tweak = null, CreatureOptions? options = null)
    {
        Creature wolf = a.SpawnCreature(guid, 300, tweak, options); // far from the players, which would pull it again
        wolf.Health = wolf.MaxHealth / 10;
        a.Map.Combat.Track(wolf); // a creature that fought is tracked by map combat, which regenerates it
        return wolf;
    }

    [Fact]
    public void AnEvadingCreature_IsNotHealedAtOnce_ItRegeneratesAThirdPerFiveSecondTick()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        uint max = wolf.MaxHealth;
        uint start = wolf.Health;
        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.True(wolf.IsInEvadeMode);
        Assert.Equal(start, wolf.Health);

        // The first tick of a unit that map combat starts to track lands at once (vmangos sets the 5 s timer after it), so one tick at most by now.
        Run(a.Kit.World, 1000);
        Assert.InRange(wolf.Health, start, start + (max / 3) + 1);
        Assert.True(wolf.Health < max);

        // then a third of the maximum every 5 seconds: still hurt after 6 s, full within 16 s
        Run(a.Kit.World, 5000);
        Assert.InRange(wolf.Health, start + (max / 3) - 1, start + (2 * (max / 3)) + 1);
        Run(a.Kit.World, 10000);
        Assert.Equal(max, wolf.Health);
    }

    [Fact]
    public void EvadeRestoresFullHealth_RestoresTheOldInstantSnap()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90, options: WithEvadeSnap());

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.Equal(wolf.MaxHealth, wolf.Health);
    }

    [Fact]
    public void AnEvadeRemovesTheAuras_ExceptANonPermanentPositiveOneCastByAPlayer()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        a.Cast(a.Tank, wolf, NegativeAura);
        a.Cast(a.Tank, wolf, TimedBuff);
        a.Cast(a.Healer, wolf, PermanentBuff);
        a.Kit.System.CastSpell(wolf, OwnBuff, SpellCastTargets.ForUnit(wolf.Guid), triggered: true); // its own buff
        Assert.True(a.Kit.System.HasAura(wolf, NegativeAura));

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.False(a.Kit.System.HasAura(wolf, NegativeAura));
        Assert.False(a.Kit.System.HasAura(wolf, PermanentBuff));
        Assert.Equal(1, a.Kit.System.GetAuras(wolf).Count(h => h.Spell.Id == TimedBuff && h.CasterGuid == a.Tank.Guid)); // the player's timed buff stays
        Assert.DoesNotContain(a.Kit.System.GetAuras(wolf), h => h.CasterGuid == wolf.Guid);
    }

    [Fact]
    public void AnEvadeKeepsTheAurasCmangosNeverRemovesOnEvade()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        a.Kit.System.CastSpell(wolf, Thrash, SpellCastTargets.ForUnit(wolf.Guid), triggered: true); // a spawn passive
        a.Kit.System.CastSpell(wolf, OwnBuff, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.True(a.Kit.System.HasAura(wolf, Thrash));
        Assert.False(a.Kit.System.HasAura(wolf, OwnBuff));
    }

    [Fact]
    public void RemoveAllAuras_StripsEveryAura()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        a.Cast(a.Tank, wolf, NegativeAura);
        a.Cast(a.Tank, wolf, TimedBuff);
        a.Kit.System.CastSpell(wolf, Thrash, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);

        a.Systems[^1].RemoveAllAuras(wolf);

        Assert.DoesNotContain(a.Kit.System.GetAuras(wolf), h => !h.IsRemoved);
    }

    [Fact]
    public void AStillRootedCreature_DoesNotWalkHome_ItsHomeEventsRunWhereItStands()
    {
        // cMaNGOS UnitAI::EnterEvadeMode (AI/BaseAI/UnitAI.cpp:122-125): IsImmobilizedState after the aura removal -> TriggerHomeEvents.
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        a.Systems[^1].SetAiImmobilized(wolf, true, combatOnly: false);

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.False(wolf.IsInEvadeMode);
        Assert.NotEqual(MovementGeneratorType.Home, wolf.Motion.CurrentType);
        Assert.Null(wolf.Combat.Victim);
    }

    [Fact]
    public void ACombatOnlyRoot_EndsAtTheEvade_SoTheCreatureWalksHome()
    {
        // cMaNGOS UnitAI::EnterEvadeMode starts with ClearCombatOnlyRoot (UnitAI.cpp:115).
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        a.Systems[^1].SetAiImmobilized(wolf, true, combatOnly: true);

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.True(wolf.IsInEvadeMode);
        Assert.Equal(MovementGeneratorType.Home, wolf.Motion.CurrentType);
    }

    [Fact]
    public void APolymorphedCreature_RegeneratesHealthInCombat()
    {
        // vmangos Creature::RegenerateAll (Creature.cpp:1094): !IsInCombat() || IsPolymorphed() -> a third of the maximum per 5 s tick.
        using ThreatArena a = Arena();
        CombatEnvironment.Register(a.Kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(a.Kit.System)));
        Creature wolf = Hurt(a, 90);
        a.Map.Combat.DealDamage(a.Tank, wolf, 1, direct: false);
        uint start = wolf.Health;
        Run(a.Kit.World, 6000);
        Assert.True(wolf.Combat.IsInCombat);
        Assert.Equal(start, wolf.Health); // in combat, not polymorphed: no regeneration

        a.Cast(a.Tank, wolf, Sheep);
        Run(a.Kit.World, 6000);
        Assert.True(wolf.Combat.IsInCombat);
        Assert.True(wolf.Health >= start + (wolf.MaxHealth / 3));
    }

    [Fact]
    public void APolymorphedCreatureAPlayerOwns_RegeneratesATenth()
    {
        // vmangos Creature::RegenerateHealth (Creature.cpp:1141-1146): GetCharmerOrOwnerGuid().IsPlayer() and polymorphed -> max / 10.
        using ThreatArena a = Arena();
        CombatEnvironment.Register(a.Kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(a.Kit.System)));
        Creature wolf = Hurt(a, 90);
        wolf.SetUInt64(UpdateFields.UnitFieldSummonedby, a.Healer.Guid.Value);
        a.Cast(a.Tank, wolf, Sheep);
        uint start = wolf.Health;

        Run(a.Kit.World, 5000);

        Assert.Equal(start + (wolf.MaxHealth / 10), wolf.Health);
    }

    private static void ManaCaster(Creature wolf)
    {
        wolf.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Mage);
        wolf.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Mana);
        wolf.SetUInt32(UpdateFields.UnitFieldMaxpower1, 5000);
        wolf.SetUInt32(UpdateFields.UnitFieldPower1, 0);
        wolf.SetUInt32(UpdateFields.UnitFieldStat0 + 3, 100); // intellect
        wolf.SetUInt32(UpdateFields.UnitFieldStat0 + 4, 100); // spirit
    }

    [Fact]
    public void InCombat_ACreatureRegainsItsManaRegenRate_NotAThird()
    {
        // vmangos Creature::RegenerateMana (Creature.cpp:1116-1120) with UpdateManaRegen (StatSystem.cpp:808-818): a mage with 100 spirit
        // and 100 intellect gains ((100 / 4 + 12.5) / 2 + 0.6 x 10 / 5) x 5 = 99.75 per tick, rounded by chance.
        using ThreatArena a = Arena();
        CombatEnvironment.Register(a.Kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(a.Kit.System)));
        Creature wolf = Hurt(a, 90);
        ManaCaster(wolf);
        a.Map.Combat.DealDamage(a.Tank, wolf, 1, direct: false);

        Run(a.Kit.World, 5000);

        Assert.True(wolf.Combat.IsInCombat);
        Assert.InRange(SpellSystem.GetPower(wolf, PowerType.Mana), 99u, 100u);
    }

    [Fact]
    public void OutOfCombat_ACreatureRegainsAThirdOfItsMana()
    {
        using ThreatArena a = Arena();
        CombatEnvironment.Register(a.Kit.World, new CombatEnvironment(new CombatOptions(), new SpellSystemPowerAuras(a.Kit.System)));
        Creature wolf = Hurt(a, 90);
        ManaCaster(wolf);

        Run(a.Kit.World, 5000);

        Assert.False(wolf.Combat.IsInCombat);
        Assert.Equal(5000u / 3, SpellSystem.GetPower(wolf, PowerType.Mana));
    }

    [Fact]
    public void KeepPositiveAurasOnEvade_RemovesOnlyTheNegativeAuras()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90, t => t with { ExtraFlags = 0x1000, ExtraFlagsDialect = CreatureExtraFlagsDialect.VMangos });
        a.Cast(a.Tank, wolf, NegativeAura);
        a.Kit.System.CastSpell(wolf, PermanentBuff, SpellCastTargets.ForUnit(wolf.Guid), triggered: true);

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.False(a.Kit.System.HasAura(wolf, NegativeAura));
        Assert.True(a.Kit.System.HasAura(wolf, PermanentBuff));
    }

    [Fact]
    public void EvadeResetsAurasOff_KeepsEverything()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90, options: new CreatureOptions { EvadeResetsAuras = false });
        a.Cast(a.Tank, wolf, NegativeAura);

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.True(a.Kit.System.HasAura(wolf, NegativeAura));
    }

    [Fact]
    public void ACharmedCreature_KeepsItsAuras_AndDoesNotRunHome()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        a.Cast(a.Tank, wolf, NegativeAura);
        wolf.SetUInt64(UpdateFields.UnitFieldCharmedby, a.Tank.Guid.Value);

        a.Systems[^1].EnterEvadeMode(wolf);

        Assert.True(a.Kit.System.HasAura(wolf, NegativeAura));
        Assert.NotEqual(MovementGeneratorType.Home, wolf.Motion.CurrentType);
    }

    [Fact]
    public void TheEvadedEvent_FiresOncePerEvade_AndNotForADeadCreature()
    {
        using ThreatArena a = Arena();
        Creature wolf = Hurt(a, 90);
        CreatureMapSystem wolfSystem = a.Systems[^1];
        Creature dead = Hurt(a, 91);
        CreatureMapSystem deadSystem = a.Systems[^1];
        var seen = new List<Creature>();
        wolfSystem.Evaded += seen.Add;
        deadSystem.Evaded += seen.Add;

        wolfSystem.EnterEvadeMode(wolf);
        wolfSystem.EnterEvadeMode(wolf); // already evading
        dead.Health = 0;
        deadSystem.EnterEvadeMode(dead);

        Assert.Same(wolf, Assert.Single(seen));
    }
}
