using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Groups;

/// <summary>
/// Tunables of <see cref="GroupRewardRange"/>. Defaults are retail (vmangos): 74 yd
/// (CONFIG_FLOAT_GROUP_XP_DISTANCE), +150 yd for world bosses, unlimited inside raid maps.
/// </summary>
public sealed record GroupRewardOptions
{
    /// <summary>vmangos CONFIG_FLOAT_GROUP_XP_DISTANCE: 2D yards from the victim within which group members share loot, XP and credit.</summary>
    public float Distance { get; init; } = 74.0f;

    /// <summary>Extra yards for a victim of rank world boss (Object.cpp:1494). 0 disables.</summary>
    public float BossDistanceBonus { get; init; } = 150.0f;

    /// <summary>A raid map has no distance limit (Object.cpp:1482-1483). False applies <see cref="Distance"/> there too.</summary>
    public bool RaidMapsUnlimited { get; init; } = true;

    /// <summary>
    /// CREATURE_STATIC_FLAG_CORPSE_RAID hook (Object.cpp:1485-1486): creatures it accepts lift the limit
    /// inside any instanceable map. Null = never; the classic-db dump sets that flag on no row.
    /// </summary>
    public Func<Creature, bool>? CorpseRaid { get; init; }
}

/// <summary>
/// "Is this player close enough to the victim to share its rewards", a port of
/// WorldObject::IsWithinLootXPDist (D:\refs\vmangos\src\game\Objects\Object.cpp:1478-1499, distance
/// test 1738-1752) and Player::IsAtGroupRewardDistance (Player.cpp:20034-20050). The test is 2D,
/// strict (<c>&lt;</c>), and widened by both bounding radii.
/// </summary>
public static class GroupRewardRange
{
    /// <summary>WorldObject::IsWithinLootXPDist: <paramref name="member"/> is within reach of <paramref name="victim"/>.</summary>
    public static bool IsWithinLootXpDist(WorldObject member, WorldObject victim, GroupRewardOptions options)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(victim);
        ArgumentNullException.ThrowIfNull(options);
        if (!IsInSameMap(member, victim))
        {
            return false;
        }

        if (victim.Map?.Template is { Instanceable: true } template)
        {
            if (template.IsRaid && options.RaidMapsUnlimited)
            {
                return true;
            }

            if (victim is Creature raidCorpse && options.CorpseRaid?.Invoke(raidCorpse) == true)
            {
                return true;
            }
        }

        float limit = options.Distance;
        if (victim is Creature { IsWorldBoss: true })
        {
            limit += options.BossDistanceBonus;
        }

        float dx = member.X - victim.X;
        float dy = member.Y - victim.Y;
        float max = limit + member.BoundingRadius + victim.BoundingRadius;
        return dx * dx + dy * dy < max * max;
    }

    /// <summary>
    /// Player::IsAtGroupRewardDistance: within reach, or - for a dead player - his corpse is. A living
    /// player far away never qualifies through a corpse.
    /// </summary>
    public static bool IsAtGroupRewardDistance(Player player, WorldObject source, GroupRewardOptions options)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (IsWithinLootXpDist(player, source, options))
        {
            return true;
        }

        return !player.IsAlive && player.Combat.Corpse is { } corpse && IsWithinLootXpDist(corpse, source, options);
    }

    /// <summary>WorldObject::IsInMap. A corpse that has not been added to a map yet is matched by map id.</summary>
    private static bool IsInSameMap(WorldObject a, WorldObject b)
        => a.MapId == b.MapId && (a.Map is null || b.Map is null || ReferenceEquals(a.Map, b.Map));
}
