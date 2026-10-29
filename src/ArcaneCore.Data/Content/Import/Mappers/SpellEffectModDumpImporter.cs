using System.Globalization;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.World.Creatures;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Reads vmangos <c>spell_effect_mod</c> (the effect corrections vmangos SpellModMgr::LoadSpellMods applies over Spell.dbc,
/// src/game/Spells/SpellModMgr.cpp:195-262) and writes the reviewed rows to the world's <c>spell_effect_mod</c> (world 63, applied by the
/// world's SpellStoreFactory into effect slots the spell data leaves empty). The dump is GPL data the operator supplies; nothing is committed.
/// <para>
/// The vmangos world dump (db-4641790) has 371 rows: 291 rewrite an effect classic-db's <c>spell_template</c> defines (cmangos corrects those spells its own
/// way), 27 name spells classic-db lacks, and 53 fill an empty effect slot. Most of the 53 are how vmangos implements a spell that the
/// cmangos family, and ArcaneCore, implement in code (Bloodthirst's heal trigger 23885-23888 next to WarriorScripts, Blade Flurry's
/// 22482 next to DummyTalentProcScripts, the Alterac Valley beacons), so applying them would run the spell twice. Only the spells in
/// <see cref="ReviewedSpells"/>, each checked against both cores and ArcaneCore, are written; every other id is counted and named in
/// <see cref="NotReviewed"/>.
/// </para>
/// </summary>
public sealed class SpellEffectModDumpImporter
{
    /// <summary>
    /// The spells whose vmangos corrections are written, each checked: the effect slot is empty in Spell.dbc and classic-db, no core and no
    /// ArcaneCore code implements the behaviour another way, and every spell the row names exists in 1.12.1 data.
    /// <list type="bullet">
    /// <item>16468 Mother's Milk: effect 2 a periodic trigger of 15474 Web Explosion every 11999 ms. classic-db has only the DUMMY aura of
    /// effect 1; neither cmangos nor vmangos code names the spell (grep of both trees) and ArcaneCore has no handler, so without the row the
    /// debuff does nothing.</item>
    /// </list>
    /// </summary>
    public static IReadOnlySet<uint> ReviewedSpells { get; } = new HashSet<uint> { 16468 };

    private readonly SortedDictionary<(uint Spell, byte Effect), SpellEffectModRow> _rows = [];
    private readonly SortedSet<uint> _notReviewed = [];
    private int _read;

    /// <summary>Whether an input carried <c>spell_effect_mod</c> rows (the table is replaced only then).</summary>
    public bool HasRows => _read > 0;

    /// <summary>The rows to write, by spell and effect index (a later row replaces an earlier one, as vmangos applies them in order).</summary>
    public IReadOnlyCollection<SpellEffectModRow> Rows => _rows.Values;

    /// <summary>The spells with rows that are not in <see cref="ReviewedSpells"/> and are left out.</summary>
    public IReadOnlyCollection<uint> NotReviewed => _notReviewed;

    /// <summary>Read one dump (call again for further files).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && row.Table.Equals(SpellEffectModDataModule.Table, StringComparison.OrdinalIgnoreCase))
            {
                Accept(row);
            }
        }
    }

    /// <summary>Replace the world's <c>spell_effect_mod</c> with the reviewed rows read (inside the caller's transaction).</summary>
    public async Task ReplaceAsync(WorldDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        await db.Set<SpellEffectModRow>().ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        db.Set<SpellEffectModRow>().AddRange(_rows.Values);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    /// <summary>The report line for the operator: what was written and which spells were left out.</summary>
    public string Describe()
        => _notReviewed.Count == 0
            ? $"spell_effect_mod: {_rows.Count} reviewed row(s)"
            : $"spell_effect_mod: {_rows.Count} reviewed row(s); {_notReviewed.Count} spell(s) not reviewed, left out: "
              + string.Join(", ", _notReviewed.Take(20)) + (_notReviewed.Count > 20 ? ", ..." : string.Empty);

    private void Accept(DumpRow row)
    {
        _read++;
        uint id = (uint)Long(row, "Id", 0);
        if (!ReviewedSpells.Contains(id))
        {
            _notReviewed.Add(id);
            return;
        }

        var mod = new SpellEffectModRow
        {
            Id = id,
            EffectIndex = (byte)Long(row, "EffectIndex", 0),
            Effect = Int(row, "Effect"),
            EffectApplyAuraName = Int(row, "EffectApplyAuraName"),
            EffectMechanic = Int(row, "EffectMechanic"),
            EffectImplicitTargetA = Int(row, "EffectImplicitTargetA"),
            EffectImplicitTargetB = Int(row, "EffectImplicitTargetB"),
            EffectRadiusIndex = Int(row, "EffectRadiusIndex"),
            EffectItemType = Long(row, "EffectItemType", -1),
            EffectMiscValue = Int(row, "EffectMiscValue"),
            EffectTriggerSpell = Int(row, "EffectTriggerSpell"),
            EffectDieSides = Int(row, "EffectDieSides"),
            EffectBaseDice = Int(row, "EffectBaseDice"),
            EffectBasePoints = Int(row, "EffectBasePoints"),
            EffectAmplitude = Int(row, "EffectAmplitude"),
            EffectChainTarget = Int(row, "EffectChainTarget"),
            EffectDicePerLevel = Float(row, "EffectDicePerLevel"),
            EffectRealPointsPerLevel = Float(row, "EffectRealPointsPerLevel"),
            EffectPointsPerComboPoint = Float(row, "EffectPointsPerComboPoint"),
            EffectMultipleValue = Float(row, "EffectMultipleValue"),
        };
        _rows[(mod.Id, mod.EffectIndex)] = mod;
    }

    // A missing or NULL column keeps the spell's value, as -1 does in vmangos (the column defaults of its spell_effect_mod are -1).
    private static long Long(DumpRow row, string name, long missing)
        => row.TryGet(out string? value, name) && !string.IsNullOrWhiteSpace(value) && !value.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : missing;

    private static int Int(DumpRow row, string name) => (int)Long(row, name, -1);

    private static float Float(DumpRow row, string name)
        => row.TryGet(out string? value, name) && !string.IsNullOrWhiteSpace(value) && !value.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            ? float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture)
            : -1f;
}
