namespace ArcaneCore.World.HotCode;

/// <summary>
/// Append-only audit trail of hot-code decisions: one tab-separated line per event
/// (UTC timestamp, kind, detail). A path of null or empty disables the file. The file lives
/// in the same account the server runs as, so it records what happened; it is not tamper-proof.
/// </summary>
public sealed class HotCodeAudit(string? path, Func<DateTimeOffset>? clock = null)
{
    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool HasFile => !string.IsNullOrWhiteSpace(path);

    public void Record(string kind, string detail)
    {
        if (!HasFile)
        {
            return;
        }

        string line = $"{_clock():O}\t{kind}\t{detail.ReplaceLineEndings(" ")}{Environment.NewLine}";
        lock (_gate)
        {
            string full = Path.GetFullPath(path!);
            if (Path.GetDirectoryName(full) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(full, line);
        }
    }
}
