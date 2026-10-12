using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// Population-scaled respawn (<c>World:Respawn:Dynamic</c>, <see cref="DynamicRespawnOptions"/>): the respawn delay a death schedules is
/// shortened by the players around the creature's home position, counted once at death. World thread.
/// </summary>
public sealed partial class CreatureMapSystem
{
    /// <summary>
    /// What <c>.spawninfo dynamic</c> reports: the live options, whether this map ever scales, and the players counted at a point.
    /// </summary>
    public readonly record struct DynamicRespawnProbe(DynamicRespawnOptions Options, bool MapScales, int Players)
    {
        /// <summary>The factor a delay is multiplied by here, before the floors.</summary>
        public double Multiplier => Options.Multiplier(Players);

        /// <summary>The delay a creature with <paramref name="baseSeconds"/> would get here (the options are applied as if enabled).</summary>
        public uint Scale(uint baseSeconds) => MapScales ? Options.ScaleDelay(baseSeconds, Players) : baseSeconds;
    }

    /// <summary>Count the players around a point exactly as a death there would, and report the options in force. For the GM command.</summary>
    public DynamicRespawnProbe ProbeDynamicRespawn(float x, float y)
    {
        DynamicRespawnOptions options = Map.World.Options.Respawn.Dynamic;
        return new DynamicRespawnProbe(options, MapAllowsPopulationScaling(Map), Map.CountPlayersWithin(x, y, options.IsUsable ? options.Radius : DynamicRespawnOptions.MaxRadius, options.CountBots));
    }

    /// <summary>Open-world maps scale; dungeons, raids, battlegrounds and every instance copy never do.</summary>
    internal static bool MapAllowsPopulationScaling(Maps.Map map)
        => map.InstanceId == 0 && map.Template is not { IsDungeon: true } and not { IsBattleground: true };

    /// <summary>
    /// The delay a death schedules once population scaling has had its say. With <c>World:Respawn:Dynamic:Enabled</c> it replaces
    /// <see cref="ScaleRespawnDelay"/> (the zone-rate scaling of <c>Creatures:Respawn:DynamicRate</c>) so the two never compound; without it
    /// that one runs exactly as before. <paramref name="scripted"/> is true when a script or a battleground chose this delay.
    /// </summary>
    private uint ScheduledRespawnDelay(Creature creature, uint delaySeconds, bool scripted)
    {
        DynamicRespawnOptions dynamic = Map.World.Options.Respawn.Dynamic;
        if (!dynamic.Active)
        {
            return ScaleRespawnDelay(creature, delaySeconds);
        }

        if (scripted || delaySeconds == Creature.RespawnNeverSeconds || delaySeconds <= dynamic.MinimumSeconds || !ScalesWithPopulation(creature, dynamic))
        {
            return delaySeconds;
        }

        // One grid query (the few cells a circle of Radius touches), at death only; never per tick.
        int players = Map.CountPlayersWithin(creature.Home.X, creature.Home.Y, dynamic.Radius, dynamic.CountBots);
        return dynamic.ScaleDelay(delaySeconds, players);
    }

    /// <summary>
    /// Whether a creature's respawn may be shortened by the crowd: a database spawn of an open-world map and an eligible rank, that no spawn
    /// group, pool, creature link or script owns the respawn of.
    /// </summary>
    private bool ScalesWithPopulation(Creature creature, DynamicRespawnOptions dynamic)
    {
        if (creature.Spawn is not { } spawn || creature.Summon is not null || !MapAllowsPopulationScaling(Map) || !dynamic.ScalesRank(creature.Template.Rank))
        {
            return false;
        }

        if (_groupOfSpawn.ContainsKey(spawn.Guid) || _poolSpawns.ContainsKey(spawn.Guid) || _scriptOnlySpawns.Contains(spawn.Guid))
        {
            return false; // a spawn group, a pool or a script decides when it comes back
        }

        if (_options.Respawn.Linked)
        {
            // creature_linking: a master, or a slave with a respawn, despawn or aggro event, is brought back by its link (LinkedRespawn.cs).
            EnsureLinkIndex();
            if (_linkMasterGuids!.Contains(spawn.Guid) || _linkMasterEntries!.Contains(creature.Entry)
                || (LinkOf(creature) is { } link && (link.Flags & LinkEventFlags) != 0))
            {
                return false;
            }
        }

        return true;
    }
}
