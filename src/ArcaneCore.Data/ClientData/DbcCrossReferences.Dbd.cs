using System.Buffers.Binary;

namespace ArcaneCore.Data.ClientData;

public static partial class DbcCrossReferences
{
    /// <summary>The heading of the <see cref="RunDbc"/> report lines (<see cref="Lines"/>).</summary>
    public const string ClientInternalTitle = "Client DBC internal references (diagnostic, not drift)";

    /// <summary>
    /// Check build-5875 DBC foreign keys (WoWDBDefs COLUMNS, <see cref="ClientDbcDbdLayouts"/>) whose target is another extracted
    /// DBC with an ID column; zero and all-ones values are unset markers. These are the client's own data, and WoWDBDefs
    /// annotations need not hold for this build (the generator overrides two: FactionTemplate.FactionGroup is a mask, Map.ParentMapID
    /// holds AreaTable ids), so callers report the result as a diagnostic and never as world-database drift.
    /// </summary>
    public static IReadOnlyList<DbcReferenceResult> RunDbc(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var results = new List<DbcReferenceResult>();
        var checks = new Dictionary<string, ClientDbcFileCheck>(StringComparer.OrdinalIgnoreCase);
        var ids = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        ClientDbcFileCheck Check(string file)
        {
            if (!checks.TryGetValue(file, out ClientDbcFileCheck? check))
            {
                check = ClientDbcInspector.Check(Path.Combine(directory, file));
                checks[file] = check;
            }

            return check;
        }

        foreach (DbdLayout source in ClientDbcDbdLayouts.All.Values.OrderBy(l => l.File, StringComparer.OrdinalIgnoreCase))
        {
            foreach (DbdField field in source.Columns.Where(c => c.ForeignTable is not null && c.ForeignColumn == "ID"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string targetName = field.ForeignTable + ".dbc";
                if (!ClientDbcDbdLayouts.All.TryGetValue(targetName, out DbdLayout? target))
                {
                    continue; // No build-5875 DBC target in the extracted client set.
                }

                var reference = new DbcReference(targetName, source.File, field.Name);
                ClientDbcFileCheck sourceCheck = Check(source.File);
                ClientDbcFileCheck targetCheck = Check(targetName);
                if (!sourceCheck.IsUsable || !targetCheck.IsUsable)
                {
                    string reason = !sourceCheck.IsUsable ? source.File + " " + sourceCheck.Describe() : targetName + " " + targetCheck.Describe();
                    results.Add(new DbcReferenceResult(reference, DbcReferenceStatus.Skipped, 0, 0, 0, [], reason));
                    continue;
                }

                DbdField? id = target.Columns.FirstOrDefault(c => c.Name == "ID")
                    ?? target.Columns.FirstOrDefault(c => c.Name == Path.GetFileNameWithoutExtension(target.File) + "ID");
                if (id is null || id.Type != "int" || field.Type != "int"
                    || !SupportedWidth(id.WidthBits) || !SupportedWidth(field.WidthBits))
                {
                    results.Add(new DbcReferenceResult(reference, DbcReferenceStatus.Skipped, 0, 0, 0, [], "unsupported field width or target ID"));
                    continue;
                }

                if (!ids.TryGetValue(targetName, out HashSet<uint>? targetIds))
                {
                    targetIds = new HashSet<uint>();
                    VisitRecords(targetCheck, target, id.Offset, 1, id.WidthBits, (_, value) => targetIds.Add(value));
                    ids[targetName] = targetIds;
                }

                var counts = new Dictionary<uint, long>();
                int lastRow = -1;
                var seenInRow = new HashSet<uint>();
                VisitRecords(sourceCheck, source, field.Offset, field.ArrayLength, field.WidthBits, (row, value) =>
                {
                    if (row != lastRow)
                    {
                        seenInRow.Clear();
                        lastRow = row;
                    }

                    if (value != 0 && value != EmptyMarker(field.WidthBits))
                    {
                        if (seenInRow.Add(value))
                        {
                            counts[value] = counts.GetValueOrDefault(value) + 1;
                        }
                    }
                });
                uint[] dangling = [.. counts.Keys.Where(value => !targetIds.Contains(value)).Order()];
                results.Add(new DbcReferenceResult(reference, dangling.Length == 0 ? DbcReferenceStatus.Ok : DbcReferenceStatus.Dangling,
                    counts.Count, dangling.Length, dangling.Sum(value => counts[value]), [.. dangling.Take(5).Select(value => (long)value)], null));
            }
        }

        return results;
    }

    private static bool SupportedWidth(int width) => width is 8 or 16 or 32;

    private static uint EmptyMarker(int width) => width switch { 8 => byte.MaxValue, 16 => ushort.MaxValue, _ => uint.MaxValue };

    private static void VisitRecords(ClientDbcFileCheck check, DbdLayout layout, int offset, int count, int width, Action<int, uint> visit)
    {
        using var stream = new FileStream(check.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = 20;
        byte[] record = new byte[layout.RecordSize];
        for (int row = 0; row < check.Records; row++)
        {
            stream.ReadExactly(record);
            for (int element = 0; element < count; element++)
            {
                int position = offset + element * (width / 8);
                uint value = width switch
                {
                    8 => record[position],
                    16 => BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position, 2)),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position, 4)),
                };
                visit(row, value);
            }
        }
    }
}
