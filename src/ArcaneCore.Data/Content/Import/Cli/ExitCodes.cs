namespace ArcaneCore.Data.Content.Import;

/// <summary>Process exit codes of the content importer.</summary>
public static class ExitCodes
{
    /// <summary>Success.</summary>
    public const int Ok = 0;

    /// <summary>An input could not be found or read: a missing or malformed dump or DBC.</summary>
    public const int Io = 1;

    /// <summary>Bad command line: unknown command or flag, missing value or argument.</summary>
    public const int Usage = 2;

    /// <summary>A source table has the wrong shape or dialect (<see cref="ImportSchemaException"/>) or is not in the dialect asked for.</summary>
    public const int Schema = 3;

    /// <summary>The target database refused the connection or the write; nothing was changed.</summary>
    public const int Database = 4;

    /// <summary>The database or report path is inside a git work tree and not git-ignored (GPL data must stay out of the repository).</summary>
    public const int RepositoryPath = 5;

    /// <summary><c>verify</c> found rows that reference content that does not exist.</summary>
    public const int Verify = 6;
}
