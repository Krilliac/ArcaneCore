using ArcaneCore.Game.Maps;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// The part of the population this process may act on. In a split cluster every world worker runs the population over one shared bot pool,
/// but a worker can only log in, create and teleport bots on the maps it owns (<c>Cluster:Worker:OwnedMaps</c>, <see cref="MapOwnership"/>).
/// The scope is the bot-placement maps (<c>AllowedMaps</c>) this process owns; the survey, the zone atlas, the race choice of a new bot,
/// the restart choice and the share of the target all read it. With no ownership restriction (combined mode, the default) the scope is the
/// whole allowed list and every question answers "yes" or "all": nothing changes. Immutable; safe from any thread.
/// </summary>
internal sealed class PopulationMapScope
{
    private readonly MapOwnership _ownership;
    private readonly uint[] _allowed;

    /// <param name="ownership">The maps this process owns.</param>
    /// <param name="allowedMaps"><see cref="ArcaneCore.World.Playerbots.PlayerbotOptions.AllowedMaps"/>: the maps bots may be placed on.</param>
    public PopulationMapScope(MapOwnership ownership, IEnumerable<uint> allowedMaps)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(allowedMaps);
        _ownership = ownership;
        _allowed = [.. allowedMaps.Distinct()];
        Maps = ownership.OwnsEverything ? _allowed : [.. _allowed.Where(m => ownership.Owns(m, 0))];
    }

    /// <summary>True when no map is withheld (combined mode).</summary>
    public bool OwnsEverything => _ownership.OwnsEverything;

    /// <summary>The allowed maps this process owns, in the order of the allowed list.</summary>
    public uint[] Maps { get; }

    /// <summary>True when this process owns none of the allowed maps (a worker of instances only): its population stays idle.</summary>
    public bool IsEmpty => !OwnsEverything && Maps.Length == 0;

    /// <summary>Whether a shared map (instance 0) is this process's.</summary>
    public bool Owns(uint mapId) => _ownership.Owns(mapId, 0);

    /// <summary>Whether a bot of this race is created at a start position on an owned continent (<see cref="PopulationStartAreas.ContinentOf"/>).</summary>
    public bool OwnsRaceStart(byte race) => Owns(PopulationStartAreas.ContinentOf(race));

    /// <summary>
    /// This process's share of a realm-wide bot count. The count is divided evenly over the allowed maps and the remainder goes one bot each to the first maps in map id order; a worker's share is the
    /// sum over the maps it owns. The workers own disjoint maps, so their shares add up to the count exactly (1001 over two maps is 501 and 500). The count itself when nothing is withheld.
    /// Each worker fills only its own share, from the bots it can log in and the bots it creates, so the workers together stay at the realm-wide target instead of each trying to reach all of it.
    /// </summary>
    public int ShareOf(int count) => ShareOf(count, null);

    /// <summary>
    /// <see cref="ShareOf(int)"/> weighted by a static per-map capacity, <paramref name="capacityOf"/> (the number of creature spawns the shared world data holds for a map, which every worker can read for every
    /// allowed map without surveying terrain). The count is split in proportion to the capacities with the largest-remainder rule (ties to the lower map id), so every worker that sees the same capacities computes the same
    /// split and the workers' shares, being sums over disjoint maps, add up to the count exactly. When <paramref name="capacityOf"/> is null, or every allowed map reports no capacity, the even split applies.
    /// <paramref name="capacityOf"/> is called on the calling thread and must be pure. The count itself when nothing is withheld.
    /// </summary>
    public int ShareOf(int count, Func<uint, int>? capacityOf)
    {
        if (OwnsEverything) return count;
        if (count <= 0 || _allowed.Length == 0 || Maps.Length == 0) return 0;
        uint[] order = [.. _allowed.Order()];
        long[] weights = new long[order.Length];
        long total = 0;
        if (capacityOf is not null)
        {
            for (int i = 0; i < order.Length; i++)
            {
                weights[i] = Math.Max(0, capacityOf(order[i]));
                total += weights[i];
            }
        }
        if (total <= 0)
        {
            Array.Fill(weights, 1L);
            total = order.Length;
        }
        int[] parts = new int[order.Length];
        long[] remainders = new long[order.Length];
        int given = 0;
        for (int i = 0; i < order.Length; i++)
        {
            long scaled = count * weights[i];
            parts[i] = (int)(scaled / total);
            remainders[i] = scaled % total;
            given += parts[i];
        }
        // order is ascending by map id, so a stable sort on the remainder alone breaks ties to the lower map id.
        int[] byRemainder = [.. Enumerable.Range(0, order.Length).OrderByDescending(i => remainders[i])];
        for (int k = 0; k < count - given; k++) parts[byRemainder[k]]++;
        int share = 0;
        for (int i = 0; i < order.Length; i++)
            if (Maps.Contains(order[i])) share += parts[i];
        return share;
    }
}
