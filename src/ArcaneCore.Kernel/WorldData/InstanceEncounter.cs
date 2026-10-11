namespace ArcaneCore.Kernel.WorldData;

/// <summary>
/// One encounter of an instance, as the 1.12.1 client's DungeonEncounter.dbc lists it joined to the server's <c>instance_encounters</c>
/// (cmangos classic-db z2815: <c>instance_dungeon_encounters</c> joined on <c>entry = Id</c>; <c>Difficulty = 0</c> only).
/// </summary>
/// <param name="MapId">The instance map.</param>
/// <param name="EncounterIndex">The encounter's position in the dungeon's order (DungeonEncounter.dbc OrderIndex; ascending is the intended kill order).</param>
/// <param name="EncounterId">DungeonEncounter.dbc id (<c>instance_encounters.entry</c>).</param>
/// <param name="CreditType">0: killing the creature <paramref name="CreditEntry"/> completes it; 1: a spell cast on a player (scripted, e.g. the Four Horsemen); other values are not creature kills.</param>
/// <param name="CreditEntry">The creature entry (credit type 0) or the spell id (credit type 1).</param>
/// <param name="LastEncounter">The encounter is the last of one dungeon (LFG) segment of the map (<c>lastEncounterDungeon</c> not 0).</param>
/// <param name="Name">The encounter's English name.</param>
public sealed record InstanceEncounter(
    uint MapId,
    uint EncounterIndex,
    uint EncounterId,
    byte CreditType,
    uint CreditEntry,
    bool LastEncounter,
    string Name)
{
    /// <summary>Credit type 0: the encounter is completed by the death of a creature.</summary>
    public const byte CreditCreatureKill = 0;

    /// <summary>Whether killing <see cref="CreditEntry"/> completes the encounter.</summary>
    public bool IsCreatureKill => CreditType == CreditCreatureKill;
}
