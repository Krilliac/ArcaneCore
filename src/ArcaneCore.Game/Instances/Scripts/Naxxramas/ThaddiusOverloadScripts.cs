using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// Shock (28159), the beam each Tesla Coil (16218) fires at Thaddius when the coils overload. Its DUMMY acts only on Thaddius (15928) while he
/// still carries his spawn stun, Thadius Spawn (28160): the stun goes and Thaddius casts Thadius Lightning Visual (28136) on himself.
/// mangos-classic ScriptDevAI naxxramas/boss_thaddius.cpp ShockThaddius (the Tesla game objects' visual reset there is not modelled: ArcaneCore
/// keeps no state on them); vmangos boss_thaddius.cpp EVENT_TRANSITION_2 casts the same beam and visual and drops the stun from the AI.
/// Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(Shock)]
public sealed class ThaddiusShockScript : ISpellScript
{
    public const uint Shock = 28159, ThaddiusSpawn = 28160, LightningVisual = 28136, ThaddiusEntry = 15928;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex != 0 || context.Target is not Creature { Entry: ThaddiusEntry } thaddius
            || !context.System.HasAura(thaddius, ThaddiusSpawn)) return;
        context.System.RemoveAuras(thaddius, ThaddiusSpawn);
        context.System.CastSpell(thaddius, LightningVisual, SpellCastTargets.ForSelf(), triggered: true);
    }
}

/// <summary>
/// Thadius Lightning Visual (28136): the creature it hits (Thaddius, casting it on himself) ends its scripted hold and engages the whole raid.
/// mangos-classic ScriptDevAI naxxramas/boss_thaddius.cpp ThaddiusLightningVisual (SetCombatScriptStatus(false), SetInCombatWithZone). The zone
/// pull is idempotent with the instance's own overload pull (<see cref="NaxxramasInstance"/> Thaddius overload).
/// Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(ThaddiusShockScript.LightningVisual)]
public sealed class ThaddiusLightningVisualScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.EffectIndex == 0 && context.Target is Creature { IsAlive: true } creature && creature.System is { } creatures)
            creatures.SetInCombatWithZone(creature);
    }
}

/// <summary>
/// Magnetic Pull (28339), the half of the tank swap Feugen casts on Stalagg (its spell_script_target is Feugen 15930's partner list in ClassicDB):
/// the hit add casts the real Magnetic Pull (28337, the jump) at the caster's victim. mangos-classic ScriptDevAI naxxramas/boss_thaddius.cpp
/// MagneticPullThaddius. ArcaneCore's <see cref="ThaddiusAddAI"/> casts 28337 directly (the vmangos DoMagneticPull form); this script keeps the
/// spell correct for any caster that uses the cmangos two-step form. Reimplemented, no GPL source copied.
/// </summary>
[SpellScript(MagneticPullB)]
public sealed class ThaddiusMagneticPullScript : ISpellScript
{
    public const uint MagneticPullB = 28339, MagneticPullJump = 28337;

    public void OnEffectExecute(SpellEffectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Effect.Effect != SpellEffectName.ScriptEffect || context.Caster.Combat.Victim is not { } victim
            || context.Target == context.Caster) return;
        context.System.CastSpell(context.Target, MagneticPullJump, SpellCastTargets.ForUnit(victim.Guid, context.Cast), triggered: true);
    }
}
