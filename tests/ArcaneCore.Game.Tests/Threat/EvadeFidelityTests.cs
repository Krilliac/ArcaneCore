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

    private static ThreatArena Arena() => new(
        Spell(NegativeAura, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), SpellVisual = 1 },
        Spell(TimedBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), SpellVisual = 1 },
        Spell(PermanentBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 },
        Spell(OwnBuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with { Duration = new SpellDuration(60000, 0, 60000), SpellVisual = 1 });

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
