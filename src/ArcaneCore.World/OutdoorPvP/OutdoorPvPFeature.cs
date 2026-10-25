using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.OutdoorPvP;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.States;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using ArcaneCore.World.Graveyards;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.OutdoorPvP;

/// <summary>
/// Outdoor PvP (vmangos <c>ZoneScriptMgr</c> + src/game/OutdoorPvP/, cross-checked with MaNGOS Zero <c>OutdoorPvPMgr</c>): one zone script
/// per continent map — the Eastern Plaguelands towers on map 0 and Silithyst on map 1 — fed by zone changes (vmangos
/// <c>HandlePlayerEnterZone/LeaveZone</c>, Player.cpp:6596-6598), logout (Player.cpp:2215), area triggers, the Silithyst flag drop on
/// mount or stealth (Unit.cpp:5802-5810, SpellAuras.cpp:3637-3645), the world tick and SMSG_INIT_WORLD_STATES. World thread.
/// </summary>
public sealed partial class OutdoorPvPFeature(IServiceProvider services, ILogger<OutdoorPvPFeature> logger)
    : IWorldFeature, IPlayerLocationListener, IAreaTriggerListener, IWorldStateProvider, IGraveyardLinkSource
{
    private readonly Dictionary<Map, OutdoorPvPZoneScript> _scripts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ObjectGuid, (OutdoorPvPZoneScript Script, Team Team)> _present = [];
    private readonly Dictionary<ObjectGuid, uint> _flagRemovedAt = [];
    private WorldRuntime? _world;
    private OutdoorPvPWorldHost? _host;

    /// <summary>After the zone feature's init world states (int.MinValue).</summary>
    public int Order => 10;

    internal WorldRuntime World => _world ?? throw new InvalidOperationException("the outdoor PvP feature is not attached");

    internal IServiceProvider Services => services;

    internal SpellSystem? Spells => services.GetService<SpellFeature>()?.System;

    /// <summary>The live zone scripts, for diagnostics and tests.</summary>
    public IEnumerable<OutdoorPvPZoneScript> Scripts => _scripts.Values;

    public EasternPlaguelandsZone? EasternPlaguelands => _scripts.Values.OfType<EasternPlaguelandsZone>().FirstOrDefault();

    public SilithusZone? Silithus => _scripts.Values.OfType<SilithusZone>().FirstOrDefault();

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        _host = new OutdoorPvPWorldHost(this);
        WorldStateHooks.For(world).WorldStates.Add(this);
        world.Post(Install);
        world.WorldTick += OnWorldTick;
        world.PlayerLoggingOut += OnPlayerLoggingOut;
        world.MapCreated += OnMapCreated;
        world.MapUnloading += OnMapUnloading;
    }

    private void Install()
    {
        if (Spells is { } spells)
        {
            spells.HolderAdded += OnHolderAdded;
            spells.HolderRemoved += OnHolderRemoved;
        }

        services.GetService<GraveyardFeature>()?.Service?.AddLinkSource(this);
        foreach (Map map in World.Maps.ToArray())
        {
            OnMapCreated(map);
        }
    }

    private void OnMapCreated(Map map)
    {
        if (!Invariant.CheckNotNull(map, nameof(map)))
        {
            return;
        }

        if (map.InstanceId != 0 || _scripts.ContainsKey(map) || _host is null)
        {
            return;
        }

        OutdoorPvPZoneScript? script = map.MapId switch
        {
            EasternPlaguelandsCatalog.MapId => new EasternPlaguelandsZone(_host),
            SilithusCatalog.MapId => new SilithusZone(_host, LoadSilithystMax()) { AreaTriggerPosition = TriggerPosition, Saved = SaveSilithyst },
            _ => null,
        };
        if (script is null)
        {
            return;
        }

        _scripts[map] = script;
        try
        {
            script.Setup();
            logger.LogInformation("outdoor PvP: {Script} running on map {Map} ({Points} capture point(s))", script.GetType().Name, map.MapId, script.CapturePoints.Count);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _scripts.Remove(map);
            FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
            AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"map {map.MapId}", $"setup error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault("outdoor-pvp.setup", ex, "outdoor PvP setup failed on map {Map}", map.MapId);
        }
    }

    private void OnMapUnloading(Map map)
    {
        if (!Invariant.CheckNotNull(map, nameof(map)))
        {
            return;
        }

        if (!_scripts.Remove(map, out OutdoorPvPZoneScript? script))
        {
            return;
        }

        foreach (ObjectGuid guid in _present.Where(p => ReferenceEquals(p.Value.Script, script)).Select(p => p.Key).ToArray())
        {
            _present.Remove(guid);
        }
    }

    private (float X, float Y, float Z)? TriggerPosition(uint triggerId)
        => WorldMaps.Of(World).FindAreaTrigger(triggerId) is { } at ? (at.X, at.Y, at.Z) : null;

    internal OutdoorPvPZoneScript? ScriptFor(uint mapId, uint zoneId)
        => _scripts.FirstOrDefault(p => p.Key.MapId == mapId && p.Key.InstanceId == 0 && p.Value.Zones.Contains(zoneId)).Value;

    internal Map? MapOf(OutdoorPvPZoneScript script) => _scripts.FirstOrDefault(p => ReferenceEquals(p.Value, script)).Key;

    /// <summary>vmangos SetupZoneScript: <c>GetSavedVariable(WS_OPVP_SI_SILITHYST_MAX, SI_MAX_RESOURCES_DEFAULT)</c>.</summary>
    private uint LoadSilithystMax()
    {
        try
        {
            using IServiceScope scope = services.CreateScope();
            if (scope.ServiceProvider.GetService<ISilithystStore>()?.LoadAsync().GetAwaiter().GetResult() is { MaxResources: > 0 } saved)
            {
                return saved.MaxResources;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
            AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", "silithyst-load", $"error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault("outdoor-pvp.silithyst-load", ex, "could not load the Silithyst state");
        }

        return SilithusCatalog.DefaultMaxResources;
    }

    private StateSaveQueue<int, SilithystState>? _silithystSaves;

    /// <summary>The Silithyst counters' writer: off the world thread (a turn-in saved on it).</summary>
    internal StateSaveQueue<int, SilithystState> SilithystSaves => _silithystSaves ??= new(async state =>
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<ISilithystStore>() is { } store) await store.SaveAsync(state).ConfigureAwait(false);
    }, logger, "Silithyst");

    private void SaveSilithyst(uint alliance, uint horde, uint max) => SilithystSaves.Save(0, new SilithystState(alliance, horde, max));

    /// <summary>Writes every queued Silithyst state before shutdown completes.</summary>
    public Task StopAsync() => _silithystSaves?.FlushAsync() ?? Task.CompletedTask;

    private void OnWorldTick(uint diffMs)
    {
        foreach (OutdoorPvPZoneScript script in _scripts.Values.ToArray())
        {
            try
            {
                script.Tick(diffMs);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
                AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"script {script.GetType().Name}", $"tick error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("outdoor-pvp.tick", ex, "outdoor PvP script {Script} tick failed", script.GetType().Name);
            }
        }

        try
        {
            _host?.UpdateCreatureGroups();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
            AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", "creature-groups", $"error={ex.GetType().Name}: {ex.Message}");
            logger.LogFault("outdoor-pvp.creature-groups", ex, "outdoor PvP creature group update failed");
        }
    }

    // ------------------------------------------------------------------ zone presence

    public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
    {
        if (!Invariant.CheckNotNull(player, nameof(player)) || !Invariant.Check(!player.Guid.IsEmpty, "OutdoorPvPFeature.OnZoneChanged: player must have a valid GUID"))
        {
            return;
        }

        OutdoorPvPZoneScript? next = player.Map is { InstanceId: 0 } ? ScriptFor(player.MapId, newZone) : null;
        if (_present.TryGetValue(player.Guid, out var current))
        {
            // Moving between zones of one script (Eastern Plaguelands into Stratholme) keeps the buffs and the states the zone entry
            // just re-sent; vmangos leaves and re-enters, which ends in the same place.
            if (ReferenceEquals(current.Script, next) && current.Team == player.Team)
            {
                return;
            }

            _present.Remove(player.Guid);
            try
            {
                current.Script.OnPlayerLeave(new OutdoorPvPPlayer(player.Guid, current.Team), loggingOut: false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
                AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"player {player.Guid}", $"zone-leave error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("outdoor-pvp.zone-leave", ex, "outdoor PvP leave failed for {Player}", player.Guid);
            }
        }

        if (next is not null)
        {
            _present[player.Guid] = (next, player.Team);
            try
            {
                next.OnPlayerEnter(new OutdoorPvPPlayer(player.Guid, player.Team));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
                AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"player {player.Guid}", $"zone-enter error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("outdoor-pvp.zone-enter", ex, "outdoor PvP enter failed for {Player}", player.Guid);
            }
        }
    }

    private void OnPlayerLoggingOut(Player player)
    {
        if (!Invariant.CheckNotNull(player, nameof(player)) || !Invariant.Check(!player.Guid.IsEmpty, "OutdoorPvPFeature.OnPlayerLoggingOut: player must have a valid GUID"))
        {
            return;
        }

        _flagRemovedAt.Remove(player.Guid);
        if (_present.Remove(player.Guid, out var current))
        {
            try
            {
                current.Script.OnPlayerLeave(new OutdoorPvPPlayer(player.Guid, current.Team), loggingOut: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
                AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"player {player.Guid}", $"logout error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("outdoor-pvp.logout", ex, "outdoor PvP logout leave failed for {Player}", player.Guid);
            }
        }
    }

    // ------------------------------------------------------------------ world states

    /// <summary>vmangos Player::SendInitWorldStates → <c>ZoneScript::FillInitialWorldStates</c> for the zone's script.</summary>
    public void Fill(Player player, uint zoneId, List<WorldStatePair> states)
    {
        if (!Invariant.CheckNotNull(player, nameof(player)) || !Invariant.CheckNotNull(states, nameof(states)))
        {
            return;
        }

        if (player.Map is { InstanceId: 0 } && ScriptFor(player.MapId, zoneId) is { } script)
        {
            try
            {
                script.FillInitialWorldStates(states);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
                AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"zone {zoneId}", $"worldstates error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("outdoor-pvp.worldstates", ex, "outdoor PvP FillInitialWorldStates failed for zone {Zone}", zoneId);
            }
        }
    }

    // ------------------------------------------------------------------ area triggers and the Silithyst flag

    /// <summary>vmangos HandleAreaTriggerOpcode → <c>ZoneScript::HandleAreaTrigger</c> of the player's zone script.</summary>
    public void OnAreaTrigger(Player player, uint triggerId)
    {
        if (!Invariant.CheckNotNull(player, nameof(player)) || !Invariant.Check(!player.Guid.IsEmpty, "OutdoorPvPFeature.OnAreaTrigger: player must have a valid GUID"))
        {
            return;
        }

        if (_present.TryGetValue(player.Guid, out var current))
        {
            try
            {
                current.Script.HandleAreaTrigger(new OutdoorPvPPlayer(player.Guid, current.Team), triggerId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
                AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"player {player.Guid}", $"areatrigger={triggerId} error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("outdoor-pvp.areatrigger", ex, "outdoor PvP area trigger {Trigger} failed for {Player}", triggerId, player.Guid);
            }
        }
    }

    private void OnHolderRemoved(SpellAuraHolder holder)
    {
        if (holder.Target is Player player && holder.Spell.Id == SilithusCatalog.SpellSilithystFlag)
        {
            _flagRemovedAt[player.Guid] = World.NowMs;

            // vmangos SilithystAuraScript::OnAfterApply (spell_special.cpp:371-380): losing the aura in Silithus to a cancel, death or
            // dispel runs HandleDropFlag; a turn-in, expiry, stack or mount/stealth strip (Default) does not, so no second mound.
            if (player.ZoneId == SilithusCatalog.SilithusZone
                && holder.RemoveMode is AuraRemoveMode.Cancel or AuraRemoveMode.Death or AuraRemoveMode.Dispel)
            {
                DropFlag(player);
            }
        }
    }

    /// <summary>
    /// Mounting or stealthing in Silithus with the flag drops it. The stealth handler strips the flag (STEALTH_INVIS_CANCELS) before this
    /// event, so a flag that went in the same world tick counts as held.
    /// </summary>
    private void OnHolderAdded(SpellAuraHolder holder)
    {
        if (holder.Target is not Player player || player.ZoneId != SilithusCatalog.SilithusZone
            || !(holder.Spell.HasAura(AuraType.ModStealth) || holder.Spell.HasAura(AuraType.Mounted)))
        {
            return;
        }

        bool held = Spells?.HasAura(player, SilithusCatalog.SpellSilithystFlag) == true
            || (_flagRemovedAt.TryGetValue(player.Guid, out uint at) && at == World.NowMs);
        if (held)
        {
            DropFlag(player);
        }
    }

    private void DropFlag(Player player)
    {
        if (_present.TryGetValue(player.Guid, out var current))
        {
            try
            {
                current.Script.HandleDropFlag(new OutdoorPvPPlayer(player.Guid, current.Team), SilithusCatalog.SpellSilithystFlag);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                FaultLedger.Shared.Record("outdoor-pvp.feature", ex);
                AnomalyLedger.Shared.Report("outdoor-pvp", "hook-fault", $"player {player.Guid}", $"drop-flag error={ex.GetType().Name}: {ex.Message}");
                logger.LogFault("outdoor-pvp.drop-flag", ex, "outdoor PvP drop flag failed for {Player}", player.Guid);
            }
        }
    }

    // ------------------------------------------------------------------ graveyard

    /// <summary>The Crown Guard graveyard while the tower's team holds it (vmangos LinkGraveYard: zone 139 and The Fungal Vale).</summary>
    public IEnumerable<uint> ExtraLinks(Player player, uint zoneId, uint areaId)
    {
        if (player.MapId == EasternPlaguelandsCatalog.MapId && (zoneId == EasternPlaguelandsCatalog.ZoneId || areaId == EasternPlaguelandsCatalog.FungalValeArea)
            && EasternPlaguelands?.GraveyardTeam == player.Team)
        {
            yield return EasternPlaguelandsCatalog.GraveyardId;
        }
    }

    // ------------------------------------------------------------------ outdoor PvP activity

    /// <summary>
    /// vmangos <c>Player::IsOutdoorPvPActive</c> (Player.cpp:6720-6724): alive, not a GM, neither invisible nor stealthed, PvP desired
    /// or on a PvP realm, and not on a taxi.
    /// </summary>
    internal bool IsOutdoorPvPActive(Player player)
    {
        if (!player.IsAlive || player.IsGameMaster || (player.UnitFlags & UnitFlags.TaxiFlight) != 0)
        {
            return false;
        }

        bool pvp = (player.Flags & PlayerFlags.PvpDesired) != 0 || WorldStateHooks.For(World).Zones.PvpRealmMode != PvpRealmMode.Normal;
        if (!pvp)
        {
            return false;
        }

        return Spells is not { } spells
            || !spells.GetAuras(player).Any(h => !h.IsRemoved && (h.HasAura(AuraType.ModStealth) || h.HasAura(AuraType.ModInvisibility)));
    }
}
