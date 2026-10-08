namespace ArcaneCore.Game.Guilds;

/// <summary>
/// The antispam check of guild and charter names (vmangos AntispamInterface::filterMessage, consulted by
/// HandlePetitionBuyOpcode, PetitionsHandler.cpp:87-95, and Guild::Create, Guild.cpp:130-137; vmangos ships the interface
/// without an implementation, GetAntispam() returns null, so it filters nothing).
/// </summary>
public interface ICharterAntispamFilter
{
    /// <summary>Whether <paramref name="name"/> is spam and must be refused.</summary>
    bool IsSpam(string name);
}

/// <summary>
/// An operator-configured filter (<see cref="GuildOptions.CharterSpamPatterns"/>): a name is spam when, compared without
/// case and without spaces, it contains one of the patterns (so "Gold Sellers Dot Com" is caught by "goldsellers"). Charter
/// names allow only letters, digits and spaces of one script (vmangos IsValidCharterName), so the spacing is the only
/// obfuscation left to undo.
/// </summary>
public sealed class PatternCharterAntispamFilter : ICharterAntispamFilter
{
    private readonly string[] _patterns;

    public PatternCharterAntispamFilter(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = [.. patterns.Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    public int PatternCount => _patterns.Length;

    public bool IsSpam(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string normalized = Normalize(name);
        return _patterns.Any(p => normalized.Contains(p, StringComparison.Ordinal));
    }

    private static string Normalize(string text) => new([.. text.Where(c => !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant)]);
}
