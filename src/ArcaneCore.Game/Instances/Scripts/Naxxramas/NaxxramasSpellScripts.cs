using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// Corrupted Mind's 1.12 healer class follow-up spells: mangos-classic
/// ScriptDevAI/scripts/eastern_kingdoms/naxxramas/boss_loatheb.cpp
/// CorruptedMind::OnEffectExecute (vmangos boss_loatheb.cpp spell script differs
/// on the priest ID; the ScriptDev2 mapping is used here).
/// </summary>
[SpellScript(29201)]
public sealed class NaxxramasSpellScripts : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex != 0 || context.Target is not Player target) return;
        uint child = target.Class switch
        {
            Class.Priest => 29185,
            Class.Druid => 29194,
            Class.Paladin => 29196,
            Class.Shaman => 29198,
            _ => 0,
        };
        if (child != 0)
            context.System.CastSpell(context.Caster, child,
                SpellCastTargets.ForUnit(target.Guid), triggered: true);
    }
}

/// <summary>
/// Widow's Embrace (28732), cast onto Faerlina by a mind-controlled Naxxramas Worshipper: its script effect
/// kills the casting worshipper. vmangos Spell::EffectScriptEffect case 28732 (SpellEffects.cpp) kills the
/// caster; mangos-classic boss_faerlina.cpp WidowEmbrace::OnEffectExecute does the same through 28748. The
/// effect on Faerlina herself is boss_faerlinaAI::SpellHit, in <see cref="NaxxramasBossAI.OnSpellHit"/>.
/// </summary>
[SpellScript(28732)]
public sealed class WidowsEmbraceScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Spell.Effects[context.EffectIndex].Effect != SpellEffectName.ScriptEffect) return;
        Unit caster = context.Caster;
        if (caster.IsAlive)
            context.System.Damage.DealSpellDamage(caster, caster, context.Spell, caster.Health, periodic: false);
    }
}
