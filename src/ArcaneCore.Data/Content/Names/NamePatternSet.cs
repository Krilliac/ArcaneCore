using System.Buffers;
using System.Text.RegularExpressions;

namespace ArcaneCore.Data.Content.Names;

/// <summary>
/// An immutable "any pattern matches" set for character-name filtering: a set of case-insensitive substring literals plus a few
/// combined non-backtracking regexes (plus the odd row that must stand alone). It answers exactly what a loop over one regex per source row would, at a roughly constant
/// heap cost instead of one automaton per row. Safe for concurrent reads (the regexes and the search values are thread-safe).
/// </summary>
public sealed class NamePatternSet
{
    public static NamePatternSet Empty { get; } = new([], null, 0);

    private readonly Regex[] _regexes;
    private readonly SearchValues<string>? _literals;

    internal NamePatternSet(Regex[] regexes, SearchValues<string>? literals, int rows)
    {
        _regexes = regexes;
        _literals = literals;
        Rows = rows;
    }

    /// <summary>Wraps ready-made regexes, each tested on its own.</summary>
    public NamePatternSet(IReadOnlyList<Regex> regexes)
        : this([.. regexes], null, regexes.Count)
    {
    }

    /// <summary>The number of source rows (patterns) this set stands for, not the number of regexes it holds.</summary>
    public int Rows { get; }

    /// <summary>The combined regexes (excludes the literal rows).</summary>
    internal IReadOnlyList<Regex> Regexes => _regexes;

    /// <summary>
    /// True when any pattern matches somewhere in <paramref name="name"/>. Throws <see cref="RegexMatchTimeoutException"/> if a
    /// regex exceeds its timeout; callers fail closed.
    /// </summary>
    public bool IsMatch(string name)
    {
        if (_literals is not null && name.AsSpan().ContainsAny(_literals)) return true;
        foreach (Regex regex in _regexes)
            if (regex.IsMatch(name)) return true;
        return false;
    }
}
