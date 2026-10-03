using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Death.Resurrection;

/// <summary>
/// What a resurrection spell offered a dead player (vmangos <c>Player::m_resurrectData</c>, <c>SetResurrectRequestData</c>,
/// Player.cpp:2055-2066): who cast it, where (the player is teleported there when it accepts), and the health and mana it
/// comes back with.
/// </summary>
/// <param name="Resurrector">The caster (a player, or a creature such as a spirit healer).</param>
/// <param name="MapId">The caster's map.</param>
/// <param name="InstanceId">The caster's instance (0 outside an instance).</param>
/// <param name="Health">Health the player returns with (capped at its maximum).</param>
/// <param name="Mana">Mana the player returns with (capped at its maximum).</param>
public sealed record ResurrectionRequest(
    ObjectGuid Resurrector, uint MapId, uint InstanceId, float X, float Y, float Z, float Orientation, uint Health, uint Mana);

/// <summary>
/// The pending resurrection request of each player. Like vmangos, a request stays until the player dies again
/// (<c>SetDeathState(JUST_DIED)</c>, Player.cpp:1522) or declines it; it is not cleared by accepting it, so a second resurrection
/// cannot be offered to a player who still has one (<c>IsRessurectRequested</c>, SpellEffects.cpp:5242). In memory only: a logout
/// forgets it (vmangos keeps it on the Player object, which a relog replaces).
/// <para>Thread affinity: world thread.</para>
/// </summary>
public static class ResurrectionRequests
{
    private sealed class Slot
    {
        public ResurrectionRequest? Request;
    }

    private static readonly ConditionalWeakTable<Player, Slot> s_slots = new();

    /// <summary>The player's pending request, or null.</summary>
    public static ResurrectionRequest? Get(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return s_slots.TryGetValue(player, out Slot? slot) ? slot.Request : null;
    }

    /// <summary>Whether the player has a request (vmangos IsRessurectRequested).</summary>
    public static bool IsRequested(Player player) => Get(player) is not null;

    /// <summary>Whether the player has a request from <paramref name="guid"/> (vmangos IsRessurectRequestedBy).</summary>
    public static bool IsRequestedBy(Player player, ObjectGuid guid) => Get(player) is { } request && !guid.IsEmpty && request.Resurrector == guid;

    /// <summary>Store a request, replacing any (vmangos SetResurrectRequestData).</summary>
    public static void Set(Player player, ResurrectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(request);
        s_slots.GetOrCreateValue(player).Request = request;
    }

    /// <summary>Forget the player's request (vmangos ClearResurrectRequestData).</summary>
    public static void Clear(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (s_slots.TryGetValue(player, out Slot? slot))
        {
            slot.Request = null;
        }
    }
}
