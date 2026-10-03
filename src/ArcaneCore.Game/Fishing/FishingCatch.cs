using ArcaneCore.Kernel.WorldData.Loot;

namespace ArcaneCore.Game.Fishing;

/// <summary>
/// The pure rules of a bobber click (vmangos GameObject::Use for GAMEOBJECT_TYPE_FISHINGNODE, GameObject.cpp:1635-1731, and
/// GameObject::getFishLoot, :878-887).
/// </summary>
public static class FishingCatch
{
    /// <summary>The hidden Wetlands lake: sub-zone 11 within 100 yd (2D) of this point gives nothing (GameObject.cpp:880).</summary>
    public const uint HiddenLakeArea = 11;

    public const float HiddenLakeX = -4074.74f;

    public const float HiddenLakeY = -1315.79f;

    public const float HiddenLakeRadius = 100.0f;

    /// <summary>
    /// The catch chance in percent before the roll: <c>skill - zoneSkill + 5</c> (the comment of the source: skill must be at least the
    /// zone's base skill; at equality the chance is 5%, and it grows one point per skill point). Signed zone skills (-70, -20) are kept.
    /// </summary>
    public static int Chance(int skill, int zoneSkill) => skill - zoneSkill + 5;

    /// <summary>vmangos: <c>skill &gt;= zone_skill &amp;&amp; chance &gt;= roll</c> with <c>roll = irand(1, 100)</c>.</summary>
    public static bool Succeeds(int skill, int zoneSkill, int roll) => skill >= zoneSkill && Chance(skill, zoneSkill) >= roll;

    /// <summary>
    /// The base skill of the bobber's sub-zone, else of its zone; 0 means no row (vmangos GetFishingBaseSkillLevel returns 0 for
    /// an unknown area and the code treats exactly 0 as missing, GameObject.cpp:1654-1658).
    /// </summary>
    public static int ZoneSkill(LootContent content, uint zoneId, uint areaId)
    {
        ArgumentNullException.ThrowIfNull(content);
        int skill = content.FishingBaseSkill(areaId);
        return skill != 0 ? skill : content.FishingBaseSkill(zoneId);
    }

    /// <summary>
    /// The fishing_loot_template entry a catch rolls: the sub-zone's table when it EXISTS, else the zone's. An existing table that
    /// rolls nothing does not fall back (<c>FillLoot</c> only fails for a missing template, LootMgr.cpp:491-498). Null: nothing at all.
    /// </summary>
    public static uint? LootEntry(LootContent content, uint zoneId, uint areaId)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.HasEntry(LootTableKind.Fishing, areaId))
        {
            return areaId;
        }

        return areaId != zoneId && content.HasEntry(LootTableKind.Fishing, zoneId) ? zoneId : null;
    }

    /// <summary>The player's (not the bobber's) position decides the hidden lake: <c>loot_owner-&gt;IsWithinDist2d</c>.</summary>
    public static bool IsHiddenLake(uint areaId, float playerX, float playerY)
    {
        if (areaId != HiddenLakeArea)
        {
            return false;
        }

        float dx = playerX - HiddenLakeX;
        float dy = playerY - HiddenLakeY;
        return (dx * dx) + (dy * dy) <= HiddenLakeRadius * HiddenLakeRadius;
    }
}