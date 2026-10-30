using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// ScriptName <c>spell_anchor</c> (Naxxramas, map 533, entry 16137 "Naxxramas Military Sub-Boss Trigger"): the Gothik anchor trigger. Clean-room
/// port of spec <c>533-spell_anchor</c> section 4: an inert marker. No aggro, no combat, no movement, no texts; it stays a valid spell target and
/// is found by the instance's creature queries by entry. The spell chain that runs through these triggers (vmangos boss_gothik.cpp
/// EffectDummyCreature_spell_anchor) is <see cref="GothikAnchorChainScript"/>; the instance starts it (NaxxramasInstance.GothikAnchors.cs).
/// Ownership: one per creature, world thread. No allocation per tick.
/// </summary>
public sealed class GothikAnchorAI(Creature creature) : CreatureAI(creature)
{
    public const string ScriptName = "spell_anchor";

    protected override bool CallsGuardsOnSight => false;

    public static void Register(CreatureAiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        factory.Register(ScriptName, static c => new GothikAnchorAI(c));
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
