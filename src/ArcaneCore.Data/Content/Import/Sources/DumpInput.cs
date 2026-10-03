namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// A named SQL dump input that can be opened any number of times (once to scan, once more to
/// feed an importer). <see cref="Name"/> is a file name, never a directory.
/// </summary>
public sealed record DumpInput(string Name, Func<TextReader> Open)
{
    /// <summary>A dump file on disk (plain or gzip, see <see cref="DumpFiles.OpenText"/>).</summary>
    public static DumpInput File(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new DumpInput(Path.GetFileName(path), () => DumpFiles.OpenText(path));
    }

    /// <summary>An in-memory dump (tests and generated overlays).</summary>
    public static DumpInput Text(string name, string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(text);
        return new DumpInput(name, () => new StringReader(text));
    }
}
