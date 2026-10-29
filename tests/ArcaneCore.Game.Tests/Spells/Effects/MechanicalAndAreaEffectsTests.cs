using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Procs;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Effects;

/// <summary>
/// The effects the wiring review found without a handler (vmangos SpellEffects.cpp table): HEAL_MECHANICAL 75 (Mechanical Patch Kit 15057), EVADE 21
/// (Evade (DND) 108) and NOSTALRIUS 131 (silent).
/// </summary>
public sealed class MechanicalAndAreaEffectsTests
{
    private const uint PatchKit = 15057, EvadeDnd = 962103, FriendHeal = 962104;

    private static SpellInfo Instant(uint id, params SpellEffectInfo[] effects)
        => Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0, Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 };

    private static SpellTestKit Kit() => new(
        Instant(PatchKit, Effect(SpellEffectName.HealMechanical, 100)),
        Instant(FriendHeal, Effect(SpellEffectName.HealMechanical, 100, SpellImplicitTarget.UnitFriend)),
        Instant(EvadeDnd, Effect(SpellEffectName.Evade, 0)));

    [Fact]
    public void TheEffectsHaveHandlers()
    {
        using SpellTestKit kit = Kit();

        foreach (SpellEffectName effect in new[]
        {
            SpellEffectName.HealMechanical, SpellEffectName.Evade, SpellEffectName.Nostalrius,
        })
        {
            Assert.True(kit.System.HasEffectHandler(effect), effect.ToString());
        }

        // vmangos leaves these unused (126, 127) or past the end of its table (132, 133).
        foreach (SpellEffectName effect in new[] { SpellEffectName.Unk126, SpellEffectName.Unk127, SpellEffectName.ApplyAreaAuraRaid, SpellEffectName.ApplyAreaAuraOwner })
        {
            Assert.False(kit.System.HasEffectHandler(effect), effect.ToString());
        }
    }

    [Fact]
    public void HealMechanical_HealsTheTargetByTheEffectValue_NeverCrits_AndNeverMoreThanTheMissingHealth()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 10_000;
        player.Health = 1_000;

        for (int i = 0; i < 40; i++)
        {
            uint before = player.Health;
            kit.System.CastSpell(player, PatchKit, SpellCastTargets.ForSelf(), triggered: true);
            Assert.Equal(before + 100, player.Health); // EffectHeal would crit now and then; DealHeal of the plain amount does not
        }

        player.Health = 9_950;
        kit.System.CastSpell(player, PatchKit, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(10_000u, player.Health);
    }

    /// <summary>
    /// vmangos EffectHealMechanical calls DealHeal only (SpellEffects.cpp:1835-1850, SpellCaster.cpp:1026-1044): no assist threat and no combat for the healer,
    /// unlike EffectHeal, whose heal goes through Unit::DealHeal's threat (Unit::DealHeal adds to the threat list of the target's attackers).
    /// </summary>
    [Fact]
    public void HealMechanical_TriggeredOnAnAllyInCombat_GivesTheHealerNoThreat_AndNoCombat()
    {
        using SpellTestKit kit = Kit();
        kit.System.Damage = new MapCombatDamageSink();
        (Player healer, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3, 0);
        ally.MaxHealth = 10_000;
        ally.Health = 1_000;
        Map map = kit.World.GetMap(0);
        var wolf = new CombatTestUnit();
        wolf.Relocate(5, 0, healer.Z, 0, 0);
        map.AddObject(wolf);
        map.Combat.Track(wolf);
        wolf.Combat.Threat.AddThreat(ally, 100);
        map.Combat.SetInCombatState(ally, 5_000);
        MapCombat.ClearInCombat(healer);

        kit.System.CastSpell(healer, FriendHeal, SpellCastTargets.ForUnit(ally.Guid), triggered: true);

        Assert.Equal(1_100u, ally.Health); // the heal landed
        Assert.Equal(0f, wolf.Combat.Threat.GetThreat(healer));
        Assert.False(healer.Combat.IsInCombat);
    }

    /// <summary>
    /// A cast that is not triggered puts the caster in combat with an in-combat ally like any assist (vmangos Spell.cpp:1713-1718); the heal itself adds no threat.
    /// </summary>
    [Fact]
    public void HealMechanical_NotTriggeredOnAnAllyInCombat_PutsTheCasterInCombat_ButGivesItNoThreat()
    {
        using SpellTestKit kit = Kit();
        kit.System.Damage = new MapCombatDamageSink();
        (Player healer, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3, 0);
        ally.MaxHealth = 10_000;
        ally.Health = 1_000;
        Map map = kit.World.GetMap(0);
        var wolf = new CombatTestUnit();
        wolf.Relocate(5, 0, healer.Z, 0, 0);
        map.AddObject(wolf);
        map.Combat.Track(wolf);
        wolf.Combat.Threat.AddThreat(ally, 100);
        map.Combat.SetInCombatState(ally, 5_000);
        MapCombat.ClearInCombat(healer);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(healer, FriendHeal, SpellCastTargets.ForUnit(ally.Guid), triggered: false));
        kit.Advance(100);

        Assert.Equal(1_100u, ally.Health);
        Assert.Equal(0f, wolf.Combat.Threat.GetThreat(healer));
        Assert.True(healer.Combat.IsInCombat);
    }

    /// <summary>
    /// effect 75 sets no m_healing, so the hit fires the plain no-damage proc with amount 0, not the heal proc with the healed amount
    /// (vmangos Spell.cpp:1294 heal branch versus :1454 no-damage branch).
    /// </summary>
    [Fact]
    public void HealMechanical_FiresThePlainHitProc_WithAmountZero_NotTheHealProc()
    {
        using SpellTestKit kit = Kit();
        (Player healer, _) = kit.AddPlayer(1);
        (Player ally, _) = kit.AddPlayer(2, 3, 0);
        ally.MaxHealth = 10_000;
        ally.Health = 1_000;
        var events = new List<ProcEvent>();
        kit.System.ProcEventProcessed += (_, e) => events.Add(e);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(healer, FriendHeal, SpellCastTargets.ForUnit(ally.Guid), triggered: false));
        kit.Advance(100);

        Assert.Equal(1_100u, ally.Health);
        List<ProcEvent> hits = events.Where(e => (e.Extra & ProcFlagsEx.CastEnd) == 0).ToList(); // the cast-end event is a separate one
        Assert.NotEmpty(hits); // the hit did fire a proc, or the amount check below would prove nothing
        Assert.All(hits, e => Assert.Equal(0u, e.Amount));
    }

    [Fact]
    public void HealMechanical_DoesNothingForADeadTarget()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 10_000;
        player.Health = 0;

        kit.System.CastSpell(player, PatchKit, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(0u, player.Health);
    }

    [Fact]
    public void EvadeAndNostalrius_CastWithoutAnEffectReport()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        SpellCastResult result = kit.System.CastSpell(player, EvadeDnd, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(SpellCastResult.CastOk, result);
    }
}
