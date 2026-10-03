namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// A source table does not have the shape the importers need: a required key column is missing
/// or empty, the keys collapsed, or no single dialect matches its columns. Thrown before any
/// row is written, naming the table (and the column when one is at fault).
/// </summary>
public sealed class ImportSchemaException : Exception
{
    public ImportSchemaException(string table, string? column, string message)
        : base(message)
    {
        Table = table;
        Column = column;
    }

    public ImportSchemaException()
        : this(string.Empty, null, "import schema error")
    {
    }

    public ImportSchemaException(string message)
        : this(string.Empty, null, message)
    {
    }

    public ImportSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
        Table = string.Empty;
    }

    /// <summary>The source table at fault.</summary>
    public string Table { get; }

    /// <summary>The canonical name of the column at fault, or <c>null</c> when the table as a whole is.</summary>
    public string? Column { get; }
}
