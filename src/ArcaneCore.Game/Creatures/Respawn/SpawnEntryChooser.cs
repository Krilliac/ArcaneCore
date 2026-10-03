using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Picks the entry of a spawn that has several (cmangos ObjectMgr::GetRandomCreatureEntry over <c>creature_spawn_entry</c>; vmangos
/// CreatureData::ChooseCreatureId over <c>id</c> ... <c>id5</c>): uniformly among the entries that have a creature template (cmangos skips a
/// row whose entry has none when it loads the table, ObjectMgr.cpp:1853-1858). Group entry limits (vmangos creature_groups_entry_limit)
/// are not applied: creature groups are not implemented.
/// </summary>
internal static class SpawnEntryChooser
{
    /// <summary>The template of one randomly chosen entry, or null when none of <paramref name="entries"/> has a template.</summary>
    public static CreatureTemplate? Choose(CreatureContent content, IReadOnlyList<uint> entries, Random random)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(random);

        var usable = new List<CreatureTemplate>(entries.Count);
        foreach (uint entry in entries)
        {
            if (content.FindTemplate(entry) is { } template)
            {
                usable.Add(template);
            }
        }

        return usable.Count switch
        {
            0 => null,
            1 => usable[0],
            _ => usable[random.Next(usable.Count)],
        };
    }
}
