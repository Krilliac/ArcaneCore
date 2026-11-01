using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Teleport;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// Puts a managed bot on a floor when it logs in over a void. A bot is saved wherever it stands, and a position with no floor under it
/// (no navigation polygon and no model floor near its height, the terrain surface far above: Ironforge x -4740..-4600, y -1040..-880 at
/// z about 501) would otherwise be logged in at again and again, walking on a stale height until its next save. Bots only: a real
/// player keeps the vmangos login (the saved position, whatever it is).
/// <para>
/// The test is the bot-motion ground check itself (<see cref="PlayerbotMotionAnomalies.JudgeGround"/> over
/// <see cref="PlayerbotMotionAnomalies.FloorUnder"/>): a bot that would be convicted of airwalk or under-map where it stands is moved.
/// The target is the best navigation-mesh floor (distance plus a height penalty, a drop costing twice a rise) within <see cref="SearchYards"/> of the saved point (rings of
/// <see cref="RingStepYards"/> yards, <see cref="Bearings"/> bearings each, the surface between <see cref="StoreyBelowYards"/> below and
/// <see cref="StoreyAboveYards"/> above the saved height, itself passing the ground check), else the character's bind point when it is
/// on the same map, else the bot stays where it is. Unloaded data never convicts, so a map without terrain or models changes nothing.
/// </para>
/// Thread affinity: world thread, after the bot joined its map (the grids around it, and with them the mesh tiles, are loaded then).
/// The queries are read-only; the only write is one move of the bot (<see cref="Place"/>). Cost: the
/// ground check once, and only for a convicted bot up to <c>(SearchYards / RingStepYards) * Bearings</c> mesh floor queries.
/// </summary>
internal static class PlayerbotLoginFloor
{
    /// <summary>The farthest the nearest mesh floor may lie from the saved point (flat yards).</summary>
    internal const float SearchYards = 30f;

    /// <summary>Distance between two sampled rings.</summary>
    internal const float RingStepYards = 2f;

    /// <summary>Samples per ring.</summary>
    internal const int Bearings = 16;

    /// <summary>How far under the saved height a mesh floor may lie and still be taken.</summary>
    internal const float StoreyBelowYards = 10f;

    /// <summary>How far over the saved height a mesh floor may lie and still be taken.</summary>
    internal const float StoreyAboveYards = 6f;

    internal enum Outcome
    {
        /// <summary>The bot stands where the ground check accepts it (or the check cannot judge): untouched.</summary>
        Kept,

        /// <summary>Moved to the nearest mesh floor.</summary>
        MovedToMesh,

        /// <summary>No mesh floor in reach: moved to the bind point.</summary>
        MovedToHome,

        /// <summary>No floor and no usable bind point: untouched.</summary>
        NoFloorFound,
    }

    internal readonly record struct Result(Outcome Outcome, Vector3 Position);

    /// <summary>The mesh floor query (<see cref="MapCollision.TryGetMeshFloor"/>): a polygon surface in the window under or over a point.</summary>
    internal delegate bool MeshFloorQuery(float x, float y, float z, float below, float above, out float height);

    /// <summary>
    /// The pure decision. <paramref name="standsOk"/> says whether a bot at a point passes the ground check; <paramref name="meshFloor"/>
    /// finds a mesh surface; <paramref name="home"/> is the bind point when it is on this map. Any thread.
    /// </summary>
    internal static Result Resolve(Vector3 at, Func<Vector3, bool> standsOk, MeshFloorQuery meshFloor, Vector3? home)
    {
        ArgumentNullException.ThrowIfNull(standsOk);
        ArgumentNullException.ThrowIfNull(meshFloor);
        if (standsOk(at)) return new(Outcome.Kept, at);

        bool found = false;
        Vector3 best = default;
        float bestScore = float.MaxValue;
        for (float radius = 0f; radius <= SearchYards; radius += RingStepYards)
        {
            int bearings = radius == 0f ? 1 : Bearings;
            for (int i = 0; i < bearings; i++)
            {
                float angle = MathF.Tau * i / bearings;
                float x = at.X + (MathF.Cos(angle) * radius), y = at.Y + (MathF.Sin(angle) * radius);
                if (!meshFloor(x, y, at.Z, StoreyBelowYards, StoreyAboveYards, out float z) || !float.IsFinite(z)) continue;
                // Distance, plus a height penalty: a floor under the saved height is another storey more often than one at or over it
                // (Ironforge's layers), so a drop costs twice its yards and a rise half.
                float score = radius + (z < at.Z ? 2f * (at.Z - z) : 0.5f * (z - at.Z));
                if (score >= bestScore) continue;
                var candidate = new Vector3(x, y, z);
                if (!standsOk(candidate)) continue;
                best = candidate;
                bestScore = score;
                found = true;
            }
        }

        if (found) return new(Outcome.MovedToMesh, best);

        return home is { } bind && float.IsFinite(bind.X) && float.IsFinite(bind.Y) && float.IsFinite(bind.Z)
            ? new(Outcome.MovedToHome, bind)
            : new(Outcome.NoFloorFound, at);
    }

    /// <summary>
    /// Settle <paramref name="player"/> (a managed bot that just joined <paramref name="map"/>) on a floor, moving it when
    /// <see cref="Resolve"/> says so. World thread.
    /// </summary>
    internal static Result Settle(Player player, Map map, uint nowMs, TeleportService? teleports)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(map);
        var at = new Vector3(player.X, player.Y, player.Z);
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)
            || (player.Movement.Flags & PlayerbotMotionAnomalies.NotWalking) != 0 || player.Locomotion.ActiveSpline is not null)
        {
            return new(Outcome.Kept, at);
        }

        MapCollision collision = map.Collision;
        float height = player.Locomotion.CollisionHeight;
        bool StandsOk(Vector3 point)
        {
            return PlayerbotMotionAnomalies.StandsOnFloor(collision, point) || PlayerbotWater.Submerged(map, point, height);
        }

        HomeBindOnMap(player, map, out Vector3? home);
        Result result = Resolve(at, StandsOk, collision.TryGetMeshFloor, home);
        if (result.Outcome is Outcome.MovedToMesh or Outcome.MovedToHome)
        {
            Place(player, map, result.Position, nowMs, teleports);
        }

        return result;
    }

    /// <summary>
    /// Move the bot the way every other same-map move is done, so its movement state (the create block sends it) and the players who
    /// already see it follow: a near teleport (observers get the teleport and the new position) acknowledged for the bot at once. Without
    /// a teleport service, or when it refuses, <see cref="Unit.Relocate(float, float, float, float, uint)"/> and a visibility pass.
    /// </summary>
    private static void Place(Player player, Map map, Vector3 to, uint nowMs, TeleportService? teleports)
    {
        float facing = player.Orientation;
        if (teleports is not null && teleports.TeleportTo(player, map.MapId, to.X, to.Y, to.Z, facing)
            && teleports.HandleTeleportAck(player, player.Guid.Value))
        {
            return;
        }

        player.Relocate(to.X, to.Y, to.Z, facing, nowMs);
        player.NeedsVisibilityUpdate = true;
    }

    private static void HomeBindOnMap(Player player, Map map, out Vector3? home)
    {
        var bind = player.Home;
        home = !bind.IsUnset && bind.MapId == map.MapId ? new Vector3(bind.X, bind.Y, bind.Z) : null;
    }
}
