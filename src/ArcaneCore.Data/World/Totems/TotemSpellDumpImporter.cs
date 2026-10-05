using System.Globalization;
using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.World.Totems;

/// <summary>What a totem spell import read and wrote.</summary>
/// <param name="Rows">Rows written to <c>totem_spell</c>.</param>
/// <param name="FromSpellList">Rows whose spell came from <c>creature_spell_list</c> rather than <c>creature_template_spells</c>.</param>
/// <param name="SkippedWithoutSpell">Totem creatures with no positive spell in the selected source, e.g. Sentry Totem 3968.</param>
/// <param name="SummonedWithoutRow">Creature entries named by a SUMMON_TOTEM / SLOT1-4 effect that end up without a row.</param>
public sealed record TotemSpellImportReport(int Rows, int FromSpellList, int SkippedWithoutSpell, IReadOnlyList<uint> SummonedWithoutRow)
{
    /// <summary>Rows resolved from vmangos creature_template.totem_spell_id.</summary>
    public int FromTemplateField { get; init; }
}

/// <summary>
/// Builds <c>totem_spell</c> from a mangos-classic style classic-db 1.12.1 dump (read by column NAME,
/// never copied into the repo). A totem creature is one named by <c>spell_template.EffectMiscValueN</c>
/// of a SPELL_EFFECT_SUMMON_TOTEM (74) or SUMMON_TOTEM_SLOT1-4 (87-90) effect (vmangos SpellEffects.cpp:4923
/// reads the entry from the effect misc value), or whose <c>creature_template.AIName</c> is TotemAI.
/// Its spell is the FIRST entry of its creature spell list (mangos-classic Entities/Totem.cpp:171-178):
/// the lowest-position positive spell of an explicit <c>creature_template.SpellList</c>, otherwise the
/// first nonzero <c>creature_template_spells.spell1..spell10</c> of set 0 (Creature.cpp:609-612 and
/// ObjectMgr.cpp:9757-9816, core 8ec338a1704e7dcb1c0213eb7ed58f9231ade40f).
/// vmangos instead uses <c>creature_template.totem_spell_id</c>
/// directly (Totem.cpp:220-222, core 0e3ff01e76d4758e8a7c3108b2717cc785ed56fa).
/// Rows whose spell is 0 are skipped and counted.
/// </summary>
public sealed class TotemSpellDumpImporter
{
    private static readonly HashSet<uint> s_summonTotemEffects = [74, 87, 88, 89, 90];

    private readonly Dictionary<uint, HashSet<uint>> _summonedBySpell = [];
    private readonly HashSet<uint> _totemAi = [];
    private readonly Dictionary<uint, uint> _templateFirst = [];
    private readonly Dictionary<uint, uint> _spellListOfCreature = [];
    private readonly Dictionary<(uint Id, uint Position), uint> _listSpells = [];
    private readonly Dictionary<uint, uint> _directSpellOfCreature = [];
    private readonly Dictionary<uint, int> _creaturePatch = [];

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
        Dictionary<uint, uint> listFirst = _listSpells.Where(p => p.Value > 0)
            .GroupBy(p => p.Key.Id).ToDictionary(g => g.Key, g => g.MinBy(p => p.Key.Position).Value);
        HashSet<uint> summoned = [.. _summonedBySpell.Values.SelectMany(entries => entries)];
        int fromList = 0;
        int fromTemplate = 0;
        int skipped = 0;
        foreach (uint entry in summoned.Union(_totemAi).Union(_directSpellOfCreature.Where(p => p.Value != 0).Select(p => p.Key)).Order())
        {
            bool direct = _directSpellOfCreature.TryGetValue(entry, out uint spell);
            bool fromSpellList = false;
            if (!direct)
            {
                if (_spellListOfCreature.TryGetValue(entry, out uint list))
                {
                    spell = listFirst.GetValueOrDefault(list);
                    fromSpellList = spell != 0;
                }
                else
                {
                    spell = _templateFirst.GetValueOrDefault(entry);
                }
            }

            if (spell == 0)
            {
                skipped++;
                if (summoned.Contains(entry))
                {
                    missing.Add(entry);
                }

                continue;
            }

            fromList += fromSpellList ? 1 : 0;
            fromTemplate += direct ? 1 : 0;
            rows.Add(new TotemSpellRow { CreatureEntry = entry, SpellId = spell });
        }

        return (rows, new TotemSpellImportReport(rows.Count, fromList, skipped, missing) { FromTemplateField = fromTemplate });
    }

    /// <summary>
    /// Replace <c>totem_spell</c> atomically; an existing caller transaction is protected by a savepoint.
    /// </summary>
    public Task<TotemSpellImportReport> WriteAsync(WorldDbContext db, CancellationToken cancellationToken = default)
        => WriteAsync(db, replace: true, cancellationToken);

    /// <summary>
    /// Write with the shared content-import transaction contract. With <paramref name="replace"/>
    /// the table is emptied first; otherwise existing keys fail and previous content is preserved.
    /// </summary>
    public async Task<TotemSpellImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        (IReadOnlyList<TotemSpellRow> rows, TotemSpellImportReport report) = Resolve();
        await ImportTransaction.RunAsync(db, async token =>
        {
            if (replace)
            {
                await db.Set<TotemSpellRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
            }

            await ImportBatch.InsertAsync(db, rows, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        return report;
    }

    private void ReadSpell(DumpRow row)
    {
        if (!TryUInt(row, out uint id, "Id"))
        {
            return;
        }

        var entries = new HashSet<uint>();
        for (int i = 1; i <= 3; i++)
        {
            if (TryUInt(row, out uint effect, "Effect" + i.ToString(CultureInfo.InvariantCulture)) && s_summonTotemEffects.Contains(effect)
                && TryInt(row, out int misc, "EffectMiscValue" + i.ToString(CultureInfo.InvariantCulture)) && misc > 0)
            {
                entries.Add((uint)misc);
            }
        }

        if (entries.Count > 0)
        {
            _summonedBySpell[id] = entries;
        }
        else
        {
            _summonedBySpell.Remove(id);
        }
    }

    private void ReadCreature(DumpRow row)
    {
        if (!TryUInt(row, out uint entry, "entry"))
        {
            return;
        }

        int patch = TryInt(row, out int sourcePatch, "patch") ? sourcePatch : 0;
        if (patch > CreatureDumpImporter.MaxPatch || (_creaturePatch.TryGetValue(entry, out int previousPatch) && previousPatch > patch))
        {
            return;
        }

        _creaturePatch[entry] = patch;

        if (row.TryGet(out string? ai, "AIName", "ai_name") && string.Equals(ai, "TotemAI", StringComparison.OrdinalIgnoreCase))
        {
            _totemAi.Add(entry);
        }
        else
        {
            _totemAi.Remove(entry);
        }

        if (TryInt(row, out int list, "SpellList", "spell_list_id") && list > 0)
        {
            _spellListOfCreature[entry] = (uint)list;
        }
        else
        {
            _spellListOfCreature.Remove(entry);
        }

        if (row.TryGet(out _, "totem_spell_id"))
        {
            TryUInt(row, out uint direct, "totem_spell_id");
            _directSpellOfCreature[entry] = direct;
        }
        else
        {
            _directSpellOfCreature.Remove(entry);
        }
    }

    private void ReadTemplateSpells(DumpRow row)
    {
        // setId 0 is the default set (creature_template_spells PRIMARY KEY (entry, setId)).
        if (TryUInt(row, out uint entry, "entry")
            && (!TryUInt(row, out uint set, "setId") || set == 0))
        {
            uint first = 0;
            for (int i = 1; i <= 10; i++)
            {
                if (TryUInt(row, out uint spell, "spell" + i.ToString(CultureInfo.InvariantCulture)) && spell != 0)
                {
                    first = spell;
                    break;
                }
            }

            _templateFirst[entry] = first;
        }
    }

    private void ReadSpellList(DumpRow row)
    {
        if (TryUInt(row, out uint id, "Id") && TryUInt(row, out uint position, "Position")
            && TryInt(row, out int spell, "SpellId"))
        {
            _listSpells[(id, position)] = spell > 0 ? (uint)spell : 0;
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
