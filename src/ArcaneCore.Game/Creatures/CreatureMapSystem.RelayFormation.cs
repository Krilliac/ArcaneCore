using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.SpawnGroups;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.SpawnGroups;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// dbscript command 51, SCRIPT_COMMAND_SPAWN_GROUP (cmangos DBScripts/ScriptMgr.cpp, the load checks in ScriptMgr::LoadScripts and the
/// handler in ScriptAction::ExecuteDbscriptCommand; Maps/SpawnGroup.cpp CreatureGroup::SetFormationData and FormationData; no code copied).
/// The subcommand is <c>datalong</c>:
/// <list type="bullet">
/// <item>150 create a formation for spawn group <c>datalong2</c> (0: the group of the target, else the source): shape <c>dataint</c>,
/// spread <c>x</c>, options <c>dataint2</c>. The formation is dynamic: the members in the world take their slots and the followers follow
/// the leader, whose own movement is left to the script (a later MOVEMENT on it sets the formation's movement). A group that already has
/// a formation is left as it is.</item>
/// <item>151 remove the formation of spawn group <c>datalong2</c>: the followers go back to their own movement; a dynamic formation's
/// leader keeps its movement.</item>
/// <item>100 switch the target's formation to shape <c>datalong2</c>; 101 set its spread to <c>x</c>; 102 set its options to
/// <c>datalong2</c>.</item>
/// </list>
/// As in cmangos, a step with a shape outside 0-6 (150, 100) or a spread outside 0.5-15 yd (101) is dropped (cmangos drops it at load),
/// as is a 151 naming no spawn group of the map. classic-db z2815 uses only subcommand 150, in relay 1162501 (Cork Gizelton's caravan,
/// group 19019).
/// </summary>
public sealed partial class CreatureMapSystem
{
    private const int FormationShapeCount = 7; // SPAWN_GROUP_FORMATION_TYPE_COUNT

    /// <returns>Whether the script ends here (cmangos returns true for a bad shape or spread at run time).</returns>
    private bool RelaySpawnGroup(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        switch (step.DataLong)
        {
            case 150:
                if (step.DataInt is < 0 or >= FormationShapeCount)
                {
                    ReportFormationStep(step, $"invalid formation shape id({step.DataInt})");
                    return false;
                }

                if (FormationGroupOf(step, source, target) is { } created)
                {
                    CreateScriptedFormation(step, created);
                }

                return false;

            case 151:
                if (!_spawnGroups.ContainsKey(step.DataLong2))
                {
                    ReportFormationStep(step, $"invalid spawngroup id({step.DataLong2})");
                    return false;
                }

                if (FormationGroupOf(step, source, target) is { } removed)
                {
                    if (_formations.TryGetValue(removed.Id, out FormationState? formation))
                    {
                        DisbandFormation(formation);
                    }
                    else
                    {
                        ReportFormationStep(step, $"formation remove failed: group {removed.Id} has no formation");
                    }
                }

                return false;

            case 100:
                if (step.DataLong2 >= FormationShapeCount)
                {
                    ReportFormationStep(step, $"invalid formation shape id({step.DataLong2})");
                    return false;
                }

                if (TargetFormation(step, target) is { } shaped)
                {
                    if (shaped.Shape == (FormationShape)step.DataLong2)
                    {
                        ReportFormationStep(step, "switch shape failed: the formation already has that shape"); // SwitchFormation false
                    }
                    else
                    {
                        shaped.Shape = (FormationShape)step.DataLong2;
                        RefreshFormation(shaped);
                    }
                }

                return false;

            case 101:
                if (step.X is < 0.5f or > 15f)
                {
                    ReportFormationStep(step, $"invalid formation spread({step.X})");
                    return false;
                }

                if (TargetFormation(step, target) is { } spread)
                {
                    spread.Spread = step.X;
                    RefreshFormation(spread);
                }

                return false;

            case 102:
                if (TargetFormation(step, target) is { } options)
                {
                    options.Options = step.DataLong2; // KEEP_COMPACT, the only option that moves slots, is not modelled
                }

                return false;

            default:
                ReportRelay(step, $"SPAWN_GROUP subcommand {step.DataLong}");
                return false;
        }
    }

    /// <summary>The group 150/151 work on: <c>datalong2</c>, or with 0 the group of the target (else the source), which must be a creature.</summary>
    private SpawnGroupState? FormationGroupOf(RelayScriptStep step, WorldObject? source, WorldObject? target)
    {
        if ((target ?? source) is not Creature leader)
        {
            ReportFormationStep(step, "the target is not a creature");
            return null;
        }

        SpawnGroupState? group = step.DataLong2 == 0
            ? leader.Spawn is { } spawn ? _groupOfSpawn.GetValueOrDefault(spawn.Guid) : null
            : _spawnGroups.GetValueOrDefault(step.DataLong2);
        if (group is null)
        {
            ReportFormationStep(step, $"target group({step.DataLong2}) not found");
        }

        return group;
    }

    /// <summary>The formation the target holds a slot in (100-102 act on the target only).</summary>
    private FormationState? TargetFormation(RelayScriptStep step, WorldObject? target)
    {
        if (target is not Creature leader)
        {
            ReportFormationStep(step, "the target is not a creature");
            return null;
        }

        if (FormationOfMember(leader) is { } formation && formation.SlotOf(leader) is not null)
        {
            return formation;
        }

        ReportFormationStep(step, $"{leader.Guid} is not in a formation");
        return null;
    }

    /// <summary>
    /// CreatureGroup::SetFormationData(new dynamic entry) → FormationData::Reset: every member in the world takes its own slot, the one in
    /// slot 0 (else the first living member) leads, and the followers start following it.
    /// </summary>
    private void CreateScriptedFormation(RelayScriptStep step, SpawnGroupState group)
    {
        if (_formations.ContainsKey(group.Id))
        {
            ReportFormationStep(step, $"formation create failed: group {group.Id} already has a formation"); // remove it first
            return;
        }

        var entry = new SpawnGroupFormation((byte)step.DataInt, step.X, (uint)step.DataInt2, 0, 0, "Dynamically created formation!");
        var formation = new FormationState(group.Id, entry, group.Members, isDynamic: true) { Spread = step.X };
        _formations[group.Id] = formation;
        foreach (Creature creature in _creatures.Values)
        {
            if (creature.IsAlive && creature.Spawn is { } spawn && group.Objects.ContainsKey(spawn.Guid)
                && formation.DefaultSlotOf(spawn.Guid) is { } slot && slot.Owner is null)
            {
                slot.Owner = creature;
            }
        }

        if (formation.Master is Creature { IsAlive: true })
        {
            RefreshFormation(formation);
        }
        else
        {
            TrySetNewLeader(formation);
        }
    }

    /// <summary>
    /// CreatureGroup::SetFormationData(nullptr) → FormationData::Disband: the members leave their slots and go back to their own movement;
    /// a dynamic formation's leader keeps what the script gave it.
    /// </summary>
    private void DisbandFormation(FormationState formation)
    {
        _formations.Remove(formation.GroupId);
        foreach (FormationSlot slot in formation.Slots)
        {
            if (slot.Owner is Creature member && member.IsAlive && _creatures.ContainsKey(member.Guid) && !(formation.IsDynamic && slot.SlotId == 0))
            {
                member.Motion.Initialize(CreateMovementGenerator(member), this, start: true);
            }

            slot.Owner = null;
        }
    }

    private void ReportFormationStep(RelayScriptStep step, string what)
    {
        if (_reportedAi.Add($"relay51:{step.Id}:{step.DelayMs}:{what}"))
        {
            _logger.LogWarning("relay script {Relay} command 51 subcommand {Sub}: {What}; that step does nothing", step.Id, step.DataLong, what);
        }
    }
}
