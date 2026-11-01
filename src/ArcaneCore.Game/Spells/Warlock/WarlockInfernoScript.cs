using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Inferno 1122 and 24670 (vmangos scripts/spells/spell_warlock.cpp:239-253, WarlockInfernoScript). When the Infernal (SUMMON_DEMON) is in the
/// world the warlock casts Enslave Demon 20882 on it, triggered (no cost, no cooldown), then the Infernal casts a short Root 22707 and the
/// Inferno Effect 22703 (the landing damage and stun) on itself, triggered. Raised by the demon summon (<see cref="ISpellScript.OnSummon"/>).
/// </summary>
[SpellScript(Inferno, InfernoRank2)]
public sealed class WarlockInfernoScript : ISpellScript
{
    public const uint Inferno = 1122, InfernoRank2 = 24670, EnslaveDemon = 20882, InfernoRoot = 22707, InfernoEffect = 22703;

    public void OnSummon(SpellEffectContext context, Creature summon)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(summon);
        context.System.CastCustomSpell(context.Caster, EnslaveDemon, SpellCastTargets.ForUnit(summon.Guid, context.Cast), null);
        context.System.CastCustomSpell(summon, InfernoRoot, SpellCastTargets.ForSelf(), null);
        context.System.CastCustomSpell(summon, InfernoEffect, SpellCastTargets.ForSelf(), null);
    }
}
