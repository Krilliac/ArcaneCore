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
    private bool IsBattlegroundMap(uint mapId, [NotNullWhen(true)] out IMapResolver? resolver)
    {
        if (BattlegroundMaps is { } bgResolver && Registry.Find(mapId) is { IsBattleground: true })
        {
            resolver = bgResolver;
            return true;
        }

        resolver = null;
        return false;
    }

    /// <inheritdoc />
    public Map ResolveLoginMap(Player player)
    {
        if (IsBattlegroundMap(player.MapId, out IMapResolver? battlegrounds))
        {
            return battlegrounds.ResolveLoginMap(player);
        }

        MapTemplate? template = Registry.Find(player.MapId);
        if (template is null || !template.IsDungeon)
        {
            return _world.GetMap(player.MapId);
        }

        // vmangos Player::LoadFromDB: a dungeon login needs the save the player was in; an
        // instance reset meanwhile (or no bind at all) sends it to the entrance.
        InstanceSave? save = GetBoundSaveForSelfOrGroup(player.Guid, player.MapId);
        bool lastMatches = _lastInstance.TryGetValue(player.Guid, out (uint MapId, uint InstanceId) last)
            && save is not null && last.MapId == player.MapId && last.InstanceId == save.InstanceId;
        TransferAbortReason? loginRefusal = save is not null && lastMatches ? CheckEntry(player, template, save, sendErrors: false) : null;
        if (save is not null && lastMatches && loginRefusal is null)
        {
            return GetOrCreateInstanceMap(save); // the bind waits for OnLoginMapAdd (vmangos DungeonMap::Add)
        }

        if (loginRefusal is { } refused)
        {
            ReportEnterRefused(player, player.MapId, refused, sendErrors: false);
        }

        RelocateToEntrance(player, template);
        return _world.GetMap(player.MapId);
    }

    /// <inheritdoc />
    public void OnLoginMapAdd(Player player, Map map)
    {
        if (_mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            BindPlayerOrGroupOnEnter(player, state.Save, map);
            _enterLimiter.Record(player.AccountId, state.Save.InstanceId, Now);
        }
    }

    /// <inheritdoc />
    public bool CanEnter(Player player, uint mapId)
    {
        if (IsBattlegroundMap(mapId, out IMapResolver? battlegrounds))
        {
            return battlegrounds.CanEnter(player, mapId);
        }

        MapTemplate? template = Registry.Find(mapId);
        if (template is null || !template.IsDungeon)
        {
            return true;
        }

        InstanceSave? save = GetBoundSaveForSelfOrGroup(player.Guid, mapId);
        TransferAbortReason? refusal = CheckEntry(player, template, save, sendErrors: true);
        if (refusal is { } reason)
        {
            ReportEnterRefused(player, mapId, reason, sendErrors: true);
        }

        return refusal is null;
    }

    /// <inheritdoc />
    public Map? ResolveEntry(Player player, uint mapId)
    {
        if (IsBattlegroundMap(mapId, out IMapResolver? battlegrounds))
        {
            return battlegrounds.ResolveEntry(player, mapId);
        }

        MapTemplate? template = Registry.Find(mapId);
        if (template is null || !template.IsDungeon)
        {
            return _world.GetMap(mapId);
        }

        InstanceSave? save = GetBoundSaveForSelfOrGroup(player.Guid, mapId);
        if (CheckEntry(player, template, save, sendErrors: true) is { } refusal)
        {
            ReportEnterRefused(player, mapId, refusal, sendErrors: true);
            return null;
        }

        save ??= CreateSave(template);
        Map map = GetOrCreateInstanceMap(save);
        BindPlayerOrGroupOnEnter(player, save, map);
        _enterLimiter.Record(player.AccountId, save.InstanceId, Now); // vmangos DungeonMap::Add
        return map;
    }

    /// <inheritdoc />
    public void OnEntered(Player player, Map map)
    {
        // The battleground resolver hears about every arrival (a far teleport out of a match leaves it, Player.cpp:2036-2043).
        BattlegroundMaps?.OnEntered(player, map);
        PlayerState state = StateFor(player);
        SendSavedInstances(player); // every far teleport, instance or not (vmangos SendNewWorld)
        if (!_mapStates.TryGetValue(map, out InstanceMapState? mapState))
        {
            // vmangos HandleMoveWorldportAckOpcode: "reset instance validity, except if going
            // to an instance inside an instance", then ResetPersonalInstanceOnLeaveDungeon.
            state.Valid = true;
            if (state.LeftDungeonMapId is uint left)
            {
                state.LeftDungeonMapId = null;
                ResetPersonalInstanceOnLeaveDungeon(player, left);
            }

            return;
        }

        state.LeftDungeonMapId = null;
        uint characterId = player.Guid.Counter;
        _lastInstance[player.Guid] = (map.MapId, map.InstanceId);
        _persistence.PlayerEnteredInstance(characterId, map.MapId, map.InstanceId);
        if (map.FindUpdater<InstanceData>() is { } instanceData)
        {
            try
            {
                instanceData.OnPlayerEnter(player);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("instance.manager", ex);
                _logger.LogFault("instance.script-player-enter", ex, "Instance script OnPlayerEnter failed on map {MapId} instance {InstanceId} for player {PlayerGuid}", map.MapId, map.InstanceId, player.Guid);
            }
        }

        // vmangos: "raid instance welcome" (SendInstanceResetWarning on entering a raid).
        if (mapState.Save.Template.IsRaid)
        {
            long resetTime = GetRaidResetTime(map.MapId);
            if (resetTime > 0)
            {
                uint left = (uint)Math.Max(0, resetTime - Now);
                player.Session.Send(
                    WorldOpcode.SmsgRaidInstanceMessage,
                    InstancePackets.BuildRaidInstanceMessage(InstancePackets.MessageTypeFor(left), map.MapId, left));
            }
        }
    }

    /// <inheritdoc />
    public Map? ResolveCorpseMap(uint mapId, uint instanceId)
    {
        if (IsBattlegroundMap(mapId, out IMapResolver? battlegrounds))
        {
            return battlegrounds.ResolveCorpseMap(mapId, instanceId);
        }

        if (_world.FindMap(mapId, instanceId) is { } loaded)
        {
            return loaded;
        }

        // A live save gets its map the way an entry creates it: managed, so it unloads on its timer and the timed reset of the
        // save still sees it. A deleted or unknown instance gets none (the body stays out of every map).
        return _saves.TryGetValue(instanceId, out InstanceSave? save) && save.MapId == mapId && !save.IsDeleted
            ? GetOrCreateInstanceMap(save)
            : null;
    }

    private TransferAbortReason? CheckEntry(Player player, MapTemplate template, InstanceSave? save, bool sendErrors)
    {
        // vmangos MapManager::CanPlayerEnter: raids need a raid group.
        if (template.IsRaid && !player.IsGameMaster && !_options.IgnoreRaidGroup && GroupOf(player.Guid) is not { IsRaid: true })
        {
            if (sendErrors)
            {
                player.Session.Send(WorldOpcode.SmsgRaidGroupOnly, InstancePackets.BuildRaidGroupOnly(0, RaidGroupError.Required));
            }

            return TransferAbortReason.Silently;
        }

        // vmangos MapManager::CanPlayerEnter (MapManager.cpp:209-214): the hourly per-account
        // limit, keyed by the id of the save the player would enter (0 when it will be a new one).
        // Checked on teleports only (sendErrors), not on the login re-entry.
        if (sendErrors && _options.PerHourLimit > 0 && !player.IsGameMaster
            && !_enterLimiter.CanEnter(player.AccountId, save?.InstanceId ?? 0, _options.PerHourLimit, Now))
        {
            player.Session.Send(WorldOpcode.SmsgTransferAborted, TeleportPackets.BuildTransferAborted(TransferAbortReason.TooManyInstances));
            return TransferAbortReason.TooManyInstances;
        }

        if (save is null)
        {
            return null;
        }

        Map? map = _world.FindMap(save.MapId, save.InstanceId);
        if (map is null)
        {
            return null;
        }

        // vmangos DungeonMap::CanEnter (Map.cpp:2134-2146): the player cap first (GMs neither
        // count nor are refused), then the pending reset, which also refuses GMs.
        TransferAbortReason? reason = null;
        if (!player.IsGameMaster && template.PlayerLimit > 0
            && map.Players.Count(p => !p.IsGameMaster && !ReferenceEquals(p, player)) >= template.PlayerLimit)
        {
            reason = TransferAbortReason.MaxPlayers;
        }
        else if (_mapStates.TryGetValue(map, out InstanceMapState? state) && state.ResetAfterUnload)
        {
            reason = TransferAbortReason.NotFound;
        }
        else if (template.IsRaid && !player.IsGameMaster && player.IsAlive && GroupInCombatInside(player, map))
        {
            // vmangos DungeonMap::CanEnter (Map.cpp:2148-2166, patch 1.11.0): no corpse-running back into a live boss fight.
            reason = TransferAbortReason.ZoneInCombat;
        }

        if (reason is { } abort && sendErrors)
        {
            player.Session.Send(WorldOpcode.SmsgTransferAborted, TeleportPackets.BuildTransferAborted(abort));
        }

        return reason;
    }

    /// <summary>vmangos <c>InstanceData::IsEncounterInProgress</c> plus <c>Group::InCombatToInstance</c>: a fight is live and another member of the entrant's group is in combat on that map.</summary>
    private bool GroupInCombatInside(Player player, Map map)
    {
        if (map.FindUpdater<InstanceData>() is not { IsEncounterInProgress: true } || GroupOf(player.Guid) is not { } group)
        {
            return false;
        }

        foreach (Player member in map.Players)
        {
            if (!ReferenceEquals(member, player) && member.Combat.IsInCombat && GroupOf(member.Guid)?.Id == group.Id)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="mapId"/> is a dungeon (an instance-0 transfer request for it needs <see cref="ReserveEntryInstance"/>; continents never do). Reads the immutable map registry: any thread.</summary>
    public bool IsInstancedMap(uint mapId) => Registry.Find(mapId) is { IsDungeon: true };

    /// <summary>
    /// The instance a character entering <paramref name="mapId"/> from another worker will enter (cluster transfer, orchestrator decision Q8; the target worker allocates ids). The save the
    /// character or its group is already bound to wins (vmangos <c>Player::GetBoundInstanceSaveForSelfOrGroup</c>); otherwise a new save is created and its map is created managed. The new save is
    /// deliberately NOT bound yet and its map is held (no unload timer): while the source still owns the character, the target's bind write is refused by the worker write fence
    /// (<c>CharacterWriteClaim</c>, design _wf/writer-lock-DESIGN.md C5), so the bind is made by <see cref="BindReservedEntry"/> once the transfer committed, and an aborted reservation is undone
    /// by <see cref="ReleaseEntryInstance"/>. Returns 0 when <paramref name="mapId"/> is not a dungeon (continents use instance 0; battleground instance ids are bound by the battleground holder). World thread.
    /// </summary>
    public uint ReserveEntryInstance(ObjectGuid player, uint mapId)
    {
        MapTemplate? template = Registry.Find(mapId);
        if (template is null || !template.IsDungeon)
        {
            return 0;
        }

        InstanceSave? save = GetBoundSaveForSelfOrGroup(player, mapId);
        if (save is null || save.IsDeleted)
        {
            save = CreateSave(template);
            Map created = GetOrCreateInstanceMap(save);
            if (_mapStates.TryGetValue(created, out InstanceMapState? state))
            {
                state.UnloadTimerMs = 0; // held until BindReservedEntry or ReleaseEntryInstance
            }

            return save.InstanceId;
        }

        GetOrCreateInstanceMap(save);
        return save.InstanceId;
    }

    /// <summary>
    /// Undoes <see cref="ReserveEntryInstance"/> for an aborted or refused reservation: a save nobody is bound to is dropped together with its map (the map unloads after the current map pass and
    /// <see cref="OnMapUnloading"/> deletes the unbound save; a map that was never created has its save deleted here). A save with binds (the character's or its group's own instance, or one
    /// <see cref="BindReservedEntry"/> bound) is left alone. Idempotent. World thread.
    /// </summary>
    public void ReleaseEntryInstance(uint mapId, uint instanceId)
    {
        if (instanceId == 0 || FindSave(instanceId) is not { IsDeleted: false } save || save.MapId != mapId || save.HasBinds)
        {
            return;
        }

        if (_world.FindMap(mapId, instanceId) is { } map)
        {
            if (_mapStates.TryGetValue(map, out InstanceMapState? state))
            {
                state.UnloadTimerMs = _options.EffectiveUnloadDelayMs; // a map that stays occupied still unloads after its last player leaves
            }

            _world.UnloadMap(map);
        }
        else
        {
            DeleteSave(save);
        }
    }

    /// <summary>
    /// Binds <paramref name="player"/> to the instance <see cref="ReserveEntryInstance"/> answered, after the transfer committed (the claim moved to this worker, so the bind write is accepted):
    /// a non-permanent bind as <c>DungeonMap::BindPlayerOrGroupOnEnter</c> makes for a solo entrant, and the held map goes back on its unload timer (an arrival then keeps it loaded). False when the save
    /// is gone or the character is bound to a different instance of the map meanwhile (nothing is changed). A character already bound to that save, itself or through its group, stays as it is. World thread.
    /// </summary>
    public bool BindReservedEntry(ObjectGuid player, uint mapId, uint instanceId)
    {
        if (instanceId == 0 || FindSave(instanceId) is not { IsDeleted: false } save || save.MapId != mapId)
        {
            return false;
        }

        InstanceSave? bound = GetBoundSaveForSelfOrGroup(player, mapId);
        if (bound is not null && bound != save)
        {
            return false;
        }

        if (bound is null)
        {
            BindPlayer(player, save, permanent: false);
        }

        Map map = GetOrCreateInstanceMap(save);
        if (map.PlayerCount == 0 && _mapStates.TryGetValue(map, out InstanceMapState? state))
        {
            state.UnloadTimerMs = _options.EffectiveUnloadDelayMs;
        }

        return true;
    }

    private InstanceSave CreateSave(MapTemplate template)
    {
        uint id = NextRawInstanceId();
        while (_saves.ContainsKey(id) || _world.FindMap(template.Entry, id) is not null)
        {
            id = NextRawInstanceId();
        }

        long resetTime = template.IsRaid ? GetRaidResetTime(template.Entry) : Now + _options.NormalDungeonResetSeconds;
        var save = new InstanceSave(id, template, resetTime);
        _saves[id] = save;
        _persistence.InstanceSaved(save);
        _logger.LogDebug("created {Save}", save);
        SaveCreated?.Invoke(save);
        return save;
    }

    private Map GetOrCreateInstanceMap(InstanceSave save)
    {
        Map map = _world.GetMap(save.MapId, save.InstanceId);
        if (!_mapStates.ContainsKey(map))
        {
            var state = new InstanceMapState(save) { UnloadTimerMs = _options.EffectiveUnloadDelayMs };
            _mapStates[map] = state;
            map.AddUpdater(new InstanceMapUpdater(this));
            state.KillHandler = (killer, victim) => OnUnitKilled(map, killer, victim);
            if (map.FindUpdater<MapCombat>() is { } combat)
            {
                combat.UnitKilled += state.KillHandler;
            }

            AttachInstanceData(map, save);
            state.BaselineData = CurrentScriptData(map);
        }

        return map;
    }
}
