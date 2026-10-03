namespace ArcaneCore.Kernel.Npc;

/// <summary>
/// One row of the <c>conditions</c> table in the cmangos/classic-db shape:
/// <c>condition_entry, type, value1, value2, value3, value4, flags</c>
/// (classic-db Full_DB <c>conditions</c> CREATE TABLE; cmangos mangos-classic
/// src/game/Globals/Conditions.h:30-82 for the numbering of <see cref="Type"/>).
/// <see cref="Type"/> is signed: -3 NOT, -2 OR, -1 AND.
/// </summary>
public sealed record ConditionRecord(
    uint Entry,
    int Type,
    uint Value1,
    uint Value2,
    uint Value3,
    uint Value4,
    byte Flags);

/// <summary>
/// Reads the conditions table at startup (off the world thread). No implementation ships yet: the
/// content importer for the quest/NPC tables (slice NQ0) is the future implementer; until one is
/// registered the world runs with an empty <c>ConditionTable</c>.
/// </summary>
public interface IConditionContentStore
{
    Task<IReadOnlyList<ConditionRecord>> LoadAsync(CancellationToken cancellationToken = default);
}
