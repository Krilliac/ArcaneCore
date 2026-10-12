using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// ScriptName <c>npc_invible_man</c> (the template's own spelling; Naxxramas, map 533, entry 17286 "Invisible Man"): the Maexxna Web Wrap and
/// Kel'Thuzad helper trigger. Clean-room port of spec <c>533-npc_invible_man</c> section 4: inert and passive. No aggro, no combat, no movement,
/// no casts, no texts or gossip. The Web Wrap sync (spec section 2) is not ported: <c>NaxxramasBossAI</c> (Maexxna) wraps players directly and
/// no caster sends spells 28618-28621, so the sync would be dead code (spec ambiguity 5.1). It remains a valid target of 28455 and 28617.
/// Ownership: one per creature, world thread. No allocation per tick.
/// </summary>
public sealed class InvisibleManAI(Creature creature) : CreatureAI(creature)
{
    public const string ScriptName = "npc_invible_man";

    protected override bool CallsGuardsOnSight => false;

    public static void Register(CreatureAiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factory.Register(ScriptName, static c => new InvisibleManAI(c));
    }

    public override void OnRespawn()
    {
        Me.UnitFlags |= UnitFlags.NotSelectable;
        System?.SetAiImmobilized(Me, true, combatOnly: false);
    }

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
