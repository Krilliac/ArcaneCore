using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace ArcaneCore.World.Gm.Teleport;

/// <summary>A saved position a player can return to with <c>.recall</c>.</summary>
public readonly record struct RecallPosition(uint MapId, float X, float Y, float Z, float Orientation);

/// <summary>
/// vmangos <c>Player::SaveRecallPosition</c> / <c>GetRecallPosition</c> (D:\refs\vmangos\src\game\Objects\Player.cpp:5999-6006):
/// the position <c>.recall</c> returns to. It is set when the player enters the world (vmangos
/// LoadFromDB, Player.cpp:14881) and before every GM command teleport that does not start in
/// a taxi flight. World-thread state; not persisted (vmangos keeps it in memory too).
/// </summary>
public sealed class RecallPositions : IWorldFeature
{
    private readonly ConditionalWeakTable<Player, StrongBox<RecallPosition>> _positions = [];

    public void Attach(WorldRuntime world) => world.PlayerLoggedIn += Save;

    public void Save(Player player) => _positions.AddOrUpdate(player, new StrongBox<RecallPosition>(
        new RecallPosition(player.MapId, player.X, player.Y, player.Z, player.Orientation)));

    public bool TryGet(Player player, out RecallPosition position)
    {
        if (_positions.TryGetValue(player, out StrongBox<RecallPosition>? box))
        {
            position = box.Value;
            return true;
        }

        position = default;
        return false;
    }
}

/// <summary>Shared steps of the GM teleport commands.</summary>
public static class GmTeleports
{
    /// <summary>
    /// The start of vmangos HandleGoHelper / HandleNamegoCommand / HandleGonameCommand for
    /// <paramref name="subject"/>: a player in a taxi flight is taken out of it
    /// (<c>MovementExpired</c> + <c>ClearTaxiDestinations</c>), otherwise the current position is
    /// remembered for <c>.recall</c> (saving it mid-flight would "recall and fall from the sky").
    /// </summary>
    public static void BeginCommandTeleport(CommandContext context, Player subject)
    {
        IServiceProvider services = context.Session.Services;
        if (services.GetService<NpcServicesFeature>()?.Flights is { } flights && flights.IsFlying(subject))
        {
            flights.Abort(subject);
        }
        else
        {
            services.GetService<RecallPositions>()?.Save(subject);
        }
    }
}
