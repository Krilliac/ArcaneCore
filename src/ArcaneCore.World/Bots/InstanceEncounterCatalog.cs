using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Game.Maps;
using ArcaneCore.World.Features;

namespace ArcaneCore.World.Bots;

/// <summary>
/// The <c>instance_encounter</c> rows in force (DungeonEncounter.dbc through cmangos classic-db): read once by the playerbot group
/// coordinator when it first attaches, swapped by <c>.reload instance_encounter</c>. Core-owned content
/// (docs/design/hot-reload-tiers.md, section 4.1), so a detach of the coordinator, or a swap of its pack, neither reloads nor loses it.
/// A pinned feature (discovered, never detached). Any thread; one immutable list replaced as a whole.
/// </summary>
public sealed class InstanceEncounterCatalog : IWorldFeature
{
    private IReadOnlyList<InstanceEncounter> _encounters = [];
    private volatile bool _loaded;

    /// <summary>The encounters in force (empty before the first load).</summary>
    public IReadOnlyList<InstanceEncounter> Current => Volatile.Read(ref _encounters);

    /// <summary>Whether the table was read (or replaced) at least once in this process.</summary>
    public bool IsLoaded => _loaded;

    /// <summary>Put <paramref name="encounters"/> in force; returns the list replaced (a reload's rollback).</summary>
    public IReadOnlyList<InstanceEncounter> Replace(IReadOnlyList<InstanceEncounter> encounters)
    {
        ArgumentNullException.ThrowIfNull(encounters);
        IReadOnlyList<InstanceEncounter> previous = Interlocked.Exchange(ref _encounters, encounters);
        _loaded = true;
        return previous;
    }

    public void Attach(WorldRuntime world)
    {
    }
}
