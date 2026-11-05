using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.BlackrockDepths;

/// <summary>
/// ScriptName <c>npc_theldren_trigger</c> (entry 16079), clean-room spec 230-npc_theldren_trigger.md. The unit the Thelrin banner's second effect
/// (spell 27517, effect index 1: SPELL_EFFECT_DUMMY with target 38, TARGET_UNIT_SCRIPT_NEAR_CASTER, resolved through the spell_script_target row
/// (27517, 1, 16079); <see cref="ThelrinBannerSpellScript"/> reads it) hits: an inert target. It never aggroes, is never drawn into combat, never moves, says and
/// casts nothing (spec section 3) and has no timers or summons. Being not-selectable and immune is template data, not script work. State: none.
/// World thread (map update).
/// </summary>
public sealed class TheldrenTriggerAI(Creature creature) : CreatureAI(creature)
{
    public const string ScriptName = "npc_theldren_trigger";

    public const uint Entry = 16079;

    /// <summary>Spell 27517 "Summon Thelrin DND", the banner whose script effect this unit is the target of.</summary>
    public const uint BannerSpell = 27517;

    protected override bool CallsGuardsOnSight => false;

    public override void OnUpdate(uint diffMs)
    {
    }

    public override void OnAttackedBy(Unit attacker)
    {
    }

    public override bool AttackStart(Unit target) => false;

    public override void MoveInLineOfSight(Unit who)
    {
    }
}
