using ArcaneCore.Data.Content;

namespace ArcaneCore.Data.Content.Import;

public static partial class ContentImporterCli
{
    /// <summary>Reimport portal rows into an existing world whose earlier import predates the requirement columns.</summary>
    private static async Task<int> RefreshPortalsAsync(CliArguments a, TextWriter o, CancellationToken ct)
    {
        string dialect = ParseDialect(a);
        RequireInputs(a);
        bool dryRun = a.Flag("--dry-run");
        Target? target = dryRun && a.Value("--database") is null && a.Value("--provider") is null ? null : ResolveTarget(a);
        if (!dryRun && target is null)
            throw new UsageException("refresh-portals needs a target: --database <file>, or --provider with --connection-string");

        if (target?.FilePath is { } file)
        {
            GuardPath(file);
            if (!File.Exists(file))
                throw new CliException(ExitCodes.Io, $"database '{file}' does not exist (refresh-portals updates an existing world database)");
        }

        (IReadOnlyList<DumpInput> inputs, IReadOnlyList<SourceFileInfo> files) = OpenInputs(a.Positional);
        ScanResult scan = ContentScanner.Scan(inputs);
        CheckDialect(scan, dialect);
        var importer = new LocationDumpImporter();
        using (TextReader reader = ChainedTextReader.Create(inputs))
            importer.Read(reader);

        int count = importer.Snapshot().Portals.Count;
        if (count == 0)
            throw new CliException(ExitCodes.Schema, "the dump has no areatrigger_teleport rows; nothing was changed");

        foreach (SourceFileInfo info in files)
            o.WriteLine($"input {info.Name}  {info.Bytes} bytes  sha256 {info.Sha256}");
        if (dryRun)
        {
            o.WriteLine($"{count} portal(s) would replace the table (dry run: no database written)");
            return ExitCodes.Ok;
        }

        o.WriteLine($"target: {target!.Describe}");
        try
        {
            await using WorldDbContext db = OpenWorld(target);
            await EnsureRefreshSchemaAsync(db, migrate: false, o, ct).ConfigureAwait(false);
            await importer.ReplacePortalsAsync(db, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or CliException or ImportSchemaException))
        {
            throw DatabaseError(ex, target);
        }

        o.WriteLine($"{count} portal(s) replaced; reload areatrigger_teleport or restart the world to use them");
        return ExitCodes.Ok;
    }
}
