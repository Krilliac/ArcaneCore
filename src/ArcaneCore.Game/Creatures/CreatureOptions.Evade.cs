namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>
    /// Whether an evading creature loses its auras (<c>Creatures:EvadeResetsAuras</c>): everything except a non-permanent positive aura
    /// cast by a player; with the KEEP_POSITIVE_AURAS_ON_EVADE flag only the negative ones (Creature::RemoveAurasAtReset,
    /// Objects/Creature.cpp:3611-3630). Retail is true. The evade health snap switch is <c>Creatures:Movement:EvadeRestoresFullHealth</c>.
    /// </summary>
    public bool EvadeResetsAuras { get; set; } = true;

    /// <summary>
    /// Milliseconds a chasing creature tolerates a victim its pathfinder cannot reach before it evades home
    /// (<c>Creatures:UnreachableTargetEvadeMs</c>): vmangos Creature::Update, <c>m_targetNotReachableTimer &gt; 24000</c> calls
    /// EnterEvadeMode (Objects/Creature.cpp:1037-1040). The count (vmangos m_targetNotReachableTimer, :1013-1030) runs while the chase on
    /// top of the creature's movement says its victim is unreachable, the creature is not a ranged chaser, not owned or charmed by a
    /// player, has no NO_UNREACHABLE_EVADE flag and cannot hit the victim (out of melee reach or out of sight); it restarts at 0 whenever
    /// that stops being true and when combat ends. 0 disables the evade; EventAI's EVENT_T_TARGET_NOT_REACHABLE still fires.
    /// </summary>
    public uint UnreachableTargetEvadeMs { get; set; } = 24000;

    /// <summary>
    /// Milliseconds of the same count after which the creature is in evade mode where it stands
    /// (<c>Creatures:UnreachableTargetSoftEvadeMs</c>): vmangos Creature::IsEvadeBecauseTargetNotReachable,
    /// <c>m_targetNotReachableTimer &gt; 3000</c> (Objects/Creature.h:510). Attacks on it evade (it counts for
    /// <see cref="Creature.IsInEvadeMode"/>), its AI does not update (Creature.cpp:1041) and it regenerates as if out of combat
    /// (:1057); it keeps its victim and keeps chasing. 0 disables this stage.
    /// </summary>
    public uint UnreachableTargetSoftEvadeMs { get; set; } = 3000;
}
