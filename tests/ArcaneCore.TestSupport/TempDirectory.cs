using Microsoft.Data.Sqlite;

namespace ArcaneCore.TestSupport;

/// <summary>
/// A scratch directory under the system temp path, created on construction and deleted on dispose. A file held open by a scan
/// must not fail a test that already passed, so a delete that hits <see cref="IOException"/> is dropped. With
/// <c>sqlite: true</c> the directory holds SQLite databases: dispose first releases the pooled connections
/// (<see cref="SqliteConnection.ClearAllPools"/>) and also drops <see cref="UnauthorizedAccessException"/>, which a
/// still-mapped database file raises on Windows.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    private readonly bool _sqlite;

    public TempDirectory(string prefix, bool sqlite = false)
    {
        _sqlite = sqlite;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (_sqlite)
        {
            SqliteConnection.ClearAllPools();
        }

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException) when (_sqlite)
        {
        }
    }
}
