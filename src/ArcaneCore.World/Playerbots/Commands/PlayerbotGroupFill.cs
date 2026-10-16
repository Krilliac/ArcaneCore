using System.Globalization;
using ArcaneCore.Game;
using ArcaneCore.Game.Groups;
using ArcaneCore.World.Playerbots.Groups;

namespace ArcaneCore.World.Playerbots.Commands;

/// <summary>One bot <c>.bot party fill</c> or <c>.bot raid</c> may pick: who it is, its class and how far its level is from the caller's.</summary>
internal readonly record struct PlayerbotFillCandidate(Guid BotId, string Name, Class Class, int LevelDiff);

/// <summary>
/// The rules <c>.bot party fill</c> and <c>.bot raid</c> share, apart from the world so they can be tested alone: how many bots a
/// group still takes (a party five, a raid forty: <see cref="Group.MaxGroupSize"/>, <see cref="Group.MaxRaidSize"/>), the tanks and
/// healers a fill of that size wants, and which candidates are picked.
/// </summary>
internal static class PlayerbotGroupFill
{
    /// <summary>A candidate within this many levels of the caller is preferred while there are enough of them.</summary>
    internal const int NearLevels = 5;

    /// <summary>The most bot names one reply lists; the rest are counted.</summary>
    internal const int MaxNamesShown = 10;

    /// <summary>
    /// How many bots <c>.bot party fill</c> invites: a party (or no group) is filled to five; a raid takes one more subgroup's worth
    /// (five), up to <see cref="Group.MaxRaidSize"/>. Zero when the group is full.
    /// </summary>
    internal static int PartyFillCount(bool raid, int memberCount)
        => raid
            ? Math.Clamp(Group.MaxRaidSize - memberCount, 0, Group.MaxGroupSize)
            : Math.Clamp(Group.MaxGroupSize - memberCount, 0, Group.MaxGroupSize - 1);

    /// <summary>
    /// <c>.bot raid N</c>: the bots to invite, <paramref name="requested"/> clamped to the free raid slots
    /// (<see cref="Group.MaxRaidSize"/> less <paramref name="memberCount"/>, the caller counting as one when not yet grouped).
    /// </summary>
    internal static (int Count, bool Clamped) RaidFillCount(int requested, int memberCount)
    {
        int free = Math.Max(0, Group.MaxRaidSize - Math.Max(1, memberCount));
        int count = Math.Clamp(requested, 0, free);
        return (count, count < requested);
    }

    /// <summary>
    /// The tanks and healers a fill of <paramref name="count"/> bots wants: one each for a party's worth (as party fill always
    /// picked), and the raid run's make-up beyond it (<see cref="PlayerbotGroupContent.RunRoleNeeds"/>: <c>TanksPerTen</c> and
    /// <c>HealerPercent</c> of <c>World:Playerbots:Groups:DungeonRuns:Raids</c>, so 10, 20 and 39 bots take 1, 2 and 4 tanks and
    /// 3, 5 and 10 healers at the defaults). Never more than <paramref name="count"/> together.
    /// </summary>
    internal static (int Tanks, int Healers) RoleTargets(int count, PlayerbotGroupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (count <= 0) return (0, 0);
        (int tanks, int healers) = count <= Group.MaxGroupSize
            ? (1, 1)
            : PlayerbotGroupContent.RunRoleNeeds(count, raid: true, options);
        tanks = Math.Clamp(tanks, 0, count);
        healers = Math.Clamp(healers, 0, count - tanks);
        return (tanks, healers);
    }

    /// <summary>
    /// Pick <paramref name="needed"/> of <paramref name="candidates"/>: from those within <see cref="NearLevels"/> levels when there
    /// are enough of them, otherwise from all, nearest level first (then by name, so the pick is stable); first up to
    /// <paramref name="tanks"/> tank classes (warrior first, then paladin and druid), then up to <paramref name="healers"/> healer
    /// classes (priest and shaman first, then paladin and druid), then the nearest levels whatever the class.
    /// </summary>
    internal static List<PlayerbotFillCandidate> Select(IReadOnlyList<PlayerbotFillCandidate> candidates, int needed, int tanks, int healers)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var selected = new List<PlayerbotFillCandidate>(Math.Max(0, needed));
        if (needed <= 0) return selected;
        List<PlayerbotFillCandidate> near = [.. candidates.Where(c => c.LevelDiff <= NearLevels)];
        List<PlayerbotFillCandidate> pool = [.. (near.Count >= needed ? near : candidates)
            .OrderBy(c => c.LevelDiff).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];

        void Pick(int count, Func<Class, int> rank)
        {
            for (int i = 0; i < count && selected.Count < needed; i++)
            {
                int best = -1;
                for (int j = 0; j < pool.Count; j++)
                {
                    int r = rank(pool[j].Class);
                    if (r < 0) continue;
                    if (best < 0 || r < rank(pool[best].Class)) best = j;
                }

                if (best < 0) return;
                selected.Add(pool[best]);
                pool.RemoveAt(best);
            }
        }

        Pick(tanks, static c => c switch { Class.Warrior => 0, Class.Paladin or Class.Druid => 1, _ => -1 });
        Pick(healers, static c => c switch { Class.Priest or Class.Shaman => 0, Class.Paladin or Class.Druid => 1, _ => -1 });
        while (selected.Count < needed && pool.Count > 0)
        {
            selected.Add(pool[0]);
            pool.RemoveAt(0);
        }

        return selected;
    }

    /// <summary>Names for a reply: at most <see cref="MaxNamesShown"/>, then "and N more".</summary>
    internal static string Names(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count <= MaxNamesShown) return string.Join(", ", names);
        return string.Join(", ", names.Take(MaxNamesShown))
            + string.Create(CultureInfo.InvariantCulture, $" and {names.Count - MaxNamesShown} more");
    }
}
