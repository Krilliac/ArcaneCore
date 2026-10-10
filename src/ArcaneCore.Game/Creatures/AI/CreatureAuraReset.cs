namespace ArcaneCore.Game.Creatures;

/// <summary>
/// An <see cref="ICreatureSpellCaster"/> that can also strip a creature's auras when it evades (vmangos Creature::RemoveAurasAtReset,
/// Objects/Creature.cpp:3611-3630). Optional: a caster without it leaves the auras alone.
/// </summary>
public interface ICreatureAuraReset
{
    /// <summary>
    /// Remove the auras of <paramref name="creature"/> an evade removes. With <paramref name="keepPositive"/>
    /// (KEEP_POSITIVE_AURAS_ON_EVADE) only the negative ones go; otherwise everything goes except a non-permanent positive aura cast by
    /// a player (a buff a player put on the creature survives its evade).
    /// </summary>
    void ResetAuras(Creature creature, bool keepPositive);

    /// <summary>
    /// Remove every aura of <paramref name="creature"/>, passives included (vmangos and cMaNGOS Unit::RemoveAllAuras). For a script's own
    /// evade that strips the creature bare, such as vmangos silithus.cpp npc_colossusAI::EnterEvadeMode.
    /// </summary>
    void RemoveAllAuras(Creature creature);
}
