namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureOptions
{
    /// <summary>
    /// Whether an evading creature loses its auras (<c>Creatures:EvadeResetsAuras</c>): everything except a non-permanent positive aura
    /// cast by a player; with the KEEP_POSITIVE_AURAS_ON_EVADE flag only the negative ones (Creature::RemoveAurasAtReset,
    /// Objects/Creature.cpp:3611-3630). Retail is true. The evade health snap switch is <c>Creatures:Movement:EvadeRestoresFullHealth</c>.
    /// </summary>
    public bool EvadeResetsAuras { get; set; } = true;
}
