using ArcaneCore.Game.Holidays;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The spell_script_target rows the Lunar Festival rocket, cluster and launcher spells need for target 46 (TARGET_LOCATION_SCRIPT_NEAR_CASTER) to
/// resolve in the sweep. Type 1 (creature) rows are the firework creature that casts the rocket or cluster spell, derived from
/// <see cref="FireworkCatalog"/>, which mirrors vmangos npcs_special.cpp:1008-1043 and the rows of vmangos
/// sql/migrations/20210727201033_world.sql:172-184 (26344-26356, 26300-26362, 26483-26515, the Lucky Rocket Cluster's extra rows). Spell 30163
/// (Copy of Rocket PURPLE) takes the Small Purple Rocket creature. The type 0 (game object) rows are the unconditioned launcher rows of the same
/// migration (conditionId 0, :186-260): the rocket spells against firework launchers 180771, 180850 and 180868, the cluster spells against
/// cluster launchers 180772, 180859, 180869 and 180874.
/// </summary>
internal static class SweepFireworkRows
{
    private static readonly uint[] RocketLaunchers = [180771, 180850, 180868];

    private static readonly uint[] RocketLauncherSpells = [26333, 26334, 26335, 26336, 26337, 26338, 26291, 26292, 26293, 26286, 26294, 26295];

    private static readonly uint[] ClusterLaunchers = [180772, 180859, 180869, 180874];

    private static readonly uint[] ClusterLauncherSpells = [26304, 26325, 26488, 26490, 26516, 26517, 26518, 26519, 26521, 26326, 26327, 26328, 26329];

    public static readonly SpellStore.ScriptTarget[] Rows =
    [
        .. FireworkCatalog.Fireworks
            .SelectMany(kind => kind.Spells.Select(spell => new SpellStore.ScriptTarget(spell, 1, kind.NpcEntry, 0))),
        new SpellStore.ScriptTarget(30163, 1, 15881, 0),
        .. RocketLaunchers.SelectMany(go => RocketLauncherSpells.Select(spell => new SpellStore.ScriptTarget(spell, 0, go, 0))),
        .. ClusterLaunchers.SelectMany(go => ClusterLauncherSpells.Select(spell => new SpellStore.ScriptTarget(spell, 0, go, 0))),
    ];
}
