using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// cmangos spawn group formations (Maps/SpawnGroup.cpp FormationData, <c>spawn_group_formation</c>; classic-db z2815 has 164): a member of a
/// group with a formation takes its slot (<c>spawn_group_spawn.SlotId</c>) when it enters the world. The member in slot 0 leads: it walks
/// the formation's <c>waypoint_path</c> (MovementType 2 loops, 4 goes back and forth), wanders (1) or stands, whatever its own spawn row
/// says. The others follow it in the formation's shape (<see cref="FormationMovementGenerator"/>). When the leader dies the first living
/// member takes over at once, or after the group's fight when it died in one, and resumes the path where the leader left it.
/// </summary>
public sealed partial class CreatureMapSystem
{
    private readonly Dictionary<uint, FormationState> _formations = [];

    /// <summary>The formation of spawn group <paramref name="groupId"/> on this map, if it has one (GM, tests).</summary>
    internal FormationState? FormationOf(uint groupId) => _formations.GetValueOrDefault(groupId);

    /// <summary>The leader of spawn group <paramref name="groupId"/>'s formation now, if any (GM, tests).</summary>
    public Creature? FormationLeader(uint groupId) => _formations.GetValueOrDefault(groupId)?.Master as Creature;

    /// <summary>A group with a <c>spawn_group_formation</c> row gets its slots (cmangos CreatureGroup constructor).</summary>
    private void CreateFormation(SpawnGroupState state)
    {
        if (state.Definition.Formation is { } entry && state.Members.Any(m => m.SlotId >= 0))
        {
            _formations[state.Id] = new FormationState(state.Id, entry, state.Members);
        }
    }

    private FormationState? FormationOfMember(Creature creature)
        => creature.Spawn is { } spawn && _groupOfSpawn.TryGetValue(spawn.Guid, out SpawnGroupState? group)
            ? _formations.GetValueOrDefault(group.Id) : null;

    /// <summary>
    /// cmangos FormationData::SetFormationSlot (Creature::AddToWorld → CreatureGroup formation): the member takes its slot; slot 0 starts the
    /// leader's movement, and every follower gets its place and its follow movement.
    /// </summary>
    private void JoinFormation(Creature creature)
    {
        if (FormationOfMember(creature) is not { } formation || !creature.IsAlive || formation.SlotOf(creature) is not null)
        {
            return;
        }

        if (formation.DefaultSlotOf(creature.Spawn!.Guid) is not { } slot)
        {
            return;
        }

        if (slot.Owner is Creature holder && !ReferenceEquals(holder, creature))
        {
            // The slot is held (a follower became leader while this one was away): it takes the holder's original slot back for it.
            if (formation.DefaultSlotOf(holder.Spawn?.Guid ?? 0) is { } holderSlot && holderSlot.Owner is null)
            {
                holderSlot.Owner = holder;
                slot.Owner = null;
            }
            else if (formation.Slots.FirstOrDefault(s => s.Owner is null) is { } free)
            {
                slot = free;
            }
            else
            {
                return;
            }
        }

        slot.Owner = creature;
        if (slot.SlotId == 0)
        {
            formation.MasterDied = false;
        }
        else if (formation.Master is null)
        {
            // No leader in the world yet: the follower waits in place until one joins (cmangos: no master, no follow movement).
            return;
        }

        RefreshFormation(formation);
    }

    /// <summary>cmangos FixSlotsPositions + SetMasterMovement + SetFollowersMaster.</summary>
    private void RefreshFormation(FormationState formation)
    {
        float width = formation.Slots.Select(s => s.Owner as Creature).Where(c => c is not null).Select(c => c!.BoundingRadius * 2f).DefaultIfEmpty(0f).Max();
        formation.FixSlotsPositions(width, _random);
        foreach (FormationSlot slot in formation.Slots)
        {
            if (slot.Owner is not Creature member || !member.IsAlive || !_creatures.ContainsKey(member.Guid))
            {
                continue;
            }

            if (slot.SlotId == 0)
            {
                if (!ReferenceEquals(formation.MovingLeader, member))
                {
                    formation.MovingLeader = member;
                    member.Motion.Initialize(LeaderMovement(formation, member), this, start: true);
                }
            }
            else if (member.Motion.Default is not FormationMovementGenerator current || !ReferenceEquals(current.Slot, slot))
            {
                member.Motion.Initialize(new FormationMovementGenerator(formation, slot), this, start: true);
            }
        }
    }

    /// <summary>cmangos FormationData::SetMasterMovement: the formation's path from the node after the last one reached, a wander, or idle.</summary>
    private ICreatureMovementGenerator LeaderMovement(FormationState formation, Creature leader)
    {
        if (!_options.MovementEnabled)
        {
            return IdleMovementGenerator.Instance;
        }

        switch (formation.MovementType)
        {
            case 2 or 4:
                IReadOnlyList<CreatureWaypoint> path = _content.GetWaypointPath(formation.PathId);
                if (path.Count == 0)
                {
                    _logger.LogDebug("formation of spawn group {Group}: waypoint_path {Path} has no points; the leader stands", formation.GroupId, formation.PathId);
                    return IdleMovementGenerator.Instance;
                }

                int start = formation.LastWaypointIndex < 0 ? 0 : (formation.LastWaypointIndex + 1) % path.Count;
                formation.LastWaypointIndex = -1;
                return new WaypointMovementGenerator(path, linear: formation.MovementType == 4, startIndex: start);

            case 1:
                return new RandomMovementGenerator();

            default:
                return IdleMovementGenerator.Instance;
        }
    }

    /// <summary>cmangos FormationData::OnDeath: the slot is freed; a dead leader is replaced now, or after the group's fight.</summary>
    private void OnFormationMemberDied(Creature creature)
    {
        if (FormationOfMember(creature) is not { } formation || formation.SlotOf(creature) is not { } slot)
        {
            return;
        }

        bool leader = slot.SlotId == 0;
        if (leader && creature.Motion.Default is WaypointMovementGenerator walk)
        {
            formation.LastWaypointIndex = walk.LastReachedIndex;
        }

        slot.Owner = null;
        if (leader)
        {
            formation.MovingLeader = null;
        }

        if (!leader)
        {
            return;
        }

        if (formation.Slots.Any(s => s.Owner is Creature member && member.IsAlive && member.Combat.IsInCombat))
        {
            formation.MasterDied = true; // deferred to arrival home
        }
        else
        {
            TrySetNewLeader(formation);
        }
    }

    /// <summary>cmangos FormationData::OnHome (CREATURE_GROUP_EVENT_HOME when the whole group is out of its fight).</summary>
    private void OnFormationHome(Creature creature)
    {
        if (FormationOfMember(creature) is { MasterDied: true } formation
            && !formation.Slots.Any(s => s.Owner is Creature member && member.IsAlive && (member.Combat.IsInCombat || member.IsEvading)))
        {
            formation.MasterDied = false;
            TrySetNewLeader(formation);
        }
    }

    /// <summary>cmangos FormationData::TrySetNewMaster: the first living slot's member takes slot 0.</summary>
    private void TrySetNewLeader(FormationState formation)
    {
        if (formation.MasterSlot is not { } master
            || formation.Slots.FirstOrDefault(s => s.SlotId != 0 && s.Owner is Creature member && member.IsAlive) is not { } alive)
        {
            return;
        }

        FormationState.Swap(master, alive);
        RefreshFormation(formation);
    }

    /// <summary>A member left the world (cmangos FormationData::Remove → OnDeath).</summary>
    private void OnFormationMemberRemoved(Creature creature)
    {
        if (FormationOfMember(creature) is { } formation && formation.SlotOf(creature) is not null)
        {
            OnFormationMemberDied(creature);
        }
    }
}
