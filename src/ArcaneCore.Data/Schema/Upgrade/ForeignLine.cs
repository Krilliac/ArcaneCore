using Microsoft.EntityFrameworkCore;

namespace ArcaneCore.Data.Schema.Upgrade;

/// <summary>A table, or one column of a table, whose presence shows that a schema step ran.</summary>
public sealed record SchemaObject(string Table, string? Column = null)
{
    public override string ToString() => Column is null ? Table : Table + "." + Column;
}

/// <summary>One schema step of another development line, as that line numbered and recorded it.</summary>
/// <param name="Version">The version the other line wrote after the step.</param>
/// <param name="MergedVersion">
/// The version of the same step in this build's numbering; null when the merge dropped the step (its rows are carried
/// over by a <see cref="ForeignLineDataMove"/> and its table is left in place).
/// </param>
/// <param name="Module">The module that owned the step on the other line (for messages).</param>
/// <param name="Objects">What the step created; every object present means the step ran.</param>
public sealed record ForeignLineStep(int Version, int? MergedVersion, string Module, IReadOnlyList<SchemaObject> Objects);

/// <summary>
/// Rows a foreign step kept somewhere this build does not read, moved into this build's schema after the merged
/// steps ran. Both delegates must be idempotent and portable SQL (they run inside the bootstrap's lock, and on
/// SQLite inside its transaction).
/// </summary>
/// <param name="ForeignVersion">The foreign step whose rows move; the move runs only when the database recorded that step.</param>
/// <param name="AfterMergedVersion">
/// The step of this build that creates what the rows move into; the move runs right after it, before any later step, so
/// that a migration interrupted on an engine without transactional DDL never leaves every step of this build in place
/// with the move undone (such a database reads as this build's own line and is not migrated again).
/// </param>
/// <param name="Description">What moves where (plans and logs).</param>
/// <param name="CountAsync">Read-only: how many rows the move would write (the dry run).</param>
/// <param name="ApplyAsync">The move; returns the rows written.</param>
public sealed record ForeignLineDataMove(
    int ForeignVersion,
    int AfterMergedVersion,
    string Description,
    Func<DbContext, CancellationToken, Task<long>> CountAsync,
    Func<DbContext, CancellationToken, Task<long>> ApplyAsync);

/// <summary>
/// The schema numbering of another development line that shares this build's history up to
/// <see cref="DivergedAfter"/> and then allocated the same version numbers to different steps. A database that line
/// created records its own numbers, so this build's step loop would read them as its own steps and skip the ones it
/// needs. <see cref="ForeignLineDetector"/> recognises such a database by the objects each line's steps create, and
/// <see cref="SchemaBootstrapper"/> migrates it once to this build's numbering (docs/integration/codex-merge-20261007.md).
/// </summary>
public sealed class ForeignLine
{
    /// <summary>The line's name in messages and logs.</summary>
    public required string Name { get; init; }

    /// <summary>The last version both lines share (identical steps 2..DivergedAfter).</summary>
    public required int DivergedAfter { get; init; }

    /// <summary>The line's steps after the divergence, contiguous from <see cref="DivergedAfter"/> + 1.</summary>
    public required IReadOnlyList<ForeignLineStep> Steps { get; init; }

    /// <summary>Rows of dropped steps that move into this build's schema.</summary>
    public IReadOnlyList<ForeignLineDataMove> DataMoves { get; init; } = [];

    /// <summary>The highest version the line ever recorded.</summary>
    public int LastVersion => Steps.Max(s => s.Version);

    /// <summary>
    /// The version in this build's numbering that a database the line left at <paramref name="foreignVersion"/>
    /// corresponds to: the merged version of the highest kept step it ran.
    /// </summary>
    public int MergedVersionAt(int foreignVersion)
        => Steps.Where(s => s.Version <= foreignVersion && s.MergedVersion is not null).Select(s => s.MergedVersion!.Value)
               .DefaultIfEmpty(0).Max() is var merged and > 0
            ? merged
            : throw new InvalidOperationException($"{Name}: version {foreignVersion} holds no step this build kept");
}

/// <summary>A database recognised as created by a <see cref="ForeignLine"/>, and what its one-shot migration does.</summary>
/// <param name="Line">The line that created it.</param>
/// <param name="ForeignVersion">The version that line recorded.</param>
/// <param name="MergedVersion">The version the migration records (this build's numbering); later steps follow as an ordinary upgrade.</param>
/// <param name="Steps">This build's steps after the divergence up to <paramref name="MergedVersion"/>, applied idempotently (the foreign line's own tables are verified, the missing ones created).</param>
/// <param name="DataMoves">The data moves the recorded steps call for.</param>
/// <param name="Evidence">The foreign steps found, for messages.</param>
public sealed record ForeignLineMatch(
    ForeignLine Line,
    int ForeignVersion,
    int MergedVersion,
    IReadOnlyList<SchemaStep> Steps,
    IReadOnlyList<ForeignLineDataMove> DataMoves,
    IReadOnlyList<string> Evidence)
{
    /// <summary>One sentence: what the migration does.</summary>
    public string Describe(string component)
        => $"migrate the {component} database from the {Line.Name} numbering (schema version {ForeignVersion}) to this build's numbering " +
           $"(schema version {MergedVersion}): verify the steps it holds, apply the steps {Line.DivergedAfter + 1}-{MergedVersion} it lacks" +
           (DataMoves.Count == 0 ? string.Empty : "; " + string.Join("; ", DataMoves.Select(m => m.Description)));
}

/// <summary>The outcome of <see cref="ForeignLineDetector.DetectAsync"/>: no foreign line, a match, or a refusal.</summary>
public sealed record ForeignLineDetection(ForeignLineMatch? Match, string? Refusal)
{
    public static readonly ForeignLineDetection None = new(null, null);
}

/// <summary>
/// Read-only recognition of a database another line created (<see cref="ForeignLine"/>). Only a version row inside a
/// line's range (after the divergence, up to its last version) is examined; there the two lines numbered different
/// steps, so the catalog decides:
/// <list type="bullet">
/// <item>every object of this build's steps up to the recorded version present: this build's line (tables of later
/// steps may exist too; the ordinary upgrade verifies them), not migrated;</item>
/// <item>otherwise the other line's steps up to the recorded version all present and none after it: that line, migrated;</item>
/// <item>anything else matches neither line and is refused, before anything is written.</item>
/// </list>
/// </summary>
public static class ForeignLineDetector
{
    public static async Task<ForeignLineDetection> DetectAsync(
        DbContext db, SchemaDefinition definition, int recordedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(definition);
        foreach (ForeignLine line in definition.ForeignLines)
        {
            if (recordedVersion <= line.DivergedAfter || recordedVersion > line.LastVersion)
            {
                continue;
            }

            string? ownGap = await OwnLineGapAsync(db, definition, line, recordedVersion, cancellationToken).ConfigureAwait(false);
            if (ownGap is null)
            {
                continue;
            }

            var present = new List<ForeignLineStep>();
            var partial = new List<(ForeignLineStep Step, IReadOnlyList<SchemaObject> Missing)>();
            foreach (ForeignLineStep step in line.Steps.OrderBy(s => s.Version))
            {
                IReadOnlyList<SchemaObject> missing = await MissingAsync(db, step.Objects, cancellationToken).ConfigureAwait(false);
                if (missing.Count == 0)
                {
                    present.Add(step);
                }
                else if (missing.Count < step.Objects.Count)
                {
                    partial.Add((step, missing));
                }
            }

            if (present.Count == 0 && partial.Count == 0)
            {
                return new ForeignLineDetection(null,
                    $"The {definition.Component} database records schema version {recordedVersion}, a number both this build and the {line.Name} line " +
                    $"used for different steps, but it holds neither line's tables ({ownGap}; no table of the {line.Name} line either). " +
                    "Nothing was changed; restore it from a backup or recreate it.");
            }

            ForeignLineStep[] missingEarly = [.. line.Steps.Where(s => s.Version <= recordedVersion && !present.Contains(s) && partial.All(p => p.Step != s))];
            ForeignLineStep[] late = [.. present.Where(s => s.Version > recordedVersion)];
            if (partial.Count > 0 || missingEarly.Length > 0 || late.Length > 0)
            {
                var problems = new List<string>();
                problems.AddRange(partial.Select(p => $"step {p.Step.Version} ({p.Step.Module}) is incomplete, missing {string.Join(", ", p.Missing)}"));
                problems.AddRange(missingEarly.Select(s => $"step {s.Version} ({s.Module}) is recorded but absent"));
                problems.AddRange(late.Select(s => $"step {s.Version} ({s.Module}) is present but not recorded"));
                return new ForeignLineDetection(null,
                    $"The {definition.Component} database records schema version {recordedVersion} and holds tables of the {line.Name} line, " +
                    $"but not as that line leaves them: {string.Join("; ", problems)}. Nor is it this build's own line ({ownGap}). " +
                    "It matches neither line; nothing was changed. Restore it from a backup taken before it was changed by hand, or recreate it.");
            }

            int merged = line.MergedVersionAt(recordedVersion);
            if (merged > definition.CurrentVersion)
            {
                return new ForeignLineDetection(null,
                    $"The {definition.Component} database was created by the {line.Name} line at schema version {recordedVersion}, which this build's " +
                    $"numbering places at version {merged}, newer than this build ({definition.CurrentVersion}). Nothing was changed.");
            }

            SchemaStep[] steps = [.. definition.Steps.Where(s => s.Version > line.DivergedAfter && s.Version <= merged).OrderBy(s => s.Version)];
            ForeignLineDataMove[] moves = [.. line.DataMoves.Where(m => m.ForeignVersion <= recordedVersion)];
            string[] evidence = [.. present.Select(s => $"{line.Name} step {s.Version} ({s.Module}) -> " +
                (s.MergedVersion is { } v ? $"version {v}" : "dropped by the merge"))];
            return new ForeignLineDetection(new ForeignLineMatch(line, recordedVersion, merged, steps, moves, evidence), null);
        }

        return ForeignLineDetection.None;
    }

    /// <summary>What this build's own steps after the divergence up to the recorded version would have created but is missing; null when complete.</summary>
    private static async Task<string?> OwnLineGapAsync(
        DbContext db, SchemaDefinition definition, ForeignLine line, int recordedVersion, CancellationToken ct)
    {
        var gaps = new List<string>();
        foreach (SchemaStep step in definition.Steps.Where(s => s.Version > line.DivergedAfter && s.Version <= recordedVersion).OrderBy(s => s.Version))
        {
            IReadOnlyList<SchemaObject> missing = await MissingAsync(db, ObjectsOf(step), ct).ConfigureAwait(false);
            if (missing.Count > 0)
            {
                gaps.Add($"this build's step {step.Version} lacks {string.Join(", ", missing)}");
            }
        }

        return gaps.Count == 0 ? null : string.Join("; ", gaps);
    }

    /// <summary>The tables and columns a step of this build creates (index repairs create nothing identifying).</summary>
    public static IReadOnlyList<SchemaObject> ObjectsOf(SchemaStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return [.. step.Changes.Select(c => c switch
        {
            CreateTableChange t => new SchemaObject(t.Table),
            AddColumnChange a => new SchemaObject(a.Table, a.Column),
            _ => null,
        }).OfType<SchemaObject>()];
    }

    private static async Task<IReadOnlyList<SchemaObject>> MissingAsync(DbContext db, IReadOnlyList<SchemaObject> objects, CancellationToken ct)
    {
        var missing = new List<SchemaObject>();
        foreach (SchemaObject o in objects)
        {
            bool exists = o.Column is null
                ? await SchemaCatalog.TableExistsAsync(db, o.Table, ct).ConfigureAwait(false)
                : await SchemaCatalog.TableExistsAsync(db, o.Table, ct).ConfigureAwait(false)
                  && await SchemaCatalog.ColumnExistsAsync(db, o.Table, o.Column, ct).ConfigureAwait(false);
            if (!exists)
            {
                missing.Add(o);
            }
        }

        return missing;
    }
}
