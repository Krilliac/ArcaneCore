using ArcaneCore.Data.World.Warden;

namespace ArcaneCore.World.Warden;

/// <summary>Turns <c>warden_checks</c> rows into scans (vmangos WardenScanMgr::LoadFromDB) and merges them with <c>Warden:Checks</c>.</summary>
public static class WardenCheckRows
{
    /// <summary>
    /// The scan a row describes, or null with <paramref name="problem"/> when the row is for another build, is an API-hook scan (not
    /// ported) or does not parse. A <c>Penalty</c> of -1 (or out of range) uses the configured action, as vmangos falls back to
    /// Warden.DefaultPenalty.
    /// </summary>
    public static WardenCheckOptions? ToOptions(WardenCheckRow row, uint build, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(row);
        problem = null;
        if (build < row.BuildMin || build > row.BuildMax)
        {
            return null;
        }

        bool wanted = row.Result.Trim() is "1";
        var check = new WardenCheckOptions
        {
            Id = row.Id,
            Comment = row.Comment,
            Address = row.Address,
            Action = row.Penalty is >= 0 and <= 2 ? (WardenAction)row.Penalty : null,
            Wanted = wanted,
        };
        switch (row.Type)
        {
            case 0:
                check.Kind = WardenCheckKind.Memory;
                check.Module = row.Str ?? string.Empty;
                check.Expected = row.Result;
                break;
            case 1:
                check.Kind = WardenCheckKind.ModuleByName;
                check.Module = row.Str ?? string.Empty;
                break;
            case 2 or 3:
                check.Kind = row.Type == 2 ? WardenCheckKind.PageA : WardenCheckKind.PageB;
                check.Pattern = row.Data ?? string.Empty;
                break;
            case 4:
                check.Kind = WardenCheckKind.Mpq;
                check.Path = row.Str ?? string.Empty;
                check.Expected = row.Result;
                break;
            case 5:
                check.Kind = WardenCheckKind.Lua;
                check.Path = row.Str ?? string.Empty;
                check.Expected = row.Data ?? string.Empty;
                break;
            case 7:
                check.Kind = WardenCheckKind.Driver;
                check.DriverName = row.Str ?? string.Empty;
                check.DriverPath = row.Data ?? string.Empty;
                break;
            case 8:
                check.Kind = WardenCheckKind.Timing;
                break;
            default:
                problem = $"type {row.Type} is not run";
                return null;
        }

        if (row.Type == 0 && Convert.FromHexString(row.Result.Length % 2 == 0 && row.Result.All(Uri.IsHexDigit) ? row.Result : string.Empty).Length != row.Length)
        {
            problem = "the expected bytes do not match the length";
            return null;
        }

        problem = WardenCheck.TryCreate(check, out _);
        return problem is null ? check : null;
    }

    /// <summary>The table's scans for <paramref name="build"/>, then the configured ones; a configured id replaces the row with that id.</summary>
    public static List<WardenCheckOptions> Merge(IEnumerable<WardenCheckRow> rows, IEnumerable<WardenCheckOptions> configured, uint build, Action<WardenCheckRow, string>? skipped = null)
    {
        var byId = new SortedDictionary<uint, WardenCheckOptions>();
        foreach (WardenCheckRow row in rows)
        {
            if (ToOptions(row, build, out string? problem) is { } check)
            {
                byId[check.Id] = check;
            }
            else if (problem is not null)
            {
                skipped?.Invoke(row, problem);
            }
        }

        foreach (WardenCheckOptions check in configured)
        {
            byId[check.Id] = check;
        }

        return [.. byId.Values];
    }
}
