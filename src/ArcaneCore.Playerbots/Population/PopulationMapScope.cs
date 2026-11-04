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
    /// This process's share of a realm-wide bot count: the count times the owned fraction of the allowed maps, rounded to the nearest whole bot
    /// (the count itself when nothing is withheld). Each worker fills only its own share, from the bots it can log in and the bots it creates, so
    /// the workers together stay at the realm-wide target instead of each trying to reach all of it from a subset of the pool.
    /// </summary>
    public int ShareOf(int count)
    {
        if (OwnsEverything) return count;
        if (count <= 0) return 0;
        if (_allowed.Length == 0 || Maps.Length == 0) return 0;
        return (int)(((long)count * Maps.Length + _allowed.Length / 2) / _allowed.Length);
    }
}
