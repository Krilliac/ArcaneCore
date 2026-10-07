using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// vmangos CritterAI (AI/CritterAI.cpp:16-60): a critter (creature type 8) never attacks and never fights back. When it takes damage that does
/// not kill it, or is hit by a hostile spell that does no direct damage, it runs from the attacker for 30 seconds; a critter that has been in combat
/// for 30 seconds without a new hit goes home (evade). Selected for a template of creature type 8 without an AIName (vmangos selects it before the
/// permit contest, AI/CreatureAISelector.cpp:78-79) unless its AIName names another AI (or, with the opt-in <c>Creatures:ImplicitEventAi</c>, it has EventAI rows); also available by name.
/// Limits: vmangos' DamageTaken hook sees the damage before it is applied, <see cref="OnAttackedBy"/> runs after a non-lethal hit (a lethal hit does not run it,
/// like the reference's <c>uiDamage &lt; health</c> test); the damage amount is not part of the hook.
/// </summary>
public sealed class CritterAI(Creature creature) : CreatureAI(creature)
{
    /// <summary>vmangos ESCAPE_TIMER: how long a critter flees and how long it stays in combat without a new hit.</summary>
    public const uint EscapeTimerMs = 30000;

    private uint _combatTimerMs;

    /// <summary>vmangos CritterAI::MoveInLineOfSight does nothing.</summary>
    protected override bool CallsGuardsOnSight => false;

    /// <summary>vmangos CritterAI::MoveInLineOfSight and AttackStart do nothing: a critter never attacks.</summary>
    public override bool AttackStart(Unit target) => false;

    public override void OnAttackedBy(Unit attacker) => Flee(attacker);

    public override void OnSpellHit(Unit caster, SpellInfo spell)
    {
        if (!spell.IsPositive && !IsDirectDamageSpell(spell) && Me.IsAlive)
        {
            Flee(caster);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!Me.Combat.IsInCombat)
        {
            return;
        }

        if (_combatTimerMs <= diffMs)
        {
            EnterEvadeMode();
            _combatTimerMs = EscapeTimerMs;
        }
        else
        {
            _combatTimerMs -= diffMs;
        }
    }

    private void Flee(Unit from)
    {
        if (Me.Motion.CurrentType != MovementGeneratorType.Fleeing)
        {
            Me.Motion.MoveFleeing(from, EscapeTimerMs);
        }

        _combatTimerMs = EscapeTimerMs;
    }

    /// <summary>vmangos SpellInternal::IsDirectDamageSpell (Spells/SpellMgr.cpp:3233-3242, IsDirectDamageEffect in SpellEntry.h:486-503).</summary>
    private static bool IsDirectDamageSpell(SpellInfo spell)
        => spell.Effects.Any(static e => e.Effect is SpellEffectName.Instakill or SpellEffectName.SchoolDamage or SpellEffectName.EnvironmentalDamage
            or SpellEffectName.HealthLeech or SpellEffectName.WeaponDamageNoschool or SpellEffectName.WeaponPercentDamage
            or SpellEffectName.WeaponDamage or SpellEffectName.PowerBurn or SpellEffectName.NormalizedWeaponDmg);
}
