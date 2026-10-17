using System.Numerics;

namespace ArcaneCore.World.Playerbots.Groups;

/// <summary>A creature as the route sees it.</summary>
/// <param name="Guid">Its object guid value (a stable order and identity).</param>
/// <param name="Entry">Its creature entry.</param>
/// <param name="Position">Where it stands.</param>
/// <param name="Alive">It is alive.</param>
/// <param name="Hostile">The group may attack it and it would attack the group.</param>
/// <param name="InCombat">It is fighting already (someone else's, or a pack that is coming).</param>
internal readonly record struct PlayerbotRunCreature(ulong Guid, uint Entry, Vector3 Position, bool Alive, bool Hostile, bool InCombat);

/// <summary>The next pull of a run: the creature the tank engages and the pack that comes with it.</summary>
/// <param name="Lead">The creature to engage.</param>
/// <param name="Pack">The lead and every creature within <see cref="PlayerbotRunRoute.PackYards"/> of it.</param>
/// <param name="RouteIndex">The route point the lead stands nearest to.</param>
/// <param name="PacksAhead">How many packs stand along the rest of the route (the lead's included).</param>
internal sealed record PlayerbotRunPull(PlayerbotRunCreature Lead, IReadOnlyList<PlayerbotRunCreature> Pack, int RouteIndex, int PacksAhead);

/// <summary>
/// Which trash a run pulls: the hostile creatures along the way to the next boss, one pack at a time, in the order the route meets
/// them, the boss never as trash. ArcaneCore's rule (neither reference core runs dungeons with bots): a creature belongs to the route
/// when it stands within <see cref="RouteClearYards"/> of a point of the path the group still has to walk, because walking past it would
/// pull it anyway (the aggro radius of a level-matched creature is far smaller than the yards of a corridor); a pack is the creatures
/// within <see cref="PackYards"/> of its first member, which is how classic-db places its groups and the radius the call-for-help of a
/// pack reaches. Pure: positions in, a decision out.
/// </summary>
internal static class PlayerbotRunRoute
{
    /// <summary>A creature this close to a remaining route point is cleared before the group walks on.</summary>
    internal const float RouteClearYards = 15f;

    /// <summary>The creatures this close to the lead of a pull are its pack.</summary>
    internal const float PackYards = 10f;

    /// <summary>
    /// The next pull, or null when nothing stands along the rest of the route.
    /// </summary>
    /// <param name="route">The planned path to the boss, in walking order.</param>
    /// <param name="progress">The index of the first route point not yet passed.</param>
    /// <param name="creatures">The creatures in sight.</param>
    /// <param name="bossEntry">The entry of the boss the route leads to (never pulled as trash; 0: none).</param>
    internal static PlayerbotRunPull? NextPull(IReadOnlyList<Vector3> route, int progress, IReadOnlyList<PlayerbotRunCreature> creatures, uint bossEntry)
        => NextPull(route, progress, creatures, bossEntry, []);

    /// <summary>
    /// The next pull as <see cref="NextPull(IReadOnlyList{Vector3}, int, IReadOnlyList{PlayerbotRunCreature}, uint)"/>, with every entry of
    /// <paramref name="bossEntries"/> (the wing's bosses, a later one too: the way to an early boss may pass a later boss) left out as trash.
    /// </summary>
    internal static PlayerbotRunPull? NextPull(IReadOnlyList<Vector3> route, int progress, IReadOnlyList<PlayerbotRunCreature> creatures, uint bossEntry,
        IReadOnlyList<uint> bossEntries)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(creatures);
        ArgumentNullException.ThrowIfNull(bossEntries);
        if (route.Count == 0) return null;
        progress = Math.Clamp(progress, 0, route.Count - 1);

        var candidates = new List<(PlayerbotRunCreature Creature, int Index, float Distance)>();
        foreach (PlayerbotRunCreature creature in creatures)
        {
            if (!creature.Alive || !creature.Hostile || creature.InCombat || (bossEntry != 0 && creature.Entry == bossEntry) || IsBoss(bossEntries, creature.Entry)) continue;
            int index = -1;
            float nearest = float.MaxValue;
            for (int point = progress; point < route.Count; point++)
            {
                float distance = Vector3.Distance(route[point], creature.Position);
                if (distance < nearest)
                {
                    nearest = distance;
                    index = point;
                }
            }

            if (index >= 0 && nearest <= RouteClearYards) candidates.Add((creature, index, nearest));
        }

        if (candidates.Count == 0) return null;
        var ordered = candidates.OrderBy(c => c.Index).ThenBy(c => c.Distance).ThenBy(c => c.Creature.Guid).ToList();
        (PlayerbotRunCreature lead, int leadIndex, _) = ordered[0];
        PlayerbotRunCreature[] pack = [.. candidates.Where(c => Vector3.Distance(c.Creature.Position, lead.Position) <= PackYards)
            .OrderBy(c => c.Creature.Guid).Select(c => c.Creature)];

        // The packs ahead: greedily peel packs off the ordered list.
        int packs = 0;
        var left = ordered.Select(c => c.Creature).ToList();
        while (left.Count > 0)
        {
            PlayerbotRunCreature head = left[0];
            left.RemoveAll(c => Vector3.Distance(c.Position, head.Position) <= PackYards);
            packs++;
        }

        return new PlayerbotRunPull(lead, pack, leadIndex, packs);
    }

    private static bool IsBoss(IReadOnlyList<uint> bossEntries, uint entry)
    {
        for (int i = 0; i < bossEntries.Count; i++)
            if (bossEntries[i] == entry) return true;

        return false;
    }
}
