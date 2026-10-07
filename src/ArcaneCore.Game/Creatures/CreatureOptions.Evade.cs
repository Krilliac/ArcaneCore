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
    /// Milliseconds a chasing creature tolerates a victim its pathfinder cannot reach before it gives the victim up
    /// (<c>Creatures:UnreachableTargetEvadeMs</c>): alone on the threat list the creature evades, otherwise the victim is dropped from
    /// the list and the next one is chosen (vmangos Creature::Update unreachable-target timer, Objects/Creature.cpp:1017-1040, and
    /// mangos Unit::SelectHostileTarget, Object/UnitThreat.cpp:342-361, which evades at once). The count pauses while the creature
    /// cannot move and restarts with every new victim. 0 disables the evade; EventAI's EVENT_T_TARGET_NOT_REACHABLE still fires. The
    /// vmangos default could not be re-read for this build (UNVERIFIED); 5000 is ArcaneCore's choice.
    /// </summary>
    public uint UnreachableTargetEvadeMs { get; set; } = 5000;
}
