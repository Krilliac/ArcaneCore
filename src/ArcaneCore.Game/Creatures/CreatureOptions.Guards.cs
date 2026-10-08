namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>
    /// <c>Creatures:NoMeleeFleeOnAggro</c> (default false): a creature with the static flag NO_MELEE_FLEE (0x00100000) that a player or a
    /// player's pet engages runs in panic for <see cref="NoMeleeFleeMs"/> and then evades (cmangos Unit::SetInCombatWithVictim,
    /// Entities/Unit.cpp:7993-7998, and CreatureAI::TimedFleeingEnded, AI/BaseAI/CreatureAI.cpp:254-258). vmangos only takes the melee
    /// away for the same bit (CREATURE_STATIC_FLAG_NO_MELEE, whose original comment is "Flee"; AI/CreatureAI.cpp:40), and the default
    /// keeps that, since vmangos is the fidelity reference; critters still run from a hit through CritterAI either way. Melee is off for
    /// the flag either way. classic-db z2815 sets it on 71 templates (deer, sheep, cows, wisps, totems, target dummies).
    /// </summary>
    public bool NoMeleeFleeOnAggro { get; set; }

    /// <summary>How long a NO_MELEE_FLEE creature runs before it evades (cmangos <c>DoFlee(30000)</c>, Unit.cpp:7996).</summary>
    public uint NoMeleeFleeMs { get; set; } = 30000;
}
