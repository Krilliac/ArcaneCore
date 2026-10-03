using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells.Rules;

/// <summary>Unit facts the spell rules share (levels, creature types, "player-like").</summary>
public static class RuleUnitExtensions
{
    private const uint HumanoidCreatureType = 7;

    /// <summary>
    /// vmangos Creature::IsWorldBoss (Creature.h:202-208). Only creatures that report
    /// <see cref="ICombatCreature.IsWorldBoss"/> (rank WORLDBOSS) qualify.
    /// </summary>
    public static bool IsWorldBoss(this Unit unit) => unit is ICombatCreature { IsWorldBoss: true };

    /// <summary>
    /// vmangos SpellCaster::GetLevelForTarget (SpellCaster.cpp:70-114): a world boss counts as
    /// <paramref name="target"/>'s level plus <paramref name="worldBossLevelDiff"/> (clamped 1..255),
    /// any other unit as its own level.
    /// </summary>
    public static int EffectiveLevelAgainst(this Unit unit, Unit? target, int worldBossLevelDiff)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!unit.IsWorldBoss() || target is null)
        {
            return unit.Level;
        }

        return Math.Clamp(target.Level + worldBossLevelDiff, 1, 255);
    }

    /// <summary>
    /// "Player-like" in the diminishing-returns and PvP sense (vmangos Unit::IsCharmerOrOwnerPlayerOrPlayerItself):
    /// players only until the pets area provides an owner link (recorded limit in docs/areas/spell-rules.md).
    /// </summary>
    public static bool IsLikePlayer(this Unit unit) => unit is Player;

    /// <summary>
    /// vmangos Unit::GetCreatureTypeMask: the CreatureType.dbc id (1 beast to 11 totem) as bit
    /// <c>1 &lt;&lt; (type - 1)</c>; players count as humanoid (7), units with no type as 0.
    /// </summary>
    public static uint CreatureTypeMask(this Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        uint type = unit switch
        {
            Player => HumanoidCreatureType,
            Creatures.Creature creature => creature.Template.CreatureType,
            _ => 0,
        };
        return type is >= 1 and <= 32 ? 1u << (int)(type - 1) : 0u;
    }
}
