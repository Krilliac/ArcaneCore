using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Tests.CombatCore;
using ArcaneCore.Game.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Effects;

/// <summary>
/// The effects the wiring review found without a handler (vmangos SpellEffects.cpp table): HEAL_MECHANICAL 75 (Mechanical Patch Kit 15057), EVADE 21
/// (Evade (DND) 108) and NOSTALRIUS 131 (silent), APPLY_AREA_AURA_FRIEND 128 and APPLY_AREA_AURA_ENEMY 129 (Sam's Test Aura 26659 has a 129 effect).
/// </summary>
public sealed class MechanicalAndAreaEffectsTests
{
    private const uint PatchKit = 15057, EvadeDnd = 962103, FriendHeal = 962104, FriendAuraAtTarget = 962105, FriendAura = 962101, EnemyAura = 962102;

    private static SpellInfo Instant(uint id, params SpellEffectInfo[] effects)
        => Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0, Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 };

    private static SpellTestKit Kit() => new(
        Instant(PatchKit, Effect(SpellEffectName.HealMechanical, 100)),
        Instant(FriendHeal, Effect(SpellEffectName.HealMechanical, 100, SpellImplicitTarget.UnitFriend)),
        Instant(FriendAuraAtTarget, Effect(SpellEffectName.ApplyAreaAuraFriend, 5, SpellImplicitTarget.UnitFriend, aura: AuraType.ModResistance, misc: 1) with { Radius = 20 }),
        Instant(EvadeDnd, Effect(SpellEffectName.Evade, 0)),
        Instant(FriendAura, Effect(SpellEffectName.ApplyAreaAuraFriend, 5, aura: AuraType.ModResistance, misc: 1) with { Radius = 20 }),
        Instant(EnemyAura, Effect(SpellEffectName.ApplyAreaAuraEnemy, 5, aura: AuraType.ModResistance, misc: 1) with { Radius = 20 }));

    [Fact]
    public void TheEffectsHaveHandlers()
    {
        using SpellTestKit kit = Kit();

        foreach (SpellEffectName effect in new[]
        {
            SpellEffectName.HealMechanical, SpellEffectName.Evade, SpellEffectName.Nostalrius,
            SpellEffectName.ApplyAreaAuraFriend, SpellEffectName.ApplyAreaAuraEnemy,
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
    public void HealMechanical_OfAnAllyInCombat_GivesTheHealerNeitherThreatNorCombat()
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
        MapCombat.ClearInCombat(healer);

        kit.System.CastSpell(healer, FriendHeal, SpellCastTargets.ForUnit(ally.Guid), triggered: true);

        Assert.Equal(1_100u, ally.Health); // the heal landed
        Assert.Equal(0f, wolf.Combat.Threat.GetThreat(healer));
        Assert.False(healer.Combat.IsInCombat);
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

    [Fact]
    public void FriendAreaAura_GivesTheCasterAndTheFriendlyUnitsInRangeTheAura_NotTheOnesOutOfRange()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player near, _) = kit.AddPlayer(2, 10, 0);
        (Player far, _) = kit.AddPlayer(3, 30, 0);
        kit.System.Relations = new FakeRelations();

        kit.System.CastSpell(caster, FriendAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);

        Assert.Single(kit.System.GetAuras(caster));
        SpellAuraHolder onNear = Assert.Single(kit.System.GetAuras(near));
        Assert.Equal(FriendAura, onNear.Spell.Id);
        Assert.Equal(caster.Guid, onNear.CasterGuid);
        Assert.Empty(kit.System.GetAuras(far));
    }

    [Fact]
    public void EnemyAreaAura_GivesHostileUnitsInRangeTheAura_AndNeverTheCasterOrFriends()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2, 5, 0);
        (Player enemy, _) = kit.AddPlayer(3, 10, 0);
        (Player farEnemy, _) = kit.AddPlayer(4, 30, 0);
        kit.System.Relations = new FakeRelations { Hostile = { enemy.Guid, farEnemy.Guid } };

        kit.System.CastSpell(caster, EnemyAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(100);

        // vmangos AreaAura::AreaAura: the caster's own aura is aura NONE, a holder that spreads and does nothing to the caster
        SpellAuraHolder onCaster = Assert.Single(kit.System.GetAuras(caster));
        Assert.Equal(EnemyAura, onCaster.Spell.Id);
        Assert.Equal(AuraType.None, Assert.Single(onCaster.Auras.OfType<SpellAura>()).Type);
        Assert.Empty(kit.System.GetAuras(friend));
        SpellAuraHolder onEnemy = Assert.Single(kit.System.GetAuras(enemy));
        Assert.Equal(EnemyAura, onEnemy.Spell.Id);
        Assert.Empty(kit.System.GetAuras(farEnemy));
    }

    [Fact]
    public void AreaAura_AUnitThatHoldsTheSpellsAuraFromAnotherCaster_GetsNoSecondOne()
    {
        using SpellTestKit kit = Kit();
        (Player first, _) = kit.AddPlayer(1);
        (Player second, _) = kit.AddPlayer(2, 5, 0);
        (Player between, _) = kit.AddPlayer(3, 2, 0);
        kit.System.Relations = new FakeRelations();

        kit.System.CastSpell(first, FriendAura, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(second, FriendAura, SpellCastTargets.ForSelf(), triggered: true);

        // vmangos AreaAura::Update (SpellAuras.cpp:757-784): the first caster's child on 'between' blocks the second caster's child there
        // (the old check only looked at holders the same caster had cast).
        SpellAuraHolder onBetween = Assert.Single(kit.System.GetAuras(between));
        Assert.Equal(first.Guid, onBetween.CasterGuid);
        Assert.Single(kit.System.GetAuras(first));
    }

    [Fact]
    public void AreaAura_OnAnotherUnitTarget_AppliesTheEffectThere_WithoutSpreading()
    {
        using SpellTestKit kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5, 0);
        (Player bystander, _) = kit.AddPlayer(3, 6, 0);
        kit.System.Relations = new FakeRelations();

        // vmangos EffectApplyAreaAura applies to unitTarget, whoever it is (SpellEffects.cpp:2323-2332); only the caster's own aura spreads.
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, FriendAuraAtTarget, SpellCastTargets.ForUnit(target.Guid), triggered: true));
        kit.Advance(100);

        Assert.True(kit.System.HasAura(target, FriendAuraAtTarget));
        Assert.Empty(kit.System.GetAuras(bystander));
        Assert.Empty(kit.System.GetAuras(caster));
    }
}
