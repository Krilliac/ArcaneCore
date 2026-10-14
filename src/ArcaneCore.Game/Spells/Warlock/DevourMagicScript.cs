using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.Warlock;

/// <summary>
/// Felhunter Devour Magic (19505, 19731, 19734, 19736; vmangos scripts/spells/spell_warlock.cpp:203-237):
/// when effect 0 dispels at least one magic effect, the caster casts the corresponding rank of Devour Magic's
/// self-heal (19658, 19732, 19733, 19735) on itself as a triggered spell.
/// </summary>
[SpellScript(19505, 19731, 19734, 19736)]
public sealed class DevourMagicScript : ISpellScript
{
    public static readonly IReadOnlyDictionary<uint, uint> HealSpells = new Dictionary<uint, uint>
    {
        [19505] = 19658,
        [19731] = 19732,
        [19734] = 19733,
        [19736] = 19735,
    };

    public void OnSuccessfulDispel(SpellEffectContext context, int removedStacks)
    {
        if (context.EffectIndex == 0 && removedStacks > 0 && HealSpells.TryGetValue(context.Spell.Id, out uint healSpell))
        {
            context.System.CastSpell(context.Caster, healSpell, SpellCastTargets.ForSelf(), triggered: true);
        }
    }
}
