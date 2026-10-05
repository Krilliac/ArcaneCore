using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Teleport;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Death;

/// <summary>
/// Player-origin resurrection requests relocate the spirit before bringing it back to life
/// (vmangos Player::ResurrectUsingRequestData). The existing teleport handshake owns movement;
/// completion runs after its acknowledgement on the world tick, and then saves the restored life.
/// Creature-origin requests restore the player at its current position.
/// </summary>
public sealed class PlayerResurrectionFeature(IServiceProvider services) : IWorldFeature, IDisposable
{
    private readonly Dictionary<Player, ResurrectionRequest> _accepted = new(ReferenceEqualityComparer.Instance);
    private WorldRuntime? _world;

    public void Attach(WorldRuntime world)
    {
        if (_world is not null)
        {
            throw new InvalidOperationException("the player resurrection feature is already attached");
        }

        _world = world;
        services.GetRequiredService<SpellFeature>().System.ResurrectionPlayers = world.FindOnlinePlayer;
        world.Updated += OnUpdated;
        world.PlayerLoggingOut += OnLoggingOut;
    }

    /// <summary>CMSG_RESURRECT_RESPONSE: decline clears any offer; acceptance must name its caster.</summary>
    public void Respond(Player player, ObjectGuid resurrector, bool accept)
    {
        if (_world is not { } world || player.IsAlive || player.IsQuestSettlementPending || player.Map is not { } map)
        {
            return;
        }

        if (!accept)
        {
            PlayerResurrection.Clear(player);
            _accepted.Remove(player);
            return;
        }

        ResurrectionRequest? request = PlayerResurrection.GetRequest(player);
        if (request is null || request.Caster != resurrector || request.Accepted
            || services.GetRequiredService<TeleportFeature>().Teleports.IsBeingTeleported(player))
        {
            return;
        }

        if (!request.Relocate)
        {
            if (PlayerResurrection.TryAccept(player, resurrector) is { } accepted && map.Combat.ResurrectFromRequest(player, accepted))
            {
                world.SavePlayer(player);
            }

            return;
        }

        // TeleportService resolves a different map through normal entry/binding checks, but its
        // same-map handshake cannot switch instances. Until it can address a specific instance,
        // refuse an offer from another instance; a later arrival is also checked against the offer.
        if (map.MapId == request.Destination.MapId && map.InstanceId != request.InstanceId)
        {
            return;
        }

        var teleports = services.GetRequiredService<TeleportFeature>().Teleports;
        var dest = request.Destination;
        if (!teleports.TeleportTo(player, dest.MapId, dest.X, dest.Y, dest.Z, dest.Orientation))
        {
            return;
        }

        if (PlayerResurrection.TryAccept(player, resurrector) is { } pending)
        {
            _accepted[player] = pending;
        }
    }

    private void OnUpdated(uint diff)
    {
        if (_world is not { } world || _accepted.Count == 0)
        {
            return;
        }

        var teleports = services.GetRequiredService<TeleportFeature>().Teleports;
        foreach ((Player player, ResurrectionRequest request) in _accepted.ToArray())
        {
            if (!ReferenceEquals(world.FindOnlinePlayer(player.Guid), player)
                || !ReferenceEquals(PlayerResurrection.GetRequest(player), request) || player.IsAlive)
            {
                _accepted.Remove(player);
                continue;
            }

            if (player.IsQuestSettlementPending || teleports.IsBeingTeleported(player))
            {
                continue;
            }

            _accepted.Remove(player);
            var dest = request.Destination;
            if (player.Map is not { } map || map.MapId != dest.MapId || map.InstanceId != request.InstanceId
                || MathF.Abs(player.X - dest.X) > 0.01f || MathF.Abs(player.Y - dest.Y) > 0.01f || MathF.Abs(player.Z - dest.Z) > 0.01f)
            {
                PlayerResurrection.Clear(player);
                continue; // cancelled, superseded, or redirected to another map by normal entry rules
            }

            if (map.Combat.ResurrectFromRequest(player, request))
            {
                world.SavePlayer(player);
            }
        }
    }

    private void OnLoggingOut(Player player)
    {
        _accepted.Remove(player);
        PlayerResurrection.Clear(player);
    }

    public void Dispose()
    {
        if (_world is { } world)
        {
            world.Updated -= OnUpdated;
            world.PlayerLoggingOut -= OnLoggingOut;
        }

        _accepted.Clear();
        _world = null;
    }
}
