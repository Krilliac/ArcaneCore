using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Fishing;

/// <summary>Fishing holes (gameobject type 25: data0 radius, data1 loot id, data2/data3 min/max successful opens).</summary>
public static class FishingHoles
{
    /// <summary>vmangos CONTACT_DISTANCE (ObjectDefines.h:23).</summary>
    public const float ContactDistance = 0.5f;

    /// <summary>The search range around a bobber: <c>20.0f + CONTACT_DISTANCE</c> (GameObject.cpp:1672, 1683).</summary>
    public const float SearchRange = 20.0f + ContactDistance;

    /// <summary>
    /// vmangos GameObject::LookupFishingHoleAround + NearestGameObjectFishingHoleCheck (GridNotifiers.h:612-640): a spawned hole within
    /// <paramref name="range"/> AND within its own radius (data0) of the bobber, distances minus both bounding radii. vmangos takes the
    /// first one the grid visit finds; here the nearest, the only deterministic choice.
    /// </summary>
    public static GameObject? FindAround(GameObjectMapSystem objects, GameObject bobber, float range = SearchRange)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(bobber);
        GameObject? best = null;
        float bestDistance = float.MaxValue;
        foreach (GameObject candidate in objects.GameObjects)
        {
            if (candidate.Type != GameObjectType.FishingHole || !candidate.IsSpawned)
            {
                continue;
            }

            float distance = bobber.DistanceTo(candidate);
            if (distance <= range && distance <= candidate.Template.GetData(0) && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// vmangos DoLootRelease for a hole, after its loot was taken completely (LootHandler.cpp:489-495): one more use; once the uses reach a
    /// random value between data2 and data3 (inclusive) the hole is used up (it despawns and respawns later), else it is ready again.
    /// </summary>
    public static GameObjectLootState AfterUse(GameObject hole, Random random)
    {
        ArgumentNullException.ThrowIfNull(hole);
        ArgumentNullException.ThrowIfNull(random);
        hole.UseCount++;
        uint min = hole.Template.GetData(2);
        uint max = Math.Max(min, hole.Template.GetData(3));
        long limit = random.NextInt64(min, (long)max + 1);
        return hole.UseCount >= limit ? GameObjectLootState.JustDeactivated : GameObjectLootState.Ready;
    }
}