using System.Runtime.CompilerServices;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Teleport;

namespace ArcaneCore.Game.Death;

/// <summary>A single in-memory resurrection offer. It belongs to this player object and this death.</summary>
public sealed record ResurrectionRequest(
    ObjectGuid Caster, TeleportDestination Destination, uint InstanceId, uint Health, uint Mana, bool Relocate)
{
    public bool Accepted { get; internal set; }
}

/// <summary>
/// World-thread resurrection request state (vmangos Player::SetResurrectRequestData). Offers are
/// deliberately not persisted: disconnecting, dying again, or revival through another path clears them.
/// A weak player key prevents a discarded login/session from retaining its offer.
/// </summary>
public static class PlayerResurrection
{
    private static readonly ConditionalWeakTable<Player, ResurrectionRequest> Requests = new();

    public static ResurrectionRequest? GetRequest(Player player) => Requests.TryGetValue(player, out ResurrectionRequest? request) ? request : null;

    public static bool TryOffer(Player player, ResurrectionRequest request)
    {
        if (player.Combat.DeathState == DeathState.Alive || !player.IsInWorld || player.IsQuestSettlementPending || request.Caster.IsEmpty || Requests.TryGetValue(player, out _))
        {
            return false;
        }

        Requests.Add(player, request);
        return true;
    }

    public static ResurrectionRequest? TryAccept(Player player, ObjectGuid resurrector)
    {
        ResurrectionRequest? request = GetRequest(player);
        if (player.Combat.DeathState == DeathState.Alive || !player.IsInWorld || player.IsQuestSettlementPending
            || request is null || request.Caster != resurrector || request.Accepted)
        {
            return null;
        }

        request.Accepted = true;
        return request;
    }

    public static void Clear(Player player) => Requests.Remove(player);
}
