using ArcaneCore.Data.Reputation;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Reputation;
using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Content.Import;

/// <summary>What a reputation-template import read and wrote.</summary>
public sealed record ReputationTemplatesImportReport(int Spillovers, int RewardRates, IReadOnlyList<string> Warnings);

/// <summary>
/// Maps <c>reputation_spillover_template</c> and <c>reputation_reward_rate</c> of a cmangos classic-db or vmangos dump
/// into <see cref="ReputationSpilloverTemplateRow"/> and <see cref="ReputationRewardRateRow"/> by column name
/// (<c>rate_1</c> = <c>Rate1</c>). Neither table is patch-versioned in either source (vmangos
/// <c>ObjectMgr::LoadReputationRewardRate</c> and <c>LoadReputationSpilloverTemplate</c>, ObjectMgr.cpp:8827-9070, read plain
/// <c>SELECT</c>s), so the layouts are identical. Rows naming a faction that is not in Faction.dbc are kept: vmangos drops
/// them at load, and <c>ReputationContent.Create</c> does the same with a warning when the world starts. A repeated key
/// keeps the later row. The dumps are GPL data and are never committed.
/// </summary>
/// <remarks>State: single-threaded accumulator; call <see cref="Read"/> from one thread, then <see cref="WriteAsync"/>.</remarks>
public sealed class ReputationTemplatesDumpImporter
{
    private const string SpilloverTable = "reputation_spillover_template";
    private const string RateTable = "reputation_reward_rate";

    private static readonly RowMapper<ReputationSpilloverTemplateRow> s_spilloverMapper = new();
    private static readonly RowMapper<ReputationRewardRateRow> s_rateMapper = new();

    private readonly SpecKeyReader _keys = new();
    private readonly Dictionary<uint, ReputationSpilloverTemplateRow> _spillovers = [];
    private readonly Dictionary<uint, ReputationRewardRateRow> _rates = [];
    private readonly MapDiagnostics _diagnostics = new();
    private int _duplicateSpillovers;
    private int _duplicateRates;

    /// <summary>Whether a column of <c>reputation_spillover_template</c> is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsSpilloverColumn(string column) => s_spilloverMapper.Maps(column);

    /// <summary>Whether a column of <c>reputation_reward_rate</c> is read (the spec's mapped-column rule).</summary>
    internal static bool ReadsRateColumn(string column) => s_rateMapper.Maps(column);

    /// <summary>Read one dump (call again for further files; later rows replace earlier ones with the same key).</summary>
    public void Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is not DumpRow row)
            {
                continue;
            }

            if (row.Table.Equals(SpilloverTable, StringComparison.OrdinalIgnoreCase))
            {
                uint faction = _keys.Read(row, SpilloverTable)[0];
                ReputationSpilloverTemplateRow mapped = s_spilloverMapper.Map(row, _diagnostics);
                mapped.Faction = faction;
                if (!_spillovers.TryAdd(faction, mapped))
                {
                    _duplicateSpillovers++;
                    _spillovers[faction] = mapped;
                }
            }
            else if (row.Table.Equals(RateTable, StringComparison.OrdinalIgnoreCase))
            {
                uint faction = _keys.Read(row, RateTable)[0];
                ReputationRewardRateRow mapped = s_rateMapper.Map(row, _diagnostics);
                mapped.Faction = faction;
                if (!_rates.TryAdd(faction, mapped))
                {
                    _duplicateRates++;
                    _rates[faction] = mapped;
                }
            }
        }
    }

    /// <summary>The spillover rows that would be written, ordered by faction (for inspection and tests).</summary>
    public IReadOnlyList<ReputationSpilloverTemplateRow> SpilloverSnapshot() => [.. _spillovers.Values.OrderBy(r => r.Faction)];

    /// <summary>The reward-rate rows that would be written, ordered by faction (for inspection and tests).</summary>
    public IReadOnlyList<ReputationRewardRateRow> RateSnapshot() => [.. _rates.Values.OrderBy(r => r.Faction)];

    /// <summary>The rows as the reputation feature consumes them (what <see cref="EfReputationContentSource"/> loads after a write).</summary>
    public ReputationContentRows ToContentRows() => EfReputationContentSource.ToRows(SpilloverSnapshot(), RateSnapshot());

    public ReputationTemplatesImportReport BuildReport()
    {
        var warnings = new List<string>(_diagnostics.Samples);
        if (_duplicateSpillovers > 0)
        {
            warnings.Add($"{SpilloverTable}: {_duplicateSpillovers} duplicate key row(s) replaced in source order");
        }

        if (_duplicateRates > 0)
        {
            warnings.Add($"{RateTable}: {_duplicateRates} duplicate key row(s) replaced in source order");
        }

        return new ReputationTemplatesImportReport(_spillovers.Count, _rates.Count, warnings);
    }

    /// <summary>
    /// Write the rows atomically with the contract of <see cref="ImportTransaction"/>. With <paramref name="replace"/> both
    /// tables are emptied first; without it an existing key fails the write and nothing changes.
    /// </summary>
    public async Task<ReputationTemplatesImportReport> WriteAsync(WorldDbContext db, bool replace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        IReadOnlyList<ReputationSpilloverTemplateRow> spillovers = SpilloverSnapshot();
        IReadOnlyList<ReputationRewardRateRow> rates = RateSnapshot();
        await ImportTransaction.RunAsync(db, async token =>
        {
            bool detect = db.ChangeTracker.AutoDetectChangesEnabled;
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                if (replace)
                {
                    await db.Set<ReputationSpilloverTemplateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                    await db.Set<ReputationRewardRateRow>().ExecuteDeleteAsync(token).ConfigureAwait(false);
                }

                await ImportBatch.InsertAsync(db, spillovers, token).ConfigureAwait(false);
                await ImportBatch.InsertAsync(db, rates, token).ConfigureAwait(false);
            }
            finally
            {
                db.ChangeTracker.AutoDetectChangesEnabled = detect;
            }
        }, cancellationToken).ConfigureAwait(false);
        return BuildReport();
    }
}
