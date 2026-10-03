using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ArcaneCore.Data.World.Totems;

/// <summary>What a totem spell import read and wrote.</summary>
/// <param name="Rows">Rows written to <c>totem_spell</c>.</param>
/// <param name="FromSpellList">Rows whose spell came from <c>creature_spell_list</c> rather than <c>creature_template_spells</c>.</param>
/// <param name="SkippedWithoutSpell">Totem creatures with no spell at all (spell1 = 0 or no list), e.g. Sentry Totem 3968.</param>
/// <param name="SummonedWithoutRow">Creature entries named by a SUMMON_TOTEM / SLOT1-4 effect that end up without a row.</param>
public sealed record TotemSpellImportReport(int Rows, int FromSpellList, int SkippedWithoutSpell, IReadOnlyList<uint> SummonedWithoutRow);

/// <summary>
/// Builds <c>totem_spell</c> from a mangos-classic style classic-db 1.12.1 dump (read by column NAME,
/// never copied into the repo). A totem creature is one named by <c>spell_template.EffectMiscValueN</c>
/// of a SPELL_EFFECT_SUMMON_TOTEM (74) or SUMMON_TOTEM_SLOT1-4 (87-90) effect (vmangos SpellEffects.cpp:4923
/// reads the entry from the effect misc value), or whose <c>creature_template.AIName</c> is TotemAI.
/// Its spell is the FIRST entry of its creature spell list (mangos-classic Entities/Totem.cpp:171-178):
/// <c>creature_template_spells.spell1</c> of set 0, else position 0 of <c>creature_spell_list</c> named by
/// <c>creature_template.SpellList</c>. Rows whose spell is 0 are skipped and counted.
/// </summary>
public sealed class TotemSpellDumpImporter
{
    private static readonly HashSet<uint> s_summonTotemEffects = [74, 87, 88, 89, 90];

    private readonly HashSet<uint> _summoned = [];
    private readonly HashSet<uint> _totemAi = [];
    private readonly Dictionary<uint, uint> _spell1 = [];
    private readonly Dictionary<uint, uint> _spellListOfCreature = [];
    private readonly Dictionary<uint, (uint Position, uint Spell)> _listFirst = [];

    /// <summary>Read one dump; call again for further files.</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var reader = new MySqlDumpReader(dump);
        foreach (object item in reader.Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            switch (row.Table.ToLowerInvariant())
            {
                case "spell_template":
                    ReadSpell(row);
                    break;
                case "creature_template":
                    ReadCreature(row);
                    break;
                case "creature_template_spells":
                    ReadTemplateSpells(row);
                    break;
                case "creature_spell_list":
                    ReadSpellList(row);
                    break;
            }
        }
    }

    /// <summary>The rows that would be written (also the report), resolved from everything read so far.</summary>
    public (IReadOnlyList<TotemSpellRow> Rows, TotemSpellImportReport Report) Resolve()
    {
        var rows = new List<TotemSpellRow>();
        var missing = new List<uint>();
        int fromList = 0;
        int skipped = 0;
        foreach (uint entry in _summoned.Union(_totemAi).Order())
        {
            uint spell = _spell1.GetValueOrDefault(entry);
            bool fromSpellList = false;
            if (spell == 0 && _spellListOfCreature.TryGetValue(entry, out uint list) && _listFirst.TryGetValue(list, out (uint Position, uint Spell) first))
            {
                spell = first.Spell;
                fromSpellList = spell != 0;
            }

            if (spell == 0)
            {
                skipped++;
                if (_summoned.Contains(entry))
                {
                    missing.Add(entry);
                }

                continue;
            }

            fromList += fromSpellList ? 1 : 0;
            rows.Add(new TotemSpellRow { CreatureEntry = entry, SpellId = spell });
        }

        return (rows, new TotemSpellImportReport(rows.Count, fromList, skipped, missing));
    }

    /// <summary>
    /// Replace <c>totem_spell</c> with the resolved rows in one transaction owned by this call (a context
    /// that already has a transaction or tracked entities is refused: the import would not be atomic).
    /// </summary>
    public async Task<TotemSpellImportReport> WriteAsync(WorldDbContext db, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.ChangeTracker.Entries().Any() || db.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Totem imports need a context with an empty change tracker and no open transaction.");
        }

        (IReadOnlyList<TotemSpellRow> rows, TotemSpellImportReport report) = Resolve();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await db.Set<TotemSpellRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.Set<TotemSpellRow>().AddRange(rows);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }

        return report;
    }

    private void ReadSpell(DumpRow row)
    {
        for (int i = 1; i <= 3; i++)
        {
            if (TryUInt(row, out uint effect, "Effect" + i.ToString(CultureInfo.InvariantCulture)) && s_summonTotemEffects.Contains(effect)
                && TryInt(row, out int misc, "EffectMiscValue" + i.ToString(CultureInfo.InvariantCulture)) && misc > 0)
            {
                _summoned.Add((uint)misc);
            }
        }
    }

    private void ReadCreature(DumpRow row)
    {
        if (!TryUInt(row, out uint entry, "entry"))
        {
            return;
        }

        if (row.TryGet(out string? ai, "AIName", "ai_name") && string.Equals(ai, "TotemAI", StringComparison.OrdinalIgnoreCase))
        {
            _totemAi.Add(entry);
        }

        if (TryInt(row, out int list, "SpellList", "spell_list_id") && list > 0)
        {
            _spellListOfCreature[entry] = (uint)list;
        }
    }

    private void ReadTemplateSpells(DumpRow row)
    {
        // setId 0 is the default set (creature_template_spells PRIMARY KEY (entry, setId)).
        if (TryUInt(row, out uint entry, "entry") && TryUInt(row, out uint spell, "spell1")
            && (!TryUInt(row, out uint set, "setId") || set == 0))
        {
            _spell1[entry] = spell;
        }
    }

    private void ReadSpellList(DumpRow row)
    {
        if (TryUInt(row, out uint id, "Id") && TryUInt(row, out uint position, "Position") && TryInt(row, out int spell, "SpellId")
            && spell > 0 && (!_listFirst.TryGetValue(id, out (uint Position, uint Spell) have) || position < have.Position))
        {
            _listFirst[id] = (position, (uint)spell);
        }
    }

    private static bool TryUInt(DumpRow row, out uint value, params ReadOnlySpan<string> names)
    {
        value = 0;
        return row.TryGet(out string? text, names) && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryInt(DumpRow row, out int value, params ReadOnlySpan<string> names)
    {
        value = 0;
        return row.TryGet(out string? text, names) && int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
