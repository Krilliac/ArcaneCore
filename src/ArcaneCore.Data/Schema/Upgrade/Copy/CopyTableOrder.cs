namespace ArcaneCore.Data.Schema.Upgrade.Copy;

/// <summary>One table of a component as the EF model declares it.</summary>
/// <param name="Name">The table name.</param>
/// <param name="Columns">Every column, in model order.</param>
/// <param name="PrimaryKey">The primary key columns (empty for a keyless table).</param>
/// <param name="Parents">The tables its foreign keys reference (itself included when it references itself).</param>
internal sealed record CopyTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<string> PrimaryKey, IReadOnlyList<string> Parents);

/// <summary>The foreign keys form a cycle (or a table references itself), so no insert order satisfies them; the message names the cycle.</summary>
internal sealed class CopyCycleException(string message) : InvalidOperationException(message);

/// <summary>Orders tables so every table comes after the tables it references (the order rows are inserted; reverse it to delete).</summary>
internal static class CopyTableOrder
{
    /// <summary>Parents first; ties by ordinal name so the order is deterministic. References to tables outside <paramref name="tables"/> are ignored.</summary>
    /// <exception cref="CopyCycleException">A cycle exists; the message lists the tables on it.</exception>
    public static IReadOnlyList<CopyTable> Sort(IReadOnlyList<CopyTable> tables)
    {
        var byName = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var remaining = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (CopyTable table in tables)
        {
            remaining[table.Name] = new HashSet<string>(
                table.Parents.Where(p => byName.ContainsKey(p)).Select(p => byName[p].Name), StringComparer.Ordinal);
        }

        var ordered = new List<CopyTable>(tables.Count);
        while (remaining.Count > 0)
        {
            string? next = remaining.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).FirstOrDefault();
            if (next is null)
            {
                throw new CopyCycleException(DescribeCycle(remaining));
            }

            remaining.Remove(next);
            ordered.Add(byName[next]);
            foreach (HashSet<string> parents in remaining.Values)
            {
                parents.Remove(next);
            }
        }

        return ordered;
    }

    private static string DescribeCycle(SortedDictionary<string, HashSet<string>> remaining)
    {
        // Every remaining table waits on another remaining table, so walking "a parent" from any of them must revisit one.
        string current = remaining.Keys.First();
        var path = new List<string>();
        while (!path.Contains(current))
        {
            path.Add(current);
            current = remaining[current].Order(StringComparer.Ordinal).First();
        }

        List<string> cycle = path[path.IndexOf(current)..];
        cycle.Add(current);
        return "the foreign keys form a cycle (" + string.Join(" -> ", cycle) + "); rows cannot be inserted in an order that satisfies them, " +
            "and a table that references itself is the same case. Copy refuses rather than disable constraints.";
    }
}
