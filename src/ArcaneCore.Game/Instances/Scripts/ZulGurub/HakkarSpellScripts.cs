using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

/// <summary>mangos-classic .../zulgurub/boss_hakkar.cpp BloodSiphon::OnEffectExecute.</summary>
[SpellScript(24324)]
public sealed class HakkarBloodSiphonScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0)
        {
            uint spell = context.System.HasAura(context.Target, 24321) ? 24323u : 24322u;
            context.System.CastSpell(context.Target, spell, SpellCastTargets.ForUnit(context.Caster.Guid), triggered: true);
        }
    }
}

/// <summary>mangos-classic .../zulgurub/boss_hakkar.cpp HakkarPowerDown::OnEffectExecute.</summary>
[SpellScript(24693)]
public sealed class HakkarPowerDownScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.EffectIndex == 0)
        {
            context.System.RemoveScriptAuraStack(context.Target, 24692);
        }
    }
}
