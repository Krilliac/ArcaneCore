namespace ArcaneCore.Kernel.Configuration;

/// <summary>
/// The <c>ClientData</c> configuration section: where the build-5875 client data files are (docs/areas/client-data.md).
/// Read once at start by the world daemon (before any feature binds its options) and by <c>arcane-db dbc</c>.
/// </summary>
public sealed class ClientDataOptions
{
    public const string SectionName = "ClientData";

    /// <summary>
    /// A directory holding the client's DBFilesClient *.dbc files (build 5875, extracted by the developer; none ships with the server).
    /// Set, every DBC consumer whose own path key (for example <c>Combat:ShapeshiftFormDbcPath</c>) is unset or empty reads
    /// the file of that name in this directory (for example SpellShapeshiftForm.dbc); a key that is set still wins. Each file is checked at start against the vmangos
    /// layout (field count and record size) and logged on one line: loaded, missing or format mismatch. A missing or mismatched
    /// file is not handed to its consumer, which keeps its built-in table or stays off, with a warning. Empty (the default): only the
    /// per-file keys are read, as before.
    /// </summary>
    public string DbcDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Make a client data problem fatal: a missing or mismatched DBC under <see cref="DbcDirectory"/>, a configured per-file path
    /// whose file is missing or has another layout, or a <see cref="DbcDirectory"/> that does not exist refuses start-up (exit
    /// code 78, like any configuration error) instead of a warning. Default false.
    /// </summary>
    public bool Strict { get; set; }
}
