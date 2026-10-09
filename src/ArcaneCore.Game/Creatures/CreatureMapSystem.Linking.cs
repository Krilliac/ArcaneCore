using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Creatures;

public sealed partial class CreatureMapSystem
{
    private const uint LinkFollow = 0x200;
    private readonly Dictionary<ObjectGuid, ObjectGuid> _linkedFollowerMaster = [];

    /// <summary>
    /// creature_linking FLAG_FOLLOW (0x200): vmangos CreatureLinkingHolder::TryFollowMaster and SetFollowing (CreatureLinkingMgr.cpp:711-794)
    /// make a living, out-of-combat slave follow its master at the distance and angle of their spawn points; vmangos does it when the master
    /// respawns and when the slave goes home after an evade (MotionMaster::MoveTargetedHome). ArcaneCore checks on each map tick instead:
    /// a slave without a follow generator re-follows, and one whose master is gone or dead is released.
    /// </summary>
    private void UpdateLinkedFollowers()
    {
        if (_creatures.Count == 0 || !_content.HasFollowLinks) return;
        Dictionary<uint, Creature>? bySpawn = null;
        foreach (Creature slave in _creatures.Values)
        {
            if (slave.Spawn is not { } spawn) continue;
            // vmangos GetLinkedTriggerInformation (CreatureLinkingMgr.cpp:310-326): the spawn's own link first, then its entry's on this map.
            CreatureLink? link = _content.FindLink(spawn.Guid);
            CreatureTemplateLink? template = link is null ? _content.FindTemplateLink(slave.Entry, Map.MapId) : null;
            uint flags = link?.Flags ?? template?.Flags ?? 0;
            if ((flags & LinkFollow) == 0) continue;

            bySpawn ??= _creatures.Values.Where(c => c.Spawn is not null).GroupBy(c => c.Spawn!.Guid).ToDictionary(g => g.Key, g => g.First());
            Creature? master = null;
            if (link is not null)
            {
                bySpawn.TryGetValue(link.MasterGuid, out master);
            }
            else if (_linkedFollowerMaster.TryGetValue(slave.Guid, out ObjectGuid current)
                && _creatures.TryGetValue(current, out Creature? followed) && followed.IsAlive)
            {
                master = followed; // keep the master already found while it lives
            }
            else
            {
                Creature[] candidates = [.. bySpawn.Values.Where(c => !ReferenceEquals(c, slave) && c.Entry == template!.MasterEntry
                    && InSearchRange(spawn, c.Spawn!, template.SearchRange))];
                // vmangos LoadFromDB requires a unique master when search_range is zero (CreatureLinkingMgr.cpp:233-250).
                master = template!.SearchRange <= 0
                    ? candidates.Length == 1 ? candidates[0] : null
                    : candidates.OrderBy(c => DistanceSq(spawn, c.Spawn!)).FirstOrDefault();
            }

            if (master is null || !master.IsAlive || !slave.IsAlive)
            {
                if (_linkedFollowerMaster.Remove(slave.Guid))
                    slave.Motion.Remove(MovementGeneratorType.Follow);
                continue;
            }

            if (slave.Combat.IsInCombat || slave.IsEvading) continue;
            if (_linkedFollowerMaster.TryGetValue(slave.Guid, out ObjectGuid previous) && previous == master.Guid
                && slave.Motion.ActiveTypes.Contains(MovementGeneratorType.Follow)) continue;

            if (_linkedFollowerMaster.ContainsKey(slave.Guid)) slave.Motion.Remove(MovementGeneratorType.Follow);

            // SetFollowing: spawn-to-spawn distance less both bounding radii, angle relative to the master's spawn orientation.
            float dx = spawn.X - master.Spawn!.X, dy = spawn.Y - master.Spawn.Y, dz = spawn.Z - master.Spawn.Z;
            float distance = MathF.Max(0, MathF.Sqrt(dx * dx + dy * dy + dz * dz) - slave.BoundingRadius - master.BoundingRadius);
            float angle = Creature.NormalizeOrientation(MathF.Atan2(dy, dx) - master.Spawn.Orientation);
            slave.Motion.MoveFollow(master, distance, angle);
            _linkedFollowerMaster[slave.Guid] = master.Guid;
        }

        if (_linkedFollowerMaster.Count > 0)
        {
            foreach (ObjectGuid guid in _linkedFollowerMaster.Keys.Where(g => !_creatures.ContainsKey(g)).ToArray())
                _linkedFollowerMaster.Remove(guid);
        }
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
