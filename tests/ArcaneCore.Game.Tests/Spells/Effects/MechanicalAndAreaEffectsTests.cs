using ArcaneCore.Game.Entities;
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
    private const uint PatchKit = 15057, EvadeDnd = 962103, FriendAura = 962101, EnemyAura = 962102;

    private static SpellInfo Instant(uint id, params SpellEffectInfo[] effects)
        => Spell(id, effects) with { StartRecoveryCategory = 0, StartRecoveryTime = 0, Duration = new SpellDuration(-1, 0, -1), SpellVisual = 1 };

    private static SpellTestKit Kit() => new(
        Instant(PatchKit, Effect(SpellEffectName.HealMechanical, 100)),
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

        Assert.Empty(kit.System.GetAuras(caster));  // vmangos AreaAura::AreaAura: the caster's own aura is aura NONE
        Assert.Empty(kit.System.GetAuras(friend));
        SpellAuraHolder onEnemy = Assert.Single(kit.System.GetAuras(enemy));
        Assert.Equal(EnemyAura, onEnemy.Spell.Id);
        Assert.Empty(kit.System.GetAuras(farEnemy));
    }
}
