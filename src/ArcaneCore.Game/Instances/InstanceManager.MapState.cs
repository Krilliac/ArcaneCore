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
    /// <summary>Instance maps currently loaded.</summary>
    public IReadOnlyCollection<Map> LoadedMaps => _mapStates.Keys;

    /// <summary>The instance map's runtime state (unload timer and reset flags), or null for other maps.</summary>
    public InstanceMapState? StateOf(Map map) => _mapStates.GetValueOrDefault(map);

    internal void UpdateMap(Map map, uint diffMs)
    {
        if (!_mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            return;
        }

        foreach (Player player in map.Players.ToArray())
        {
            try
            {
                UpdateHomebindTimer(player, diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("instance.manager", ex);
                _logger.LogFault("instance.homebind-timer", ex, "Failed to update homebind timer for player {PlayerGuid} on map {MapId} instance {InstanceId}", player.Guid, map.MapId, map.InstanceId);
            }
        }

        if (state.UnloadTimerMs > 0)
        {
            if (diffMs >= state.UnloadTimerMs)
            {
                state.UnloadTimerMs = 0;
                try
                {
                    if (map.PlayerCount > 0)
                    {
                        VerifyNobodyInside(state.Save, UnloadWithPlayersInsideCode, "map unloaded");
                    }

                    _world.UnloadMap(map);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    FaultLedger.Shared.Record("instance.manager", ex);
                    _logger.LogFault("instance.unload-map", ex, "Failed to unload empty instance map {MapId} instance {InstanceId}", map.MapId, map.InstanceId);
                }
            }
            else
            {
                state.UnloadTimerMs -= diffMs;
            }
        }
    }

    internal void PlayerLeftMap(Map map, Player player)
    {
        if (!_mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            return;
        }

        if (_players.TryGetValue(player.Guid, out PlayerState? playerState))
        {
            playerState.LeftDungeonMapId = map.MapId;
            if (playerState.HomebindTimerMs != 0)
            {
                playerState.HomebindTimerMs = 0;
                player.Session.Send(WorldOpcode.SmsgRaidGroupOnly, InstancePackets.BuildRaidGroupOnly(0, RaidGroupError.Required));
            }
        }

        // vmangos DungeonMap::Remove: the last player out starts the unload timer and arms
        // the normal reset.
        if (map.Players.All(p => ReferenceEquals(p, player)))
        {
            state.UnloadTimerMs = state.UnloadWhenEmpty ? InstanceOptions.MinUnloadDelayMs : _options.EffectiveUnloadDelayMs;
            if (!state.Save.Template.IsRaid)
            {
                state.Save.ResetScheduled = true;
            }
        }
    }

    private void OnMapUnloading(Map map)
    {
        if (!_mapStates.Remove(map, out InstanceMapState? state))
        {
            return;
        }

        if (state.KillHandler is not null && map.FindUpdater<MapCombat>() is { } combat)
        {
            combat.UnitKilled -= state.KillHandler;
        }

        InstanceSave save = state.Save;
        VerifyScriptStateSaved(map, state);
        if (state.ResetAfterUnload)
        {
            ResetSave(save);
        }
        else if (!save.HasBinds)
        {
            DeleteSave(save);
        }
        else if (!save.Template.IsRaid)
        {
            save.ResetScheduled = true;
        }

        _logger.LogDebug("unloaded map of {Save}", save);
    }
}

/// <summary>Runtime state of one loaded instance map (vmangos <c>DungeonMap</c> members).</summary>
public sealed class InstanceMapState
{
    internal InstanceMapState(InstanceSave save) => Save = save;

    public InstanceSave Save { get; }

    /// <summary>Milliseconds until the map unloads; 0 while it is occupied (vmangos <c>m_unloadTimer</c>).</summary>
    public uint UnloadTimerMs { get; internal set; }

    /// <summary>Unload as soon as the last player leaves (vmangos <c>m_unloadWhenEmpty</c>).</summary>
    public bool UnloadWhenEmpty { get; internal set; }

    /// <summary>Reset (delete) the save when the map unloads; nobody may enter meanwhile (vmangos <c>m_resetAfterUnload</c>).</summary>
    public bool ResetAfterUnload { get; internal set; }

    /// <summary>Unix seconds of the last "please leave" notice of a refused personal reset (<see cref="InstanceOptions.ResetRefusedNoticeSeconds"/>).</summary>
    internal long? LastRefusedResetNotice { get; set; }

    internal Action<Unit?, Unit>? KillHandler { get; set; }

    /// <summary>The instance script's save string right after it was created and loaded (null: no script, or it keeps none); the state a map starts with.</summary>
    internal string? BaselineData { get; set; }
}

/// <summary>Drives one instance map's unload timer and its players' homebind timers.</summary>
internal sealed class InstanceMapUpdater(InstanceManager manager) : IMapUpdater
{
    public void Update(Map map, uint diffMs)
    {
        try
        {
            manager.UpdateMap(map, diffMs);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("instance.map-updater", ex);
        }
    }

    public void OnPlayerRemoved(Map map, Player player)
    {
        try
        {
            manager.PlayerLeftMap(map, player);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("instance.player-removed", ex);
        }
    }
}
