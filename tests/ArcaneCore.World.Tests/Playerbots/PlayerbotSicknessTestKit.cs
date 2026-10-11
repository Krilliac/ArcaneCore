using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// Resurrection sickness as the 1.12.1 client data has it (Spell.dbc row 15007, client-dbc-5875): three auras, each base points -76
/// with one die (-75%): MOD_PERCENT_STAT on every stat (misc -1), MOD_DAMAGE_PERCENT_DONE and MOD_RESISTANCE_PCT on every school
/// (misc 127), ten minutes, kept through death (AttributesEx3 0x00100000, SPELL_ATTR_EX3_DEATH_PERSISTENT), cast on the caster.
/// </summary>
internal static class PlayerbotSicknessTestKit
{
    internal static readonly SpellInfo Sickness = new()
    {
        Id = SpiritHealerResurrection.ResurrectionSicknessSpell,
        Name = "Resurrection Sickness",
        AttributesEx3 = 0x00100000,
        Duration = new SpellDuration(600_000, 0, 600_000),
        Effects =
        [
            Effect(AuraType.ModPercentStat, -1),
            Effect(AuraType.ModDamagePercentDone, 127),
            Effect(AuraType.ModResistancePct, 127),
        ],
    };

    private static SpellEffectInfo Effect(AuraType aura, int misc) => new()
    {
        Effect = SpellEffectName.ApplyAura, AuraType = aura, BasePoints = -76, BaseDice = 1, DieSides = 1, MiscValue = misc,
        TargetA = SpellImplicitTarget.UnitCaster,
    };

    /// <summary>World thread: add the sickness to the spell store (beside what it holds) and put it on <paramref name="player"/>.</summary>
    internal static SpellAuraHolder Apply(IServiceProvider world, Player player)
    {
        SpellSystem spells = world.GetRequiredService<SpellFeature>().System;
        if (spells.Store.Get(Sickness.Id) is null)
            spells.Store = new SpellStore([.. spells.Store.All, Sickness], [], []);
        Assert.Equal(SpellCastResult.CastOk, spells.CastSpell(player, Sickness.Id, SpellCastTargets.ForSelf(), triggered: true));
        return Assert.Single(spells.GetAuras(player), h => h.Spell.Id == Sickness.Id);
    }
}
