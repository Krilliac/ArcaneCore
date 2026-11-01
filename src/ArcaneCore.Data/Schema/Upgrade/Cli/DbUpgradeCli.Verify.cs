using System.Globalization;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema.Upgrade.Copy;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Schema.Upgrade.Cli;

public static partial class DbUpgradeCli
{
    private sealed partial class Run
    {
        /// <summary>
        /// <c>backup</c>: write a verified consistent copy (<see cref="BackupAdvisor.BackupSqliteAsync"/>: <c>VACUUM INTO</c>, then <c>integrity_check</c>) of each selected
        /// component's SQLite database as <c>&lt;component&gt;.db</c> in <c>--backup-dir</c>. The databases are the configured Database section, the same ones the daemons open;
        /// a relative <c>Data Source</c> resolves against this process's working directory, as it does for them. Server engines are refused (their own dump tools apply).
        /// Nothing is overwritten.
        /// </summary>
        public async Task<int> BackupAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            string directory = a.Value("--backup-dir") ?? throw new UsageException("backup needs --backup-dir <directory>");
            List<Target> targets = [.. Targets(a, readOnly: true)];
            foreach (Target target in targets)
            {
                try
                {
                    string file = await BackupAdvisor.BackupSqliteAsync(target.Connection, directory, $"{target.Spec.Name}.db", ct).ConfigureAwait(false);
                    await _out.WriteLineAsync($"backup {target.Spec.Name}: {Scrub(file)} (integrity ok)").ConfigureAwait(false);
                }
                catch (BackupException ex)
                {
                    await _err.WriteLineAsync($"error: {target.Spec.Name}: backup failed: {Scrub(ex.Message)}").ConfigureAwait(false);
                    return DbUpgradeExitCodes.BackupNotConfirmed;
                }
            }

            return DbUpgradeExitCodes.Ok;
        }

        /// <summary>
        /// <c>verify</c>: compare each selected component of the source (the configured Database section, or --source-*) with the target (ARCANE_DB_TARGET_* or
        /// --target-*), without writing anything: per table the row count and a digest of every value read through the model's column types. A table or model column
        /// missing on either side is a difference. Exit <see cref="DbUpgradeExitCodes.Ok"/> when every table matches, <see cref="DbUpgradeExitCodes.CopyVerifyFailed"/>
        /// when any differs.
        /// </summary>
        public async Task<int> VerifyAsync(DbUpgradeArguments a, CancellationToken ct)
        {
            IReadOnlyList<string> names = a.Components();
            if (names.Count != 1 && (a.Value("--source-connection") is not null || a.Value("--target-connection") is not null))
            {
                throw new UsageException(
                    "--source-connection and --target-connection name one database, so they need --component auth, characters or world; " +
                    "for --component all use the configured Database section and ARCANE_DB_TARGET_<COMPONENT>_CONNECTION variables");
            }

            var ends = new List<(Spec Spec, CopyEnd Source, CopyEnd Target)>();
            foreach (string name in names)
            {
                Spec spec = s_specs.Single(s => s.Name == name);
                CopyEnd source = ResolveCopyEnd(spec, a, isSource: true);
                CopyEnd target = ResolveCopyEnd(spec, a, isSource: false, readOnlyTarget: true);
                if (SameDatabase(source.Target, target.Target))
                {
                    throw new UsageException($"the {name} source and target are the same database ({source.Target.Description}); verify compares two databases");
                }

                ends.Add((spec, source, target));
            }

            await _out.WriteLineAsync($"arcane-db verify (ArcaneCore.Data {typeof(DbUpgradeCli).Assembly.GetName().Version})").ConfigureAwait(false);
            int differences = 0;
            int tableLines = 0;
            foreach ((Spec spec, CopyEnd source, CopyEnd target) in ends)
            {
                await _out.WriteLineAsync($"{spec.Name}: {ProviderName(source.Target.Connection)} {source.Target.Description} vs {ProviderName(target.Target.Connection)} {target.Target.Description}").ConfigureAwait(false);
                (int bad, int lines) = await GuardAsync(source.Target, ct, sdb => GuardAsync(target.Target, ct, tdb => VerifyComponentAsync(spec, sdb, tdb, ct))).ConfigureAwait(false);
                differences += bad;
                tableLines += lines;
            }

            await _out.WriteLineAsync($"verify: {tableLines.ToString(CultureInfo.InvariantCulture)} table line(s), {differences.ToString(CultureInfo.InvariantCulture)} difference(s)").ConfigureAwait(false);
            if (differences > 0)
            {
                await _err.WriteLineAsync($"error: verify found {differences} table(s) that differ; nothing was changed").ConfigureAwait(false);
                return DbUpgradeExitCodes.CopyVerifyFailed;
            }

            return DbUpgradeExitCodes.Ok;
        }

        private static string? StructureProblem(CopyTable table, Dictionary<string, HashSet<string>> present, string role)
        {
            if (!present.TryGetValue(table.Name, out HashSet<string>? columns))
            {
                return $"missing in the {role}";
            }

            string[] lacking = [.. table.Columns.Where(c => !columns.Contains(c))];
            return lacking.Length == 0 ? null : $"the {role} lacks column(s) {string.Join(", ", lacking)}";
        }

        private async Task<(int Differences, int Lines)> VerifyComponentAsync(Spec spec, DbContext sdb, DbContext tdb, CancellationToken ct)
        {
            IReadOnlyList<CopyTable> tables = DatabaseCopy.TablesOf(sdb, spec.Schema);
            Dictionary<string, HashSet<string>> sourceColumns = await DatabaseCopy.ReadColumnsAsync(sdb, tables.Select(t => t.Name), ct).ConfigureAwait(false);
            Dictionary<string, HashSet<string>> targetColumns = await DatabaseCopy.ReadColumnsAsync(tdb, tables.Select(t => t.Name), ct).ConfigureAwait(false);

            var problems = new Dictionary<string, string>(StringComparer.Ordinal);
            var comparable = new List<CopyTable>();
            foreach (CopyTable table in tables)
            {
                string? problem = StructureProblem(table, sourceColumns, "source") ?? StructureProblem(table, targetColumns, "target");
                if (problem is null)
                {
                    comparable.Add(table);
                }
                else
                {
                    problems[table.Name] = problem;
                }
            }

            IReadOnlyList<CopyTableResult> results = await DatabaseCopy.VerifyAsync(sdb, tdb, comparable, ct).ConfigureAwait(false);
            Dictionary<string, CopyTableResult> byName = results.ToDictionary(r => r.Table, StringComparer.Ordinal);
            int bad = 0;
            foreach (CopyTable table in tables)
            {
                string label = $"{spec.Name}.{table.Name}";
                if (problems.TryGetValue(table.Name, out string? structure))
                {
                    await _out.WriteLineAsync($"FAIL {label}  {structure}").ConfigureAwait(false);
                    bad++;
                    continue;
                }

                CopyTableResult result = byName[table.Name];
                if (result.IsOk)
                {
                    await _out.WriteLineAsync($"OK   {label}  rows={result.SourceRows.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
                }
                else
                {
                    string why = result.SourceRows != result.TargetRows ? "ROW COUNT DIFFERS" : "CONTENT DIFFERS";
                    await _out.WriteLineAsync(
                        $"FAIL {label}  source rows={result.SourceRows.ToString(CultureInfo.InvariantCulture)}  target rows={result.TargetRows.ToString(CultureInfo.InvariantCulture)}  {why}").ConfigureAwait(false);
                    bad++;
                }

                if (spec.Name == "world" && table.Name == "creature_template" && result.SourceRows == 0)
                {
                    await _out.WriteLineAsync("WARN world.creature_template is empty in the source: the copy carries no world content; populate it with arcane-content-importer (docs/areas/content-import.md)").ConfigureAwait(false);
                }
            }

            return (bad, tables.Count);
        }
    }
}
