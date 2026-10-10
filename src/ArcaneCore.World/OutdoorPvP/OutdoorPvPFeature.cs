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
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.Features;
using ArcaneCore.World.Graveyards;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.OutdoorPvP;

/// <summary>
/// Outdoor PvP (vmangos <c>ZoneScriptMgr</c> + src/game/OutdoorPvP/, cross-checked with MaNGOS Zero <c>OutdoorPvPMgr</c>): one zone script
/// per continent map — the Eastern Plaguelands towers on map 0 and Silithyst on map 1 — fed by zone changes (vmangos
/// <c>HandlePlayerEnterZone/LeaveZone</c>, Player.cpp:6596-6598), logout (Player.cpp:2215), area triggers, the Silithyst flag drop on
/// mount or stealth (Unit.cpp:5802-5810, SpellAuras.cpp:3637-3645), the world tick and SMSG_INIT_WORLD_STATES. World thread.
/// </summary>
public sealed class OutdoorPvPFeature(IServiceProvider services, ILogger<OutdoorPvPFeature> logger)
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
        script.Setup();
        logger.LogInformation("outdoor PvP: {Script} running on map {Map} ({Points} capture point(s))", script.GetType().Name, map.MapId, script.CapturePoints.Count);
    }

    private void OnMapUnloading(Map map)
    {
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
        catch (Exception ex)
        {
            logger.LogError(ex, "could not load the Silithyst state");
        }

        return SilithusCatalog.DefaultMaxResources;
    }

    private void SaveSilithyst(uint alliance, uint horde, uint max)
    {
        try
        {
            using IServiceScope scope = services.CreateScope();
            scope.ServiceProvider.GetService<ISilithystStore>()?.SaveAsync(new SilithystState(alliance, horde, max)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "could not save the Silithyst state");
        }
    }

    private void OnWorldTick(uint diffMs)
    {
        foreach (OutdoorPvPZoneScript script in _scripts.Values.ToArray())
        {
            script.Tick(diffMs);
        }

        _host?.UpdateCreatureGroups();
    }

    // ------------------------------------------------------------------ zone presence

    public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
    {
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
            current.Script.OnPlayerLeave(new OutdoorPvPPlayer(player.Guid, current.Team), loggingOut: false);
        }

        if (next is not null)
        {
            _present[player.Guid] = (next, player.Team);
            next.OnPlayerEnter(new OutdoorPvPPlayer(player.Guid, player.Team));
        }
    }

    private void OnPlayerLoggingOut(Player player)
    {
        _flagRemovedAt.Remove(player.Guid);
        if (_present.Remove(player.Guid, out var current))
        {
            current.Script.OnPlayerLeave(new OutdoorPvPPlayer(player.Guid, current.Team), loggingOut: true);
        }
    }

    // ------------------------------------------------------------------ world states

    /// <summary>vmangos Player::SendInitWorldStates → <c>ZoneScript::FillInitialWorldStates</c> for the zone's script.</summary>
    public void Fill(Player player, uint zoneId, List<WorldStatePair> states)
    {
        if (player.Map is { InstanceId: 0 } && ScriptFor(player.MapId, zoneId) is { } script)
        {
            script.FillInitialWorldStates(states);
        }
    }

    // ------------------------------------------------------------------ area triggers and the Silithyst flag

    /// <summary>vmangos HandleAreaTriggerOpcode → <c>ZoneScript::HandleAreaTrigger</c> of the player's zone script.</summary>
    public void OnAreaTrigger(Player player, uint triggerId)
    {
        if (_present.TryGetValue(player.Guid, out var current))
        {
            current.Script.HandleAreaTrigger(new OutdoorPvPPlayer(player.Guid, current.Team), triggerId);
        }
    }

    private void OnHolderRemoved(SpellAuraHolder holder)
    {
        if (holder.Target is Player player && holder.Spell.Id == SilithusCatalog.SpellSilithystFlag)
        {
            _flagRemovedAt[player.Guid] = World.NowMs;
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
        if (held && _present.TryGetValue(player.Guid, out var current))
        {
            current.Script.HandleDropFlag(new OutdoorPvPPlayer(player.Guid, current.Team), SilithusCatalog.SpellSilithystFlag);
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
