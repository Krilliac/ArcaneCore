using ArcaneCore.Kernel.Skills;
using ArcaneCore.Kernel.WorldData.Threat;

namespace ArcaneCore.Game.Combat.Threat;

/// <summary>
/// The live <c>spell_threat</c> table (vmangos SpellMgr::mSpellThreatMap): an <see cref="ISpellThreatCatalog"/> whose rows can be
/// swapped (<see cref="Replace"/>, the startup load and <c>.reload spell_threat</c>) while the map combat keeps the same object. The
/// higher ranks of a listed spell are filled from the rank chains when the table is first read after a swap, so the chains and the spell
/// store may finish loading after the table does (<see cref="SpellThreatContent.Resolve"/>); without chains only the listed spells
/// are known. Read and replaced on the world thread.
/// </summary>
public sealed class SpellThreatTable(Func<SpellRankChains?>? ranks = null, Func<uint, bool>? spellExists = null, Action<string>? report = null) : ISpellThreatCatalog
{
    private SpellThreatContent _content = SpellThreatContent.Empty;
    private IReadOnlyDictionary<uint, SpellThreatRecord>? _resolved;

    /// <summary>Swap in a new table; the next lookup resolves the ranks again.</summary>
    public void Replace(SpellThreatContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _content = content;
        _resolved = null;
    }

    /// <summary>The rows as loaded, before the rank fill.</summary>
    public SpellThreatContent Content => _content;

    /// <summary>The number of spells with an entry after the rank fill (vmangos "Loaded %u spell threat entries" counts the listed rows).</summary>
    public int Count => Resolved.Count;

    /// <inheritdoc/>
    public SpellThreatEntry? Find(uint spellId)
        => Resolved.TryGetValue(spellId, out SpellThreatRecord? row) ? new SpellThreatEntry(spellId, row.Threat, row.Multiplier, row.InverseEffectMask) : null;

    private IReadOnlyDictionary<uint, SpellThreatRecord> Resolved
        => _resolved ??= _content.Resolve(ranks?.Invoke() ?? SpellRankChains.Empty, spellExists, report);
}
