using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Procs;

namespace ArcaneCore.Game.Spells.Procs;

/// <summary>
/// The live <c>spell_proc_event</c> table (vmangos SpellMgr::mSpellProcEventMap): an <see cref="ISpellProcEventCatalog"/> whose rows can be swapped
/// (<see cref="Replace"/>, the startup load and <c>.reload spell_proc_event</c>) while the spell system keeps the same object. The higher ranks of a
/// listed spell are filled from the rank chains when the table is first read after a swap (<see cref="SpellProcEventContent.Resolve"/>), so the chains
/// and the spell store may finish loading after the table does. Read and replaced on the world thread.
/// </summary>
public sealed class SpellProcEventTable(Func<SpellRankChains?>? ranks = null, Func<uint, bool>? spellExists = null, Action<string>? report = null) : ISpellProcEventCatalog
{
    private SpellProcEventContent _content = SpellProcEventContent.Empty;
    private IReadOnlyDictionary<uint, SpellProcEventRecord>? _resolved;

    /// <summary>Swap in a new table; the next lookup resolves the ranks again.</summary>
    public void Replace(SpellProcEventContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _resolved = null;
    }

    /// <summary>The rows as loaded, before the rank fill.</summary>
    public SpellProcEventContent Content => _content;

    /// <summary>The number of spells with an entry after the rank fill.</summary>
    public int Count => Resolved.Count;

    /// <inheritdoc/>
    public SpellProcEventRecord? Find(uint spellId) => Resolved.GetValueOrDefault(spellId);

    private IReadOnlyDictionary<uint, SpellProcEventRecord> Resolved
        => _resolved ??= _content.Resolve(ranks?.Invoke() ?? SpellRankChains.Empty, spellExists, report);
}
