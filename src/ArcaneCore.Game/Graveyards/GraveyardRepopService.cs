using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Teleport;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Graveyards;

/// <summary>
/// Sends a released spirit to its graveyard (vmangos <c>Player::RepopAtGraveyard</c>, Player.cpp:4988-5025): the closest
/// linked graveyard (<see cref="GraveyardSelector"/>), a spirit on a transport comes back alive at once, the facing is the
/// safe location's, and the trip is a teleport (the ghost state lives on the player, so it survives a far one). No linked
/// graveyard leaves the ghost where it is. Registered as the world's <see cref="IGraveyardRepop"/> by the graveyard feature.
/// <para>Thread affinity: world thread.</para>
/// </summary>
public sealed class GraveyardRepopService(WorldRuntime world, Func<TeleportService?> teleports) : IGraveyardRepop
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
        Map corpseMap = place.MapId == map.MapId ? map : world.GetMap(place.MapId);
        WorldSafeLoc? corpseGrave = ChooseAt(player, corpseMap, place.X, place.Y, place.Z);
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

    private WorldSafeLoc? ChooseAt(Player player, Map map, float x, float y, float z)
    {
        WorldMaps maps = WorldMaps.Of(world);
        if (maps.Registry.Find(map.MapId) is { IsBattleground: true })
        {
            foreach (IGraveyardOverride graveyardOverride in _overrides)
            {
                if (graveyardOverride.TryChoose(player, out WorldSafeLoc? chosen))
                {
                    return chosen;
                }
            }

            return null; // a battleground without its own graveyards: the spirit stays
        }

        GraveyardCatalog catalog = WorldGraveyards.Of(world).Catalog;
        if (catalog.SafeLocCount == 0)
        {
            return null;
        }

        (uint zoneId, uint areaId) = map.GetZoneAndAreaId(x, y, z);
        uint team = player.Team == Team.Alliance ? GraveyardCatalog.TeamAlliance : GraveyardCatalog.TeamHorde;
        WorldSafeLoc? picked = DeathHooks.For(world).Options.GraveyardFallbackToDefaults
            ? GraveyardSelector.FindClosestOrDefault(catalog, maps.Registry, map.MapId, x, y, z, zoneId, areaId, team)
            : GraveyardSelector.FindClosest(catalog, maps.Registry, map.MapId, x, y, z, zoneId, areaId, team);
        return _linkSources.Count == 0 ? picked : WithExtraLinks(picked, catalog, player, map.MapId, x, y, z, zoneId, areaId);
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
