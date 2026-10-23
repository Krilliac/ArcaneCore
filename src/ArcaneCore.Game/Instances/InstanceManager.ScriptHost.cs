using System.Diagnostics.CodeAnalysis;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Instances.Scripts;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ArcaneCore.Game.Instances;

public sealed partial class InstanceManager
{
    /// <summary>
    /// Which maps have an instance script (vmangos <c>map_template.ScriptName</c>); the scripts of the Game assembly by default. Read on every
    /// instance map creation, so a module swap committed on this registry (<see cref="InstanceScriptRegistry.CommitSwap"/>) applies to the next
    /// instance created; loaded instances keep the <see cref="InstanceData"/> they were created with (its state is their progress).
    /// </summary>
    public InstanceScriptRegistry Scripts { get; set; } = InstanceScriptRegistry.Default;

    /// <summary>
    /// How many loaded instance maps still run script code that is no longer the current script of their map in <see cref="Scripts"/> (replaced
    /// or removed by a swap, or created by a registry since replaced): what the module host reports as retired code still live. Such an instance
    /// keeps its script until it unloads or resets. World thread.
    /// </summary>
    public int RetiredScriptInstanceCount
        => _mapStates.Keys.Count(map => map.FindUpdater<InstanceData>() is { } data && !Scripts.RunsCurrentScript(data));

    /// <summary>World service callbacks passed to each ScriptDev2 instance at creation.</summary>
    public Action<Player, uint, ObjectGuid>? ScriptCreatureCredit { get; set; }

    public Action<Player, uint>? ScriptCastPlayerSpell { get; set; }

    public Action<Player, uint, ObjectGuid>? ScriptCastPlayerTargetSpell { get; set; }

    /// <summary>
    /// vmangos Map::CreateInstanceData (Maps/Map.cpp:1987-2037) for a map with a script (<see cref="Scripts"/>): the script is created and
    /// initialized, then loads the save string the instance save holds (vmangos <c>SELECT data FROM instance</c>), and is attached to the map
    /// before anything is placed in it. Creatures and game objects the map already has (a system that loaded a grid early) are reported to it as
    /// created. Its saves go to the instance save and to <see cref="IInstancePersistence.InstanceDataSaved"/>.
    /// </summary>
    private void AttachInstanceData(Map map, InstanceSave save)
    {
        if (map.FindUpdater<InstanceData>() is not null || Scripts.Create(map) is not { } data)
        {
            return;
        }

        data.Logger = _logger;
        data.QuestCompleteUnrewarded = QuestCompleteUnrewarded;
        data.GameEventActive = eventId => GameEventActive(eventId);
        data.HolidayActive = holidayId => HolidayActive(holidayId);
        data.CreatureCredit = ScriptCreatureCredit;
        data.CastPlayerSpell = ScriptCastPlayerSpell;
        data.CastPlayerTargetSpell = ScriptCastPlayerTargetSpell;
        data.Saving = (_, text) =>
        {
            if (save.IsDeleted)
            {
                ReportSaveDataDropped(save, text);
                return;
            }

            save.Data = text;
            _persistence.InstanceDataSaved(save);
        };
        try
        {
            data.Initialize();
            if (save.Data is { } stored)
            {
                data.Load(stored);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("instance.manager", ex);
            _logger.LogFault("instance.script-init", ex, "Instance script Initialize/Load failed on map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
        }

        map.AddUpdater(data);
        foreach (Creature creature in map.FindUpdater<CreatureMapSystem>()?.Creatures.ToArray() ?? [])
        {
            try
            {
                data.OnCreatureCreate(creature);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("instance.manager", ex);
                _logger.LogFault("instance.script-creature-create", ex, "Instance script OnCreatureCreate failed on map {MapId} instance {InstanceId} for creature {CreatureEntry}", map.MapId, map.InstanceId, creature.Entry);
            }
        }

        foreach (GameObject go in map.FindUpdater<GameObjectMapSystem>()?.GameObjects.ToArray() ?? [])
        {
            try
            {
                data.OnObjectCreate(go);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("instance.manager", ex);
                _logger.LogFault("instance.script-object-create", ex, "Instance script OnObjectCreate failed on map {MapId} instance {InstanceId} for object {ObjectEntry}", map.MapId, map.InstanceId, go.Entry);
            }
        }
    }

    /// <summary>The script state of an instance map (vmangos <c>Map::GetInstanceData</c>), or null when its map has no script.</summary>
    public static InstanceData? InstanceDataOf(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.FindUpdater<InstanceData>();
    }
}
