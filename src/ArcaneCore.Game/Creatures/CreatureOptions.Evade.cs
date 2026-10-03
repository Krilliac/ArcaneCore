namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>
    /// Development switch (<c>Creatures:EvadeRestoresFullHealth</c>): an evading creature snaps to full health and mana at once. Retail
    /// (false) does not touch them: CreatureAI::EnterEvadeMode only drops the threat list and the fight (AI/CreatureAI.cpp:323-346) and
    /// the creature regenerates a third of its maximum every 5 seconds once out of combat (Creature::RegenerateAll, Objects/Creature.cpp:1087-1161).
    /// </summary>
    public bool EvadeRestoresFullHealth { get; set; }

    /// <summary>
    /// Whether an evading creature loses its auras (<c>Creatures:EvadeResetsAuras</c>): everything except a non-permanent positive aura
    /// cast by a player; with the KEEP_POSITIVE_AURAS_ON_EVADE flag only the negative ones (Creature::RemoveAurasAtReset,
    /// Objects/Creature.cpp:3611-3630). Retail is true.
    /// </summary>
    public bool EvadeResetsAuras { get; set; } = true;
}
