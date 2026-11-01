using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Playerbots;

/// <summary>
/// The crowd rule of the town vendor visit (<see cref="PlayerbotTownGoals"/>) for the controllers that walk to a service NPC in a line of their own: the bank, the flight
/// master visit, the quest-giver goal and the auction visit. Bots that arrive the same way stop at the same point of an NPC's reach and stood in one pile; a bot that comes
/// into reach with another bot on its place steps once (per NPC and <see cref="PlayerbotTownGoals.CrowdSpotMs"/>) to its own ring spot
/// (<see cref="PlayerbotStandSpots.ServiceSpot"/>, 2 to 4 yards from the NPC, inside the server's reach) and is then served from there.
/// Owned by one controller of one bot; world thread only. Allocation: one small dictionary per bot, a route only when a bot is actually crowded.
/// </summary>
internal sealed class PlayerbotCrowdSpread(WorldSession session, PlayerbotOptions options)
{
    private readonly PlayerbotTownGoals.CrowdSpotMemo _memo = new();
    private readonly PlayerbotEconomyWalker _walker = new(session, options);
    private bool _active;
    private ObjectGuid _npc;
    private Vector3 _spot;

    /// <summary>Whether the bot is walking to its spot now.</summary>
    internal bool Active => _active;

    /// <summary>
    /// One think of a bot that is in reach of <paramref name="npc"/>. True while the bot walks to its own spot (the caller holds its service and returns);
    /// false when the bot is served from where it stands (not crowded, the rule already fired for this NPC, no spot found, or it has arrived).
    /// </summary>
    internal bool Step(Player player, NpcInfo npc, uint elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(npc);
        if (_active)
        {
            if (_npc != npc.Guid)
            {
                Clear();
            }
            else
            {
                if (Vector3.Distance(new(player.X, player.Y, player.Z), _spot) <= PlayerbotTownGoals.VisitSpotArrivedYards
                    || !_walker.Step(player, _spot, elapsedMs))
                {
                    Clear();
                    return false;
                }

                return true;
            }
        }

        uint now = session.World.NowMs;
        if (!ShouldSpread(_memo, npc.Guid, now, () => PlayerbotTownGoals.AnotherBotWithin(player, PlayerbotTownGoals.CrowdedYards))) return false;
        if (npc.IsGameObject || player.Map is not { } map) return false;
        var at = new Vector3(npc.X, npc.Y, npc.Z);
        if (!at.IsFinite()) return false;
        Vector3? spot = PickSpot(player.Guid.Value, npc.Guid.Value, at, candidate =>
        {
            float height = map.Collision.GetHeight(candidate.X, candidate.Y, at.Z + 2f);
            if (!PlayerbotNavigation.InvalidHeight(height))
            {
                if (MathF.Abs(height - at.Z) > PlayerbotTownGoals.CrowdedHeightYards) return null; // another floor
                candidate = candidate with { Z = height };
            }

            return PlayerbotQuestGoals.WithinReachAt(candidate, player.BoundingRadius, npc) ? candidate : null;
        });
        if (spot is not { } chosen) return false;
        _npc = npc.Guid;
        _spot = chosen;
        _active = true;
        _walker.Clear();
        return _walker.Step(player, chosen, elapsedMs) || ClearAndFalse();
    }

    /// <summary>Forget the walk (the visit ended or was dropped). The per-NPC memo is kept: a bot that walks away and back is not moved again within the minute.</summary>
    internal void Clear()
    {
        _active = false;
        _npc = default;
        _walker.Clear();
    }

    private bool ClearAndFalse()
    {
        Clear();
        return false;
    }

    /// <summary>
    /// Whether the crowd rule fires now: the NPC's gate is open and another bot crowds the place (<paramref name="crowded"/> is only asked when the gate is open). Firing closes the gate
    /// for <see cref="PlayerbotTownGoals.CrowdSpotMs"/>, so a bot with no spot to step to is not planned for again at every think.
    /// </summary>
    internal static bool ShouldSpread(PlayerbotTownGoals.CrowdSpotMemo memo, ObjectGuid npc, uint now, Func<bool> crowded)
    {
        if (!memo.Due(npc, now) || !crowded()) return false;
        memo.Mark(npc, now);
        return true;
    }

    /// <summary>The first of the bot's ring spots around <paramref name="at"/> that <paramref name="resolve"/> accepts (it returns the spot to use, or null to skip it); null when none is.</summary>
    internal static Vector3? PickSpot(ulong botGuid, ulong npcGuid, Vector3 at, Func<Vector3, Vector3?> resolve)
    {
        for (int attempt = 0; attempt < PlayerbotStandSpots.ServiceAttempts; attempt++)
        {
            if (resolve(PlayerbotStandSpots.ServiceSpot(botGuid, npcGuid, at, attempt)) is { } spot) return spot;
        }

        return null;
    }
}
