using System.Diagnostics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    private const uint LinkFollow = 0x200;
    private readonly Dictionary<ObjectGuid, ObjectGuid> _linkedFollowerMaster = [];

    /// <summary>
    /// Spawn guid to the creature standing for it: the first one added when several share a guid (the old per-tick GroupBy took the first in
    /// <c>_creatures</c> order, which for a dictionary with removals was only roughly insertion order). Kept in step with <c>_creatures</c> by
    /// <see cref="IndexCreature"/> and <see cref="UnindexCreature"/>; world thread only.
    /// </summary>
    private readonly Dictionary<uint, Creature> _bySpawnGuid = [];

    /// <summary>The later creatures that share a spawn guid with the one in <see cref="_bySpawnGuid"/>, in the order added. Normally empty.</summary>
    private readonly Dictionary<uint, List<Creature>> _spawnGuidDuplicates = [];

    /// <summary>The creatures in <see cref="_bySpawnGuid"/> grouped by their current <see cref="Creature.Entry"/>.</summary>
    private readonly Dictionary<uint, List<Creature>> _byEntry = [];

    /// <summary>The entry under which a creature in <see cref="_bySpawnGuid"/> is currently bucketed in <see cref="_byEntry"/>.</summary>
    private readonly Dictionary<Creature, uint> _indexedEntry = [];

    /// <summary>The creatures whose link (own or by entry) carries FLAG_FOLLOW; stays empty unless the content has a follow link at all.</summary>
    private readonly HashSet<Creature> _followSlaves = [];

    /// <summary>
    /// <see cref="FindTemplateMaster"/>'s answer per template-linked slave, null included, paired with the <c>MasterEntry</c> it searched for.
    /// The answer for a given <c>MasterEntry</c> depends only on which creatures in <see cref="_bySpawnGuid"/> currently have that entry, so only
    /// changes to that entry in <see cref="_byEntry"/> invalidate it. A slave whose master is dead or not loaded rescanned every spawn of the map
    /// each tick before (6% of map work on live).
    /// </summary>
    private readonly TemplateMasterCache _templateMasterCache = new();
    private readonly List<Creature> _templateCacheInvalidateScratch = [];

    private sealed class TemplateMasterCache : Dictionary<Creature, (uint MasterEntry, Creature? Master)>
    {
        public bool NeedsEntrySync { get; set; }

        public new void Clear()
        {
            base.Clear();
            NeedsEntrySync = true;
        }
    }

    /// <summary>The creature_linking flags of a creature: its spawn's own link first, then its entry's on this map (CreatureLinkingMgr.cpp:310-326).</summary>
    private uint LinkFlags(Creature creature, out CreatureLink? link, out CreatureTemplateLink? template)
    {
        link = null;
        template = null;
        if (creature.Spawn is not { } spawn) return 0;
        link = _content.FindLink(spawn.Guid);
        template = link is null ? _content.FindTemplateLink(creature.Entry, Map.MapId) : null;
        return link?.Flags ?? template?.Flags ?? 0;
    }

    internal bool IsFollowSlave(Creature creature) => _followSlaves.Contains(creature);

    internal bool HasSpawnIndex(uint spawnGuid) => _bySpawnGuid.ContainsKey(spawnGuid);

    /// <summary>
    /// The creature of this map spawned from database row <paramref name="spawnGuid"/> (the first one added when a duplicate exists), or
    /// null: the spawn-guid index, not a scan of every creature of the map. <c>Creatures.FirstOrDefault(c =&gt; c.Spawn?.Guid == guid)</c>
    /// ran every world tick for the AQ war-effort researchers of Kalimdor (4% of the live world thread, Oct 17). World thread only.
    /// </summary>
    public Creature? FindBySpawnGuid(uint spawnGuid) => _bySpawnGuid.GetValueOrDefault(spawnGuid);

    internal int SpawnDuplicateCount => _spawnGuidDuplicates.Count;

    private void AddByEntry(Creature creature)
    {
        uint entry = creature.Entry;
        _indexedEntry[creature] = entry;
        if (!_byEntry.TryGetValue(entry, out List<Creature>? list))
        {
            _byEntry[entry] = list = [];
        }

        list.Add(creature);
        InvalidateTemplateMasterCacheForMasterEntry(entry);
    }

    private void RemoveByEntry(Creature creature)
    {
        if (!_indexedEntry.Remove(creature, out uint entry))
        {
            return;
        }

        if (_byEntry.TryGetValue(entry, out List<Creature>? list))
        {
            list.Remove(creature);
            if (list.Count == 0)
            {
                _byEntry.Remove(entry);
            }
        }

        InvalidateTemplateMasterCacheForMasterEntry(entry);
    }

    private void SyncByEntry(Creature creature)
    {
        if (!_indexedEntry.TryGetValue(creature, out uint oldEntry))
        {
            return;
        }

        uint newEntry = creature.Entry;
        if (oldEntry == newEntry)
        {
            return;
        }

        if (_byEntry.TryGetValue(oldEntry, out List<Creature>? oldList))
        {
            oldList.Remove(creature);
            if (oldList.Count == 0)
            {
                _byEntry.Remove(oldEntry);
            }
        }

        _indexedEntry[creature] = newEntry;
        if (!_byEntry.TryGetValue(newEntry, out List<Creature>? newList))
        {
            _byEntry[newEntry] = newList = [];
        }

        newList.Add(creature);
        InvalidateTemplateMasterCacheForMasterEntry(oldEntry);
        InvalidateTemplateMasterCacheForMasterEntry(newEntry);
    }

    private void InvalidateTemplateMasterCacheForMasterEntry(uint masterEntry)
    {
        if (_templateMasterCache.Count == 0)
        {
            return;
        }

        _templateCacheInvalidateScratch.Clear();
        foreach ((Creature slave, (uint cachedMasterEntry, _)) in _templateMasterCache)
        {
            if (cachedMasterEntry == masterEntry)
            {
                _templateCacheInvalidateScratch.Add(slave);
            }
        }

        foreach (Creature slave in _templateCacheInvalidateScratch)
        {
            _templateMasterCache.Remove(slave);
        }

        _templateCacheInvalidateScratch.Clear();
    }

    /// <summary>Record a creature that has just entered <c>_creatures</c> in the spawn-guid index and, when it follows, the follower set.</summary>
    internal void IndexCreature(Creature creature)
    {
        if (creature.Spawn is { } spawn)
        {
            if (_bySpawnGuid.TryAdd(spawn.Guid, creature))
            {
                AddByEntry(creature);
            }
            else if (!ReferenceEquals(_bySpawnGuid[spawn.Guid], creature))
            {
                if (!_spawnGuidDuplicates.TryGetValue(spawn.Guid, out List<Creature>? later))
                    _spawnGuidDuplicates[spawn.Guid] = later = [];
                if (!later.Contains(creature))
                {
                    later.Add(creature);
                    Creature existing = _bySpawnGuid[spawn.Guid];
                    CreatureAnomalies.ReportDuplicateRespawn(
                        creature,
                        existing,
                        $"spawn {spawn.Guid} already has live/world creature {existing.Guid} (entry {existing.Entry})");
                }
            }
        }

        RefreshFollowSlave(creature);
    }

    /// <summary>Drop a creature that is leaving <c>_creatures</c> from the indexes; a later creature of the same spawn guid takes its place.</summary>
    internal void UnindexCreature(Creature creature)
    {
        _templateMasterCache.Remove(creature);
        _followSlaves.Remove(creature);
        _linkedFollowerMaster.Remove(creature.Guid);
        if (creature.Spawn is not { } spawn) return;
        if (_bySpawnGuid.TryGetValue(spawn.Guid, out Creature? indexed) && ReferenceEquals(indexed, creature))
        {
            RemoveByEntry(creature);
            if (_spawnGuidDuplicates.TryGetValue(spawn.Guid, out List<Creature>? later))
            {
                Creature promoted = later[0];
                _bySpawnGuid[spawn.Guid] = promoted;
                later.RemoveAt(0);
                if (later.Count == 0) _spawnGuidDuplicates.Remove(spawn.Guid);
                AddByEntry(promoted);
            }
            else
            {
                _bySpawnGuid.Remove(spawn.Guid);
            }
        }
        else if (_spawnGuidDuplicates.TryGetValue(spawn.Guid, out List<Creature>? others) && others.Remove(creature) && others.Count == 0)
        {
            _spawnGuidDuplicates.Remove(spawn.Guid);
        }
    }

    /// <summary>
    /// Re-evaluate whether a creature in this map follows: the flags depend on its spawn guid (fixed) and its entry, which
    /// Creature.InitializeFields rewrites on a respawn into another entry and on UpdateEntry; both call this.
    /// </summary>
    internal void RefreshFollowSlave(Creature creature)
    {
        _templateMasterCache.Remove(creature);
        SyncByEntry(creature);
        if (!_content.HasFollowLinks || !_creatures.TryGetValue(creature.Guid, out Creature? member) || !ReferenceEquals(member, creature)) return;
        if ((LinkFlags(creature, out _, out _) & LinkFollow) != 0) _followSlaves.Add(creature);
        else _followSlaves.Remove(creature);
    }

    /// <summary>
    /// creature_linking FLAG_FOLLOW (0x200): vmangos CreatureLinkingHolder::TryFollowMaster and SetFollowing (CreatureLinkingMgr.cpp:711-794)
    /// make a living, out-of-combat slave follow its master at the distance and angle of their spawn points; vmangos does it when the master
    /// respawns and when the slave goes home after an evade (MotionMaster::MoveTargetedHome). ArcaneCore checks on each map tick instead:
    /// a slave without a follow generator re-follows, and one whose master is gone or dead is released.
    /// Allocation-free: it walks only the follow slaves and finds masters through the spawn-guid and entry indexes.
    /// </summary>
    internal void UpdateLinkedFollowers()
    {
        if (_followSlaves.Count == 0) return;
        foreach (Creature slave in _followSlaves)
        {
            if (!ArcaneCore.Kernel.Diagnostics.Invariant.Check(slave.Spawn is not null, $"Follow slave creature {slave.Guid} (Entry {slave.Entry}) has null Spawn.")
                || slave.Spawn is not { } spawn)
            {
                continue;
            }
            // vmangos GetLinkedTriggerInformation (CreatureLinkingMgr.cpp:310-326): the spawn's own link first, then its entry's on this map.
            if ((LinkFlags(slave, out CreatureLink? link, out CreatureTemplateLink? template) & LinkFollow) == 0) continue; // entry rewritten behind RefreshFollowSlave's back

            Creature? master = null;
            if (link is not null)
            {
                _bySpawnGuid.TryGetValue(link.MasterGuid, out master);
            }
            else if (_linkedFollowerMaster.TryGetValue(slave.Guid, out ObjectGuid current)
                && _creatures.TryGetValue(current, out Creature? followed) && followed.IsAlive)
            {
                master = followed; // keep the master already found while it lives
            }
            else
            {
                if (!_templateMasterCache.TryGetValue(slave, out (uint MasterEntry, Creature? Master) cached)
                    || cached.MasterEntry != template!.MasterEntry
                    || (cached.Master is { } prev && prev.Entry != template.MasterEntry))
                {
                    master = FindTemplateMaster(slave, spawn, template!);
                    _templateMasterCache[slave] = (template!.MasterEntry, master);
                }
                else
                {
                    master = cached.Master;
                }
            }

            if (master is null || !master.IsAlive || !slave.IsAlive)
            {
                if (_linkedFollowerMaster.Remove(slave.Guid))
                    slave.Motion.Remove(MovementGeneratorType.Follow);
                continue;
            }

            if (slave.Combat.IsInCombat || slave.IsEvading) continue;
            if (_linkedFollowerMaster.TryGetValue(slave.Guid, out ObjectGuid previous) && previous == master.Guid
                && slave.Motion.HasActive(MovementGeneratorType.Follow)) continue;

            if (_linkedFollowerMaster.ContainsKey(slave.Guid)) slave.Motion.Remove(MovementGeneratorType.Follow);

            // SetFollowing: spawn-to-spawn distance less both bounding radii, angle relative to the master's spawn orientation.
            float dx = spawn.X - master.Spawn!.X, dy = spawn.Y - master.Spawn.Y, dz = spawn.Z - master.Spawn.Z;
            float distance = MathF.Max(0, MathF.Sqrt(dx * dx + dy * dy + dz * dz) - slave.BoundingRadius - master.BoundingRadius);
            float angle = Creature.NormalizeOrientation(MathF.Atan2(dy, dx) - master.Spawn.Orientation);
            slave.Motion.MoveFollow(master, distance, angle);
            _linkedFollowerMaster[slave.Guid] = master.Guid;
        }
    }

    /// <summary>
    /// The master of a template-linked slave among the creatures standing for a spawn guid: one of the master entry within the search range.
    /// vmangos LoadFromDB requires it to be unique when search_range is zero (CreatureLinkingMgr.cpp:233-250); otherwise the nearest wins
    /// (equal distances: the one met first, in <see cref="_byEntry"/> slot order).
    /// Deviations from vmangos that predate the spawn-guid index and are kept: vmangos TryFollowMaster takes the first registered master in
    /// range, not the nearest (CreatureLinkingMgr.cpp:720-729), and it checks the range-0 uniqueness once at load, over the database rows,
    /// where this counts the creatures currently loaded on the map.
    /// </summary>
    private Creature? FindTemplateMaster(Creature slave, CreatureSpawn spawn, CreatureTemplateLink template)
    {
        if (_templateMasterCache.NeedsEntrySync)
        {
            _templateMasterCache.NeedsEntrySync = false;
            foreach (Creature indexed in _bySpawnGuid.Values)
            {
                SyncByEntry(indexed);
            }
        }
        else
        {
            SyncByEntry(slave);
        }

        if (!_byEntry.TryGetValue(template.MasterEntry, out List<Creature>? candidates) || candidates.Count == 0)
        {
            return null;
        }

        Creature? best = null;
        float bestDistance = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            Creature candidate = candidates[i];
            if (candidate.Entry != template.MasterEntry)
            {
                SyncByEntry(candidate);
                i--;
                continue;
            }

            if (ReferenceEquals(candidate, slave) || !InSearchRange(spawn, candidate.Spawn!, template.SearchRange)) continue;
            if (template.SearchRange <= 0)
            {
                if (best is not null) return null; // not unique
                best = candidate;
                continue;
            }

            float distance = DistanceSq(spawn, candidate.Spawn!);
            if (best is null || distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>vmangos IsSlaveInRangeOfMaster (CreatureLinkingMgr.cpp:616-629): 2D, between the spawn points; no range means anywhere.</summary>
    private static bool InSearchRange(CreatureSpawn slave, CreatureSpawn master, float range)
    {
        if (range <= 0) return true;
        float dx = slave.X - master.X, dy = slave.Y - master.Y;
        return dx * dx + dy * dy < range * range;
    }

    private static float DistanceSq(CreatureSpawn a, CreatureSpawn b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);
}
