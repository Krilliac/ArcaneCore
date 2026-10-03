namespace ArcaneCore.Kernel.WorldData.Totems;

/// <summary>
/// The spell each totem creature casts on itself (or at enemies, for the one cast-time totem family).
/// classic-db has no <c>totem_spell_id</c> column (vmangos adds one later, Objects/Totem.cpp:220-223);
/// mangos-classic takes the FIRST spell of the creature's spell list (Entities/Totem.cpp:171-178), which
/// for the 1.12.1 data is <c>creature_template_spells.spell1</c> (or position 0 of the template's
/// <c>creature_spell_list</c>). The importer resolves that once into the <c>totem_spell</c> table.
/// </summary>
public sealed class TotemContent
{
    public static readonly TotemContent Empty = new([]);

    private readonly Dictionary<uint, uint> _spellByCreature;

    public TotemContent(IEnumerable<(uint CreatureEntry, uint SpellId)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _spellByCreature = [];
        foreach ((uint entry, uint spell) in rows)
        {
            _spellByCreature[entry] = spell;
        }
    }

    public int Count => _spellByCreature.Count;

    /// <summary>The totem's spell, or null when the creature has none (e.g. Sentry Totem 3968).</summary>
    public uint? GetSpell(uint creatureEntry) => _spellByCreature.TryGetValue(creatureEntry, out uint spell) ? spell : null;
}

/// <summary>Loads the totem content from the world database.</summary>
public interface ITotemDataStore
{
    Task<TotemContent> LoadAsync(CancellationToken cancellationToken = default);
}
