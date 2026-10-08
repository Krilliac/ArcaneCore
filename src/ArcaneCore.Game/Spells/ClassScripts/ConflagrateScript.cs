using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Spells.ClassScripts;

/// <summary>
/// Conflagrate (17962, 18930, 18931, 18932; vmangos scripts/spells/spell_warlock.cpp:58-110): the target must carry the caster's Immolate (warlock
/// family, CF_WARLOCK_IMMOLATE bit 2, a PERIODIC_DAMAGE aura) or the cast fails TARGET_AURASTATE (no unit target: BAD_IMPLICIT_TARGETS); when the
/// damage lands, that Immolate is consumed.
/// </summary>
[SpellScript(17962, 18930, 18931, 18932, ExecuteEffects = [SpellEffectName.SchoolDamage])]
public sealed class ConflagrateScript : ISpellScript
{
    public const uint WarlockFamily = 5;
    public const int ImmolateBit = 2;

    public SpellCastResult OnCheckCast(in SpellCastCheckContext context)
    {
        if (context.Target is not { } target)
        {
            return SpellCastResult.BadImplicitTargets;
        }

        return FindImmolate(context.System, target, context.Caster) is null ? SpellCastResult.TargetAurastate : SpellCastResult.CastOk;
    }

    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0 && FindImmolate(context.System, context.Target, context.Caster) is { } immolate)
        {
            context.System.RemoveAurasByCaster(context.Target, immolate.Spell.Id, context.Caster.Guid);
        }
    }

    private static SpellAuraHolder? FindImmolate(SpellSystem system, Unit target, Unit caster)
        => system.GetAuras(target).FirstOrDefault(h => !h.IsRemoved && h.CasterGuid == caster.Guid && h.Spell.IsFitToFamily(WarlockFamily, ImmolateBit)
            && h.Auras.Any(a => a is { Type: AuraType.PeriodicDamage }));
}
