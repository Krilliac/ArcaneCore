using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// The JSON record of a run: which files (name, size, SHA-256), which <c>db_version</c>, each
/// table's dialect, row/key counts and mapped/unmapped columns, the statements that were not
/// applied, what was imported, and the license notice. It holds no source rows and no host
/// paths. Written next to the target database (never into the repository), and the same
/// report is produced by <c>plan</c> and a dry run, which write no rows.
/// </summary>
public sealed record ContentImportReport
{
    /// <summary>Version of the report layout and of the table specs it describes.</summary>
    public const int SpecVersion = 2;

    /// <summary>Printed in every report: the source data is not ours to relicense.</summary>
    public const string License =
        "Source data: cmaNGOS classic-db (GNU GPL v3), which contains World of Warcraft material that remains the copyright of "
        + "Blizzard Entertainment and its licensors (see classic-db COPYRIGHT.md). This report holds no source rows. "
        + "A database imported from it is derived data: keep it outside the repository and do not redistribute it.";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Tool { get; init; } = "arcane-content-importer";

    /// <summary>The <see cref="SpecVersion"/> the report was written with (JSON <c>specVersion</c>).</summary>
    [JsonPropertyName("specVersion")]
    public int SpecVersionValue => SpecVersion;

    public required string Command { get; init; }

    public bool DryRun { get; init; }

    public string? DbVersion { get; init; }

    public IReadOnlyList<SourceFileInfo> Inputs { get; init; } = [];

    public IReadOnlyDictionary<string, TableScan> Tables { get; init; } = new Dictionary<string, TableScan>();

    public IReadOnlyDictionary<string, int> UnappliedStatements { get; init; } = new Dictionary<string, int>();

    /// <summary>Rows written per target table by an import (empty for a plan or dry run).</summary>
    public IReadOnlyDictionary<string, long> Imported { get; init; } = new Dictionary<string, long>();

    /// <summary>Source rows an importer read but did not write, per table (patch filters, out-of-range ids).</summary>
    public IReadOnlyDictionary<string, long> Skipped { get; init; } = new Dictionary<string, long>();

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string LicenseNotice => License;

    public static ContentImportReport Create(
        string command, IReadOnlyList<SourceFileInfo> files, ScanResult scan, IReadOnlyList<string> warnings, bool dryRun)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(warnings);
        return new ContentImportReport
        {
            Command = command,
            DryRun = dryRun,
            DbVersion = scan.DbVersion,
            Inputs = files,
            Tables = scan.Tables,
            UnappliedStatements = scan.UnappliedStatements,
            Warnings = warnings,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, s_json);
}
