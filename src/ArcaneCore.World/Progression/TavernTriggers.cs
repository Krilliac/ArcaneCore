using System.Collections.Frozen;

namespace ArcaneCore.World.Progression;

/// <summary>
/// The area triggers that mark an inn (the <c>areatrigger_tavern</c> table; vmangos ObjectMgr::mTavernAreaTriggerSet). The set is
/// an immutable object behind this holder: <see cref="Replace"/> swaps it as a whole (startup load, <c>.reload areatrigger_tavern</c>),
/// so a reader sees the old set or the new one, never a mixture. Read and replaced on the world thread.
/// </summary>
public sealed class TavernTriggers
{
    private FrozenSet<uint> _ids = FrozenSet<uint>.Empty;

    /// <summary>The number of inn triggers.</summary>
    public int Count => _ids.Count;

    /// <summary>The ids as loaded (ascending).</summary>
    public IReadOnlyCollection<uint> Ids => _ids.Items.Order().ToArray();

    /// <summary>vmangos ObjectMgr::IsTavernAreaTrigger.</summary>
    public bool Contains(uint triggerId) => _ids.Contains(triggerId);

    /// <summary>Install a new set and return the one replaced.</summary>
    public FrozenSet<uint> Replace(FrozenSet<uint> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        FrozenSet<uint> previous = _ids;
        _ids = ids;
        return previous;
    }

    /// <summary>An immutable set of <paramref name="ids"/> (duplicates collapse).</summary>
    public static FrozenSet<uint> Build(IEnumerable<uint> ids) => ids.ToFrozenSet();
}
