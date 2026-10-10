using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// creature_linking respawn, despawn and death events (cmangos CreatureLinkingHolder, Entities/CreatureLinkingMgr.cpp; flags in
/// CreatureLinkingMgr.h:57-81), and TrinityCore's population-scaled respawn delay. Aggro linking stays with the instance scripts.
/// </summary>
public sealed partial class CreatureMapSystem
{
    internal const uint LinkRespawnOnEvade = 0x0004, LinkToRespawnOnEvade = 0x0008, LinkDespawnOnDeath = 0x0010, LinkSelfkillOnDeath = 0x0020,
        LinkRespawnOnDeath = 0x0040, LinkRespawnOnRespawn = 0x0080, LinkDespawnOnRespawn = 0x0100, LinkCantSpawnIfBossDead = 0x0400,
        LinkCantSpawnIfBossAlive = 0x0800, LinkDespawnOnEvade = 0x1000, LinkDespawnOnDespawn = 0x2000, LinkEvadeOnEvade = 0x4000;

    private enum LinkEvent { Evade, Die, Respawn, Despawn }

    // cmangos EVENT_MASK_ON_* (CreatureLinkingMgr.cpp:258-266) without FOLLOW, which UpdateLinkedFollowers carries.
    private static uint EventMask(LinkEvent e) => e switch
    {
        LinkEvent.Evade => LinkRespawnOnEvade | LinkDespawnOnEvade | LinkEvadeOnEvade,
        LinkEvent.Die => LinkDespawnOnDeath | LinkSelfkillOnDeath | LinkRespawnOnDeath,
        LinkEvent.Respawn => LinkRespawnOnRespawn | LinkDespawnOnRespawn,
        _ => LinkDespawnOnDespawn,
    };

    private const uint LinkEventFlags = 0x7DFC; // every flag above
    private HashSet<uint>? _linkMasterGuids, _linkMasterEntries, _linkSlaveEntries;
    private readonly HashSet<ObjectGuid> _linkEventsInUse = []; // cmangos HolderMap inUse: no recursion through the same master

    private void EnsureLinkIndex()
    {
        if (_linkMasterGuids is not null) return;
        _linkMasterGuids = [.. _content.Links.Where(l => (l.Flags & LinkEventFlags) != 0).Select(l => l.MasterGuid)];
        CreatureTemplateLink[] onMap = [.. _content.TemplateLinks.Where(l => l.MapId == Map.MapId && (l.Flags & LinkEventFlags) != 0)];
        _linkMasterEntries = [.. onMap.Select(l => l.MasterEntry)];
        _linkSlaveEntries = [.. onMap.Select(l => l.SlaveEntry)];
    }

    /// <summary>cmangos GetLinkedTriggerInformation: the spawn's own row first, then its entry's row on this map.</summary>
    private (uint Flags, uint MasterGuid, CreatureTemplateLink? Template)? LinkOf(Creature creature)
    {
        if (creature.Spawn is not { } spawn) return null;
        if (_content.FindLink(spawn.Guid) is { } link) return (link.Flags, link.MasterGuid, null);
        if (_content.FindTemplateLink(creature.Entry, Map.MapId) is { } template) return (template.Flags, 0, template);
        return null;
    }

    /// <summary>cmangos DoCreatureLinkingEvent for the evade, death, respawn and despawn events (CreatureLinkingMgr.cpp:401-510).</summary>
    private void DoLinkedEvent(Creature source, LinkEvent e)
    {
        if (!_options.Respawn.Linked || source.Spawn is not { } sourceSpawn || source.IsCharmerOrOwnerPlayerOrPlayerItself) return; // IsControlledByPlayer
        EnsureLinkIndex();
        bool isMaster = _linkMasterGuids!.Contains(sourceSpawn.Guid) || _linkMasterEntries!.Contains(source.Entry);

        if (isMaster && _linkEventsInUse.Add(source.Guid))
        {
            try
            {
                uint mask = EventMask(e);
                foreach (Creature slave in _creatures.Values.ToArray())
                {
                    if (ReferenceEquals(slave, source) || slave.Summon is { Kind: SummonKind.Pet }) continue;
                    if (LinkOf(slave) is not { } info || (info.Flags & mask) == 0) continue;
                    bool linked = info.Template is { } t
                        ? t.MasterEntry == source.Entry && InSearchRange(slave.Spawn!, sourceSpawn, t.SearchRange)
                        : info.MasterGuid == sourceSpawn.Guid;
                    if (linked) ProcessLinkedSlave(e, info.Flags & mask, slave);
                }
            }
            finally
            {
                _linkEventsInUse.Remove(source.Guid);
            }
        }

        // Master case: FLAG_TO_RESPAWN_ON_EVADE brings back the slave's dead master (CreatureLinkingMgr.cpp:451-509).
        if (e == LinkEvent.Evade && LinkOf(source) is { } own && (own.Flags & LinkToRespawnOnEvade) != 0
            && FindLinkedMaster(source, own) is { IsAlive: false } master)
        {
            ForceRespawn(master);
        }
    }

    /// <summary>cmangos CreatureLinkingHolder::ProcessSlave (CreatureLinkingMgr.cpp:555-612).</summary>
    private void ProcessLinkedSlave(LinkEvent e, uint flags, Creature slave)
    {
        switch (e)
        {
            case LinkEvent.Evade:
                if ((flags & LinkDespawnOnEvade) != 0 && slave.IsAlive) ForcedDespawn(slave, 0);
                if ((flags & LinkRespawnOnEvade) != 0 && !slave.IsAlive) ForceRespawn(slave);
                if ((flags & LinkEvadeOnEvade) != 0 && slave.IsAlive) EnterEvadeMode(slave);
                break;
            case LinkEvent.Die:
                if ((flags & LinkSelfkillOnDeath) != 0 && slave.IsAlive) KillCreature(slave);
                if ((flags & LinkDespawnOnDeath) != 0 && slave.IsAlive) ForcedDespawn(slave, 0);
                if ((flags & LinkRespawnOnDeath) != 0 && !slave.IsAlive) ForceRespawn(slave);
                break;
            case LinkEvent.Respawn:
                if ((flags & LinkRespawnOnRespawn) != 0)
                {
                    // :595-597, against endless loops: only a slave still waiting on its own timer (or without one) comes back.
                    if (!slave.IsAlive && (slave.RespawnDelaySeconds == 0 || slave.RespawnAtMs > _clockMs)) ForceRespawn(slave);
                }
                else if ((flags & LinkDespawnOnRespawn) != 0 && slave.IsAlive)
                {
                    ForcedDespawn(slave, 0);
                }

                break;
            case LinkEvent.Despawn:
                if ((flags & LinkDespawnOnDespawn) != 0 && slave.DeathState != CreatureDeathState.Dead) ForcedDespawn(slave, 0); // !IsDespawned()
                break;
        }
    }

    private Creature? FindLinkedMaster(Creature slave, (uint Flags, uint MasterGuid, CreatureTemplateLink? Template) info)
    {
        if (info.Template is not { } t)
            return _creatures.Values.FirstOrDefault(c => c.Spawn?.Guid == info.MasterGuid);
        Creature[] candidates = [.. _creatures.Values.Where(c => c.Spawn is not null && c.Entry == t.MasterEntry
            && InSearchRange(slave.Spawn!, c.Spawn, t.SearchRange))];
        return t.SearchRange <= 0 ? candidates.Length == 1 ? candidates[0] : null : candidates.OrderBy(c => DistanceSq(slave.Spawn!, c.Spawn!)).FirstOrDefault();
    }

    /// <summary>
    /// cmangos CreatureLinkingHolder::CanSpawn (CreatureLinkingMgr.cpp:678-751): FLAG_CANT_SPAWN_IF_BOSS_DEAD holds a slave back while its
    /// master is dead or waiting to respawn (and, map-wide, while the instance has an encounter in progress); FLAG_CANT_SPAWN_IF_BOSS_ALIVE
    /// the opposite. A ranged entry link with no master nearby spawns.
    /// </summary>
    internal bool LinkAllowsSpawn(Creature creature)
    {
        if (!_options.Respawn.Linked || LinkOf(creature) is not { } info
            || (info.Flags & (LinkCantSpawnIfBossDead | LinkCantSpawnIfBossAlive)) == 0) return true;
        bool bossDead = (info.Flags & LinkCantSpawnIfBossDead) != 0;

        if (info.Template is { SearchRange: > 0 })
        {
            if (FindLinkedMaster(creature, info) is not { } near) return true;
            return bossDead ? near.IsAlive : !near.IsAlive;
        }

        uint masterGuid = info.MasterGuid;
        if (info.Template is { } t)
        {
            IReadOnlyList<CreatureSpawn> masters = _content.GetSpawns(Map.MapId, t.MasterEntry);
            if (masters.Count != 1) return true; // cmangos LoadFromDB rejects a non-unique map-wide master (:233-250)
            masterGuid = masters[0].Guid;
        }

        if (bossDead && Map.FindUpdater<Instances.Scripts.InstanceData>() is { IsEncounterInProgress: true }) return false;
        bool ready = MasterRespawnReady(masterGuid);
        return bossDead ? ready : !ready;
    }

    /// <summary>cmangos IsRespawnReady (:663-675): no pending respawn time for the master spawn.</summary>
    private bool MasterRespawnReady(uint masterGuid)
    {
        if (_respawnAt.TryGetValue(masterGuid, out long at)) return at <= _clockMs;
        Creature? master = _creatures.Values.FirstOrDefault(c => c.Spawn?.Guid == masterGuid);
        return master is null || master.IsAlive || master.RespawnAtMs <= _clockMs;
    }

    /// <summary>
    /// TrinityCore Map::ApplyDynamicModeRespawnScaling (Maps/Map.cpp:3312-3354), off unless <c>Creatures:Respawn:DynamicRate</c> is set:
    /// delay * rate / players-in-zone when that factor is below 1, floored at the minimum; a delay already at or under it is left alone.
    /// </summary>
    internal uint ScaleRespawnDelay(Creature creature, uint delaySeconds)
    {
        float rate = _options.Respawn.DynamicRate;
        uint minimum = _options.Respawn.DynamicMinimumSeconds;
        if (rate <= 0 || creature.Spawn is null || delaySeconds == Creature.RespawnNeverSeconds || delaySeconds <= minimum) return delaySeconds;
        if (Map.Template is { } map && (map.IsDungeon || map.IsBattleground)) return delaySeconds;
        if (creature.Template.Rank is 2 or 3 or 4) return delaySeconds; // rares and world bosses keep their timers

        uint zone = ZoneAndAreaOf(creature).ZoneId;
        int players = Map.Players.Count(p => p.ZoneId == zone);
        if (players == 0) return delaySeconds;
        double factor = rate / players;
        if (factor >= 1.0) return delaySeconds;
        return Math.Max((uint)Math.Ceiling(delaySeconds * factor), minimum);
    }
}
