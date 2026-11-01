using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The spell_script_target rows the Lunar Festival rocket and cluster spells need for target 46 (TARGET_LOCATION_SCRIPT_NEAR_CASTER) to resolve in the
/// sweep: type 1 (creature), the firework creature that casts the spell. Derived from <see cref="FireworkCatalog"/>, which mirrors vmangos
/// npcs_special.cpp:1008-1043 and the vmangos rows of migration 20171007150644_world.sql (26344-26356, 26300-26362, 26483-26515, the Lucky Rocket
/// Cluster's extra rows). Spell 30163 (Copy of Rocket PURPLE) takes the Small Purple Rocket creature. The launcher spells need object rows
/// (type 0, 180868) that vmangos adds with conditions; they are not loaded (docs/areas/spells.md).
/// </summary>
internal static class SweepFireworkRows
{
    public static readonly SpellStore.ScriptTarget[] Rows =
    [
        .. FireworkCatalog.Fireworks
            .SelectMany(kind => kind.Spells.Select(spell => new SpellStore.ScriptTarget(spell, 1, kind.NpcEntry, 0))),
        new SpellStore.ScriptTarget(30163, 1, 15881, 0),
    ];
}
