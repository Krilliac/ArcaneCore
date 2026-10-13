using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.GameObjects;

namespace ArcaneCore.Game.GameObjects;

/// <summary>
/// Population-scaled respawn of gathering nodes and quest objects (<c>World:Respawn:Dynamic</c>, <see cref="DynamicRespawnOptions"/>): the
/// delay a despawn schedules, and the delay of a pool member that replaces it, is shortened by the players around the spawn, counted once
/// when the delay is scheduled. World thread.
/// </summary>
public sealed partial class GameObjectMapSystem
{
    /// <summary>
    /// <paramref name="delayMs"/> after population scaling: the same rule as a creature's (<see cref="DynamicRespawnOptions.ScaleDelay"/>) on
    /// the whole seconds of the delay, counting the players around <paramref name="spawn"/>. Unchanged when scaling is off, for an object that is
    /// neither a gathering node nor a quest object, and for a spawn-group member or a spawn on an instance or battleground map. Used both when
    /// an object despawns and when a pool brings a member in (<c>IPoolHost.SpawnMember</c>), so whichever member a pool picks is scaled.
    /// Pools of creatures are deliberately not scaled (they hold rare spots); pools of game objects are, since rotating nodes are the contention.
    /// </summary>
    private long ScaleRespawnDelayMs(GameObjectSpawn spawn, long delayMs)
    {
        DynamicRespawnOptions dynamic = Map.World.Options.Respawn.Dynamic;
        if (!dynamic.Active || !dynamic.IncludeGameObjects || !CreatureMapSystem.MapAllowsPopulationScaling(Map))
        {
            return delayMs;
        }

        uint baseSeconds = (uint)Math.Min((delayMs + 999) / 1000, uint.MaxValue - 1);
        if (baseSeconds <= dynamic.MinimumSeconds || _groupOfSpawn.ContainsKey(spawn.Guid)
            || _content.FindTemplate(_spawnEntries.TryGetValue(spawn.Guid, out uint entry) ? entry : spawn.Entry) is not { } template
            || !IsGatheringOrQuestObject(template))
        {
            return delayMs;
        }

        // One grid query (the few cells a circle of Radius touches), when the delay is scheduled; never per tick.
        int players = Map.CountPlayersWithin(spawn.X, spawn.Y, dynamic.Radius, dynamic.CountBots);
        uint scaled = dynamic.ScaleDelay(baseSeconds, players);
        return scaled == baseSeconds ? delayMs : scaled * 1000L;
    }

    /// <summary>
    /// A herb or mineral node (a chest whose lock has a herbalism or mining skill case), or a quest object (see <see cref="IsQuestRelated"/>).
    /// </summary>
    private bool IsGatheringOrQuestObject(GameObjectTemplate template)
    {
        if (IsQuestRelated(template))
        {
            return true;
        }

        if ((GameObjectType)template.Type != GameObjectType.Chest || _content.FindLock(GameObjectLocks.LockIdOf(template)) is not { } entry)
        {
            return false;
        }

        int cases = Math.Min(entry.Types.Count, entry.Indexes.Count);
        for (int i = 0; i < cases; i++)
        {
            if (entry.Types[i] == (uint)LockKeyType.Skill && entry.Indexes[i] is (uint)LockType.Herbalism or (uint)LockType.Mining)
            {
                return true;
            }
        }

        return false;
    }
}
