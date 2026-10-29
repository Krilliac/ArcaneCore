namespace ArcaneCore.Game.Tests.Rules.Creatures.Parity;

/// <summary>
/// A difference between the two executors that is approved: the lines of <see cref="Scenario"/> that exist on only one side
/// (<see cref="Side"/>) and contain <see cref="Contains"/>. <see cref="Reason"/> says why the difference is acceptable (a deliberate change,
/// an old bug, a known gap); an approved exception that no longer matches a difference fails the run so the list stays true.
/// </summary>
internal sealed record ParityException(string Scenario, ParitySide Side, string Contains, string Reason);

/// <summary>One line that only one side logged.</summary>
internal sealed record ParityDifference(ParitySide Side, int Index, string Line)
{
    public override string ToString() => $"{(Side == ParitySide.Old ? "-old   " : "+engine")} [{Index}] {Line}";
}

/// <summary>The line diff of two effect logs (longest common subsequence) and the check of the differences against the approved exceptions.</summary>
internal static class ParityDiff
{
    public static IReadOnlyList<ParityDifference> Compare(IReadOnlyList<string> old, IReadOnlyList<string> engine)
    {
        int n = old.Count;
        int m = engine.Count;
        int[,] lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = old[i] == engine[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<ParityDifference>();
        int a = 0;
        int b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && old[a] == engine[b])
            {
                a++;
                b++;
            }
            else if (b >= m || (a < n && lcs[a + 1, b] >= lcs[a, b + 1]))
            {
                result.Add(new ParityDifference(ParitySide.Old, a, old[a]));
                a++;
            }
            else
            {
                result.Add(new ParityDifference(ParitySide.Engine, b, engine[b]));
                b++;
            }
        }

        return result;
    }

    /// <summary>The differences no approved exception covers, and the approved exceptions of the scenario that covered nothing.</summary>
    public static (IReadOnlyList<ParityDifference> Unexplained, IReadOnlyList<ParityException> Unused) Apply(
        string scenario, IReadOnlyList<ParityDifference> differences, IReadOnlyList<ParityException> approved)
    {
        ParityException[] mine = [.. approved.Where(e => e.Scenario == scenario)];
        var used = new HashSet<ParityException>();
        var unexplained = new List<ParityDifference>();
        foreach (ParityDifference difference in differences)
        {
            ParityException? match = mine.FirstOrDefault(e => e.Side == difference.Side && difference.Line.Contains(e.Contains, StringComparison.Ordinal));
            if (match is null)
            {
                unexplained.Add(difference);
            }
            else
            {
                used.Add(match);
            }
        }

        return (unexplained, [.. mine.Where(e => !used.Contains(e))]);
    }
}
