using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.WorldState;

/// <summary>
/// The world daemon's zone and area feature (docs/areas/world-state.md): binds
/// <c>World:Zones</c>, registers every <see cref="IPlayerLocationListener"/> world feature with
/// <see cref="WorldStateHooks"/>, and is itself the first listener: a real zone change sends
/// SMSG_INIT_WORLD_STATES (vmangos <c>Player::UpdateZone</c> → <c>SendInitWorldStates</c>).
/// Login, far teleports and CMSG_ZONEUPDATE call <see cref="ForceUpdate"/> /
/// <see cref="HandleClientZone"/>. World thread.
/// </summary>
public sealed class ZoneAreaFeature(IServiceProvider services) : IWorldFeature, IPlayerLocationListener
{
    private WorldRuntime? _world;

    /// <summary>Runs before every other listener so weather and similar follow the world states.</summary>
    public int Order => int.MinValue;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _world = world;
        WorldStateHooks hooks = WorldStateHooks.For(world);
        IConfiguration? configuration = services.GetService<IConfiguration>();
        configuration?.GetSection(ZoneOptions.SectionName).Bind(hooks.Zones);
        // The game-time options live here too (this feature is the world-state option binder).
        configuration?.GetSection(TimeOptions.SectionName).Bind(hooks.TimeSettings);
        if (hooks.TimeSettings.UseServerLocalTime && hooks.TimeSettings.TimeZoneId.Length > 0)
        {
            // Fail closed at startup on an unknown zone id instead of at the first login.
            _ = TimeZoneInfo.FindSystemTimeZoneById(hooks.TimeSettings.TimeZoneId);
        }

        hooks.AddLocationListener(this);
        foreach (IPlayerLocationListener listener in services.GetServices<IWorldFeature>().OfType<IPlayerLocationListener>())
        {
            hooks.AddLocationListener(listener);
        }
    }

    public void OnZoneChanged(Player player, uint oldZone, uint newZone, uint newArea, AreaTemplate? zoneEntry)
        => player.Session.Send(WorldOpcode.SmsgInitWorldStates, LoginPackets.BuildInitWorldStates(player.MapId, newZone));

    /// <summary>
    /// vmangos <c>SendInitialPacketsAfterAddToMap</c>: derive the zone now and run the zone update
    /// (world states, weather and the other listeners). False when nothing could be derived.
    /// </summary>
    public bool ForceUpdate(Player player) => player.Map?.FindUpdater<ZoneAreaUpdater>()?.ForceUpdate(player) ?? false;

    /// <summary>
    /// CMSG_ZONEUPDATE. vmangos ignores the client's value and derives the zone from terrain
    /// (MiscHandler.cpp:381-386). While <see cref="WorldStateHooks.UsesClientZone"/> holds (no area
    /// data, or <c>ClientZoneTrust=Always</c>) the client's non-zero value is accepted instead.
    /// </summary>
    public void HandleClientZone(Player player, uint clientZone)
    {
        if (_world is not null && WorldStateHooks.For(_world).UsesClientZone && clientZone != 0)
        {
            player.ZoneId = clientZone;
        }

        ForceUpdate(player);
    }
}
