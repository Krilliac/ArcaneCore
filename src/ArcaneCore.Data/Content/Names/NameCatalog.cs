using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace ArcaneCore.Data.Content.Names;

public enum NameCatalogResult { Allowed, Profane, Reserved }

public sealed class NameCatalog
{
    public static NameCatalog Empty { get; } = new([], [], []);

    private readonly ImmutableArray<Regex> _profane;
    private readonly ImmutableArray<Regex> _reserved;
    private readonly ImmutableHashSet<string> _reservedExact;
    private readonly ImmutableArray<Regex> _filterProfane = [];

    public NameCatalog(IReadOnlyList<Regex> profane, IReadOnlyList<Regex> reserved, IReadOnlyList<NameCatalogSource> sources, IReadOnlySet<string>? reservedExact = null)
    {
        _profane = [.. profane];
        _reserved = [.. reserved];
        Sources = [.. sources];
        _reservedExact = (reservedExact as IEnumerable<string> ?? Enumerable.Empty<string>())
            .Select(ReservedNameNormalization.Normalize)
            .Where(name => name is not null)
            .Cast<string>()
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<NameCatalogSource> Sources { get; }

    /// <summary>The name-scope <c>chat_word_filter</c> patterns (world schema 47), answered as profane after the DBC patterns.</summary>
    public IReadOnlyList<Regex> FilterProfane => _filterProfane;

    /// <summary>Creates a complete catalog with the same DBC rules and provenance but a new SQL exact-name snapshot.</summary>
    public NameCatalog WithReservedExact(IReadOnlySet<string> reservedExact)
        => new(_profane, _reserved, Sources, reservedExact) { FilterProfaneInit = _filterProfane };

    /// <summary>Creates a complete catalog with the same DBC and SQL reserved rules but new <c>chat_word_filter</c> name patterns.</summary>
    public NameCatalog WithFilterProfane(IReadOnlyList<Regex> patterns)
        => new(_profane, _reserved, Sources, _reservedExact) { FilterProfaneInit = [.. patterns] };

    private ImmutableArray<Regex> FilterProfaneInit { init => _filterProfane = value; }

    public NameCatalogResult Check(string name)
    {
        foreach (Regex pattern in _profane)
            try { if (pattern.IsMatch(name)) return NameCatalogResult.Profane; }
            catch (RegexMatchTimeoutException) { return NameCatalogResult.Profane; }
        foreach (Regex pattern in _filterProfane)
            try { if (pattern.IsMatch(name)) return NameCatalogResult.Profane; }
            catch (RegexMatchTimeoutException) { return NameCatalogResult.Profane; }
        if (_reservedExact.Contains(name.ToLowerInvariant())) return NameCatalogResult.Reserved;
        foreach (Regex pattern in _reserved)
            try { if (pattern.IsMatch(name)) return NameCatalogResult.Reserved; }
            catch (RegexMatchTimeoutException) { return NameCatalogResult.Reserved; }
        return NameCatalogResult.Allowed;
    }

    /// <summary>Reports whether the normalized name is blocked by the SQL exact-name snapshot.</summary>
    public bool IsReservedExact(string name) => _reservedExact.Contains(name.ToLowerInvariant());

    /// <summary>Reports whether SQL exact blocking is the only reserved reason for this name.</summary>
    public bool IsSqlReservedOnly(string name)
    {
        if (!IsReservedExact(name)) return false;
        foreach (Regex pattern in _reserved)
            try { if (pattern.IsMatch(name)) return false; }
            catch (RegexMatchTimeoutException) { return false; }
        return true;
    }
}

public sealed record NameCatalogSource(string Kind, string Path, string Sha256, int Rows);
