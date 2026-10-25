using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Graveyards;

/// <summary>Where a chosen graveyard came from.</summary>
public enum GraveyardSource
{
    /// <summary>A <c>game_graveyard_zone</c> link of the spirit's area or zone.</summary>
    Link,

    /// <summary>The battleground's own choice (<see cref="IGraveyardOverride"/>).</summary>
    Battleground,

    /// <summary>The default graveyard of the team (<c>World:Death:GraveyardFallbackToDefaults</c>), no link existed.</summary>
    Default,

    /// <summary>A run-time link (<see cref="IGraveyardLinkSource"/>, e.g. an outdoor PvP tower) closer than the table's choice.</summary>
    RunTimeLink,
}

/// <summary>A graveyard and where it came from.</summary>
public sealed record GraveyardChoice(WorldSafeLoc Location, GraveyardSource Source);

/// <summary>
/// Sends a released spirit to its graveyard (vmangos <c>Player::RepopAtGraveyard</c>, Player.cpp:4988-5025): the closest
/// linked graveyard (<see cref="GraveyardSelector"/>), a spirit on a transport comes back alive at once, the facing is the
/// safe location's, and the trip is a teleport (the ghost state lives on the player, so it survives a far one). No linked
/// graveyard leaves the ghost where it is. Registered as the world's <see cref="IGraveyardRepop"/> by the graveyard feature.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed partial class GraveyardRepopService(WorldRuntime world, Func<TeleportService?> teleports) : IGraveyardRepop
{
    private readonly List<IGraveyardOverride> _overrides = [];
    private readonly List<IGraveyardLinkSource> _linkSources = [];

    /// <summary>Add an <see cref="IGraveyardOverride"/> (battlegrounds), asked in registration order.</summary>
    public void AddOverride(IGraveyardOverride graveyardOverride)
    {
        ArgumentNullException.ThrowIfNull(graveyardOverride);
        _overrides.Add(graveyardOverride);
    }

    /// <summary>Add an <see cref="IGraveyardLinkSource"/> (outdoor PvP run-time links).</summary>
    public void AddLinkSource(IGraveyardLinkSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _linkSources.Add(source);
    }

    /// <inheritdoc />
    public bool RepopAtGraveyard(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map is not { } map)
        {
            return false;
        }

        WorldSafeLoc? graveyard = Choose(player, map);
        if (graveyard is null)
        {
            player.LastReleasedGraveyard = null;
            // "Fix invisible spirit healer if you die close to graveyard" (Player.cpp:5021-5022).
            MarkVisibility(player);
            return false;
        }

        // vmangos Player.cpp:5010-5016 tests GetTransport(); the movement flag is kept as well, which is how this base
        // recognised a spirit on a transport before ships existed.
        if (!player.IsAlive && (player.Transport is not null || (player.Movement.Flags & MovementFlags.OnTransport) != 0))
        {
            player.Transport?.RemovePassenger(player);
            map.Combat.ResurrectFromTransport(player);
        }

        bool moved = teleports() is { } service
            && service.TeleportTo(player, graveyard.MapId, graveyard.X, graveyard.Y, graveyard.Z, graveyard.Orientation);
        player.LastReleasedGraveyard = moved ? graveyard : null;
        MarkVisibility(player);
        return moved;
    }

    /// <inheritdoc />
    public bool RelocateLeavingPlayer(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.Map is not { } map || Choose(player, map) is not { } graveyard)
        {
            return false;
        }

        player.LogoutLocation = (graveyard.MapId, graveyard.X, graveyard.Y, graveyard.Z, graveyard.Orientation);
        return true;
    }

    /// <inheritdoc />
    public bool TeleportToCorpseGraveyard(Player player, CorpsePlace? corpse)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (corpse is not { } place || player.Map is not { } map)
        {
            MarkVisibility(player);
            return false;
        }

        // The corpse's graveyard is chosen from the corpse's own map and position; the ghost's from where it stands now.
        // vmangos asks sTerrainMgr.GetZoneAndAreaId (ObjectMgr.cpp:7514). GetMap would create instance 0 of that map —
        // and every default updater — only to read the zone, including a dungeon the ghost has already left.
        (uint corpseZone, uint corpseArea) = place.MapId == map.MapId
            ? map.GetZoneAndAreaId(place.X, place.Y, place.Z)
            : WorldMaps.Of(world).Terrain.For(place.MapId).GetZoneAndAreaId(place.X, place.Y, place.Z);
        WorldSafeLoc? corpseGrave = ChooseAt(player, place.MapId, place.X, place.Y, place.Z, corpseZone, corpseArea);
        WorldSafeLoc? ghostGrave = Choose(player, map);
        if (corpseGrave is null || corpseGrave.Id == ghostGrave?.Id)
        {
            MarkVisibility(player);
            return false;
        }

        // "if (float facing = GetWorldSafeLocFacing(...)) orientation = facing": no stored facing keeps the player's own.
        float orientation = corpseGrave.Orientation != 0f ? corpseGrave.Orientation : player.Orientation;
        bool moved = teleports() is { } service
            && service.TeleportTo(player, corpseGrave.MapId, corpseGrave.X, corpseGrave.Y, corpseGrave.Z, orientation);
        if (!moved)
        {
            MarkVisibility(player);
        }

        return moved;
    }

    /// <summary>The safe location for <paramref name="player"/> at its position, or null.</summary>
    internal WorldSafeLoc? Choose(Player player, Map map) => ChooseAt(player, map, player.X, player.Y, player.Z);

    /// <summary>
    /// The graveyard a spirit at <paramref name="player"/>'s position would be sent to, as a GM query (<c>.neargrave</c>), with where
    /// it came from. It is the real repop's selection (battleground overrides, zone and area links, the default fallback, run-time
    /// links). <paramref name="team"/> (469 Alliance, 67 Horde, 0 any) replaces the player's own for the zone and area links and the
    /// default fallback only: <see cref="IGraveyardOverride"/> (the player's match and team) and <see cref="IGraveyardLinkSource"/>
    /// (the player's team) take the player, so on a battleground map and for run-time links the player's own team still applies.
    /// </summary>
    public GraveyardChoice? ChooseNearest(Player player, Map map, uint team)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(map);
        (uint zoneId, uint areaId) = map.GetZoneAndAreaId(player.X, player.Y, player.Z);
        return ChooseCore(player, map.MapId, player.X, player.Y, player.Z, zoneId, areaId, team);
    }

    private WorldSafeLoc? ChooseAt(Player player, Map map, float x, float y, float z)
    {
        (uint zoneId, uint areaId) = map.GetZoneAndAreaId(x, y, z);
        return ChooseAt(player, map.MapId, x, y, z, zoneId, areaId);
    }

    private WorldSafeLoc? ChooseAt(Player player, uint mapId, float x, float y, float z, uint zoneId, uint areaId)
        => ChooseCore(player, mapId, x, y, z, zoneId, areaId, teamOverride: null)?.Location;

    private GraveyardChoice? ChooseCore(Player player, uint mapId, float x, float y, float z, uint zoneId, uint areaId, uint? teamOverride)
    {
        WorldMaps maps = WorldMaps.Of(world);
        if (maps.Registry.Find(mapId) is { IsBattleground: true })
        {
            foreach (IGraveyardOverride graveyardOverride in _overrides)
            {
                if (graveyardOverride.TryChoose(player, out WorldSafeLoc? chosen))
                {
                    return chosen is null ? null : new GraveyardChoice(chosen, GraveyardSource.Battleground);
                }
            }

            return null; // a battleground without its own graveyards: the spirit stays
        }

        GraveyardCatalog catalog = WorldGraveyards.Of(world).Catalog;
        if (catalog.SafeLocCount == 0)
        {
            return null;
        }

        uint ownTeam = player.Team == Team.Alliance ? GraveyardCatalog.TeamAlliance : GraveyardCatalog.TeamHorde;
        uint team = teamOverride ?? ownTeam;
        WorldSafeLoc? picked = GraveyardSelector.FindClosest(catalog, maps.Registry, mapId, x, y, z, zoneId, areaId, team);
        GraveyardSource source = GraveyardSource.Link;
        if (picked is null && DeathHooks.For(world).Options.GraveyardFallbackToDefaults)
        {
            // The default graveyards are per team; "any team" (a GM query only) falls back to the player's own.
            picked = GraveyardSelector.FindClosestOrDefault(catalog, maps.Registry, mapId, x, y, z, zoneId, areaId, team == 0 ? ownTeam : team);
            source = GraveyardSource.Default;
        }

        WorldSafeLoc? final = _linkSources.Count == 0 ? picked : WithExtraLinks(picked, catalog, player, mapId, x, y, z, zoneId, areaId);
        if (final is null)
        {
            return null;
        }

        return new GraveyardChoice(final, ReferenceEquals(final, picked) ? source : GraveyardSource.RunTimeLink);
    }

    /// <summary>The closest of the chosen graveyard and the run-time links on the spirit's map (vmangos GetClosestGraveYard's distance pick).</summary>
    private WorldSafeLoc? WithExtraLinks(WorldSafeLoc? chosen, GraveyardCatalog catalog, Player player, uint mapId, float x, float y, float z, uint zoneId, uint areaId)
    {
        float best = chosen is not null && chosen.MapId == mapId ? DistanceSquared(chosen, x, y, z) : float.MaxValue;
        foreach (IGraveyardLinkSource source in _linkSources)
        {
            foreach (uint id in source.ExtraLinks(player, zoneId, areaId))
            {
                if (catalog.Find(id) is { } loc && loc.MapId == mapId && DistanceSquared(loc, x, y, z) is var d && d < best)
                {
                    best = d;
                    chosen = loc;
                }
            }
        }

        return chosen;
    }

    private static float DistanceSquared(WorldSafeLoc loc, float x, float y, float z)
        => ((loc.X - x) * (loc.X - x)) + ((loc.Y - y) * (loc.Y - y)) + ((loc.Z - z) * (loc.Z - z));

    private static void MarkVisibility(Player player)
    {
        if (!player.IsAlive)
        {
            player.NeedsVisibilityUpdate = true;
        }
    }
}
