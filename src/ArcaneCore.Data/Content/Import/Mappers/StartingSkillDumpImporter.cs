using ArcaneCore.Data.Skills;
using ArcaneCore.Data.World.Creatures;
using System.Globalization;
using ArcaneCore.Kernel.Skills;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

public sealed record StartingSkillImportReport(int Rows, int SkippedRows, IReadOnlyList<string> Warnings);

/// <summary>Maps playercreateinfo_skills by column name and applies vmangos mask/skill/step bounds.</summary>
public sealed class StartingSkillDumpImporter
{
    private const string TableName = "playercreateinfo_skills";
    private const uint PlayableRaces = 0xFF;
    private const uint PlayableClasses = (1u << 0) | (1u << 1) | (1u << 2) | (1u << 3) | (1u << 4) | (1u << 6) | (1u << 7) | (1u << 8) | (1u << 10);
    private static readonly RowMapper<StartingSkillRow> Mapper = new();
    private readonly Dictionary<(uint Race, uint Class, ushort Skill), StartingSkillRow> _rows = [];
    private readonly SpecKeyReader _keys = new();
    private readonly MapDiagnostics _diagnostics = new();
    private readonly List<string> _skips = [];
    private int _skipped;
    private int _duplicates;

    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && row.Table.Equals(TableName, StringComparison.OrdinalIgnoreCase))
            {
                uint[] key = _keys.Read(row, TableName);
                StartingSkillRow mapped = Mapper.Map(row, _diagnostics);
                string? reason = null;
                if (key[0] != 0 && (key[0] & ~PlayableRaces) != 0) reason = $"raceMask {key[0]} contains non-playable bits";
                else if (key[1] != 0 && (key[1] & ~PlayableClasses) != 0) reason = $"classMask {key[1]} contains non-playable bits";
                else if (key[2] == 0 || key[2] > ushort.MaxValue) reason = "skill source value is outside the unsigned 16-bit range";
                else if (!row.TryGet(out string? rawStep, "step")
                    || !uint.TryParse(rawStep, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint step)
                    || step > SkillTierRecord.StepCount) reason = "step source value is outside 0..16";

                if (reason is not null)
                {
                    _skipped++;
                    if (_skips.Count == 0) _skips.Add($"raceMask {key[0]} classMask {key[1]} skill {mapped.Skill}: {reason}");
                    continue;
                }

                var identity = (key[0], key[1], mapped.Skill);
                if (_rows.ContainsKey(identity)) _duplicates++;
                _rows[identity] = mapped;
            }
        }
    }

    public IReadOnlyCollection<StartingSkillRow> Snapshot() => [.. _rows.Values];

    public StartingSkillImportReport BuildReport()
    {
        var warnings = new List<string>(_diagnostics.Samples);
        if (_skipped > 0) warnings.Add($"{TableName}: {_skipped} row(s) skipped (first: {_skips[0]})");
        if (_duplicates > 0) warnings.Add($"{TableName}: {_duplicates} duplicate key row(s) replaced in source order");
        return new StartingSkillImportReport(_rows.Count, _skipped, warnings);
    }

    public async Task<StartingSkillImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyCollection<StartingSkillRow> rows = Snapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            if (replace) await db.Set<StartingSkillEntity>().ExecuteDeleteAsync(token).ConfigureAwait(false);
            await ImportBatch.InsertAsync(db, rows.Select(r => new StartingSkillEntity
            {
                RaceMask = r.RaceMask, ClassMask = r.ClassMask, Skill = r.Skill, Step = r.Step, Note = r.Note,
            }).ToArray(), token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }
}
