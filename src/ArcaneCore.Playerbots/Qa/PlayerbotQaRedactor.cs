using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>
/// Removes what must not leave the server from text bound for a GitHub issue, a comment, an export or chat: tokens and keys, IP addresses, e-mail
/// addresses, user folders, guids, and the names of bots and accounts. Also shapes titles and bodies for GitHub (character set, length caps, the
/// dedup marker kept). Pure apart from the replaceable name set; thread-safe (the name set is swapped, never mutated).
/// </summary>
internal sealed partial class PlayerbotQaRedactor
{
    internal const int MaxTitleChars = 120;

    /// <summary>GitHub caps an issue or comment body at 65536 characters; stay well below.</summary>
    internal const int MaxBodyChars = 60_000;

    private const string Truncated = "\n\n_(truncated)_\n";

    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _token;

    public PlayerbotQaRedactor(string? knownToken = null)
    {
        _token = string.IsNullOrEmpty(knownToken) ? null : knownToken;
    }

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,})", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubTokenPattern();

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{8,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"\b(?<k>(?:api[_-]?)?key|token|password|passwd|secret|authorization)(?<s>\s*[=:]\s*)(?!<redacted>)[^\s,;&""']{1,200}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,255}\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.]*\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:])(?:(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:(?:[0-9A-Fa-f]{1,4}(?::[0-9A-Fa-f]{1,4}){0,6})?|::(?:[0-9A-Fa-f]{1,4}:){0,6}[0-9A-Fa-f]{1,4})(?![0-9A-Fa-f:])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"(?<root>[A-Za-z]:[\\/]Users[\\/]|/home/|/Users/)[^\\/\s""']{1,64}", RegexOptions.CultureInvariant)]
    private static partial Regex UserPathPattern();

    [GeneratedRegex(@"\p{L}{3,32}", RegexOptions.CultureInvariant)]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"(?<name>@\w+)\s*=\s*'(?:[^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex SqlParameterPattern();

    [GeneratedRegex(@"\b(?:SELECT|INSERT|UPDATE|DELETE|REPLACE|MERGE)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SqlVerbPattern();

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.CultureInvariant)]
    private static partial Regex SqlStringLiteralPattern();

    [GeneratedRegex(@"(?<![\w@.:])-?\d+(?:\.\d+)?(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex SqlNumberPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\s""'<>|*?]*|\\\\[^\s\\""'<>|*?]+\\[^\s""'<>|*?]*|(?<![\w:/.\\-])/(?:[\w.@+-]+/)+[\w.@+-]*", RegexOptions.CultureInvariant)]
    private static partial Regex FilePathPattern();

    [GeneratedRegex(@"\b0[xX][0-9a-fA-F]{6,16}\b|\b(?<k>guid|owner|caster|account)(?<s>\s*[=:#]?\s*)\d{3,}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GameIdPattern();

    /// <summary>The word before a <c>(guid N)</c> tail: a player (or bot) name in <c>player Name (guid 5)</c>, <c>to Name (guid 5)</c>.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?<w>\p{L}{2,24})(?=\s+\(guid\s+[0-9A-Fa-f]+\))", RegexOptions.CultureInvariant)]
    private static partial Regex NameBeforeGuidPattern();

    /// <summary>A capitalised word after <c>player</c> (game names are capitalised; <c>player alive</c> stays).</summary>
    [GeneratedRegex(@"\b(?<k>[Pp]layer)(?<s>\s+)\p{Lu}\p{L}{1,23}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerNamePattern();

    /// <summary>Words that precede a <c>(guid N)</c> tail as the kind of the subject, not as a name.</summary>
    private static readonly HashSet<string> SubjectKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "player", "creature", "unit", "bot", "gameobject", "object", "item", "pet", "npc", "character", "spawn", "transport",
    };

    /// <summary>
    /// <see cref="Redact"/> for text taken from an exception (a server fault's message): also removes absolute file paths (drive, UNC and
    /// rooted POSIX paths), hexadecimal object ids and <c>guid 12345</c>-style numeric ids. An exception message can carry anything a handler formatted
    /// into it, so this is the stricter reading used for the public issue.
    /// </summary>
    public string RedactFault(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = StripSqlValues(text);
        text = FilePathPattern().Replace(text, "<path>"); // before Redact: its user-folder rule would leave a path tail behind
        text = Redact(text);
        return GameIdPattern().Replace(text, m => m.Groups["k"].Success ? m.Groups["k"].Value + m.Groups["s"].Value + "<id>" : "<id>");
    }

    /// <summary>
    /// Keeps the shape of SQL text and drops every value in it: the quoted value of an EF Core parameter (<c>@p0='Name' (Size = 4)</c> becomes <c>@p0='?' (Size = 4)</c>) and,
    /// in text that holds a SQL statement, every string literal and number (<c>SET name = 'x' WHERE guid = 42</c> becomes <c>SET name = '?' WHERE guid = ?</c>). Text with no SQL is
    /// left alone apart from the parameter rule. A value can be a player name, a guid or an item: none of it belongs in a public issue.
    /// </summary>
    internal static string StripSqlValues(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = SqlParameterPattern().Replace(text, "${name}='?'");
        if (!SqlVerbPattern().IsMatch(text)) return text;
        text = SqlStringLiteralPattern().Replace(text, "'?'");
        return SqlNumberPattern().Replace(text, "?");
    }

    /// <summary>A redactor with the same names that also removes the literal <paramref name="token"/> wherever it appears.</summary>
    public PlayerbotQaRedactor WithToken(string token) => new(token) { _names = _names };

    /// <summary>Replace the names to hide (bots and accounts); names under three characters are ignored.</summary>
    public void SetNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        _names = new HashSet<string>(names.Where(name => name is { Length: >= 3 and <= 32 }), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Only the secrets: tokens, bearer headers, keys, assignments, and the known token.</summary>
    public static string RedactSecrets(string text, string? knownToken = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!string.IsNullOrEmpty(knownToken)) text = text.Replace(knownToken, "<token>", StringComparison.Ordinal);
        text = GitHubTokenPattern().Replace(text, "<token>");
        text = BearerPattern().Replace(text, "Bearer <token>");
        text = ApiKeyPattern().Replace(text, "<token>");
        return AssignmentPattern().Replace(text, "${k}${s}<redacted>");
    }

    /// <summary>Secrets, addresses, user folders, guids and known names.</summary>
    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = RedactSecrets(text, _token);
        text = GuidPattern().Replace(text, "<id>");
        text = EmailPattern().Replace(text, "<email>");
        text = Ipv4Pattern().Replace(text, "<ip>");
        text = Ipv6Pattern().Replace(text, "<ip>");
        text = UserPathPattern().Replace(text, m => m.Groups["root"].Value + "<user>");
        text = NameBeforeGuidPattern().Replace(text, m => SubjectKinds.Contains(m.Groups["w"].Value) ? m.Value : "<name>");
        text = PlayerNamePattern().Replace(text, "${k}${s}<name>");
        HashSet<string> names = _names;
        if (names.Count > 0) text = WordPattern().Replace(text, m => names.Contains(m.Value) ? "<name>" : m.Value);
        return text;
    }

    /// <summary>
    /// <c>[bot-qa] kind: subject</c>: the subject redacted, characters outside <c>A-Za-z0-9 ._:/#()=,+'-</c> turned into '?', white space
    /// collapsed, at most <see cref="MaxTitleChars"/> characters.
    /// </summary>
    public string Title(string kind, string subject)
    {
        string text = WhitespacePattern().Replace(Redact(kind + ": " + subject), " ").Trim();
        var sb = new StringBuilder("[bot-qa] ", MaxTitleChars);
        foreach (char c in text)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || " ._:/#()=,+'-".Contains(c, StringComparison.Ordinal) ? c : '?');
        }

        return sb.Length <= MaxTitleChars ? sb.ToString().TrimEnd() : sb.ToString(0, MaxTitleChars - 3).TrimEnd() + "...";
    }

    /// <summary>
    /// <paramref name="body"/> with secrets removed once more, cut on a line boundary to <see cref="MaxBodyChars"/>, and always starting with
    /// <paramref name="marker"/> (added when missing, kept when the cut happens).
    /// </summary>
    public string Body(string body, string marker)
    {
        string text = RedactSecrets(body ?? string.Empty, _token);
        if (!text.Contains(marker, StringComparison.Ordinal)) text = marker + "\n" + text;
        if (text.Length <= MaxBodyChars) return text;
        int limit = MaxBodyChars - Truncated.Length;
        int cut = text.LastIndexOf('\n', limit - 1);
        if (cut < marker.Length) cut = limit;
        return text[..cut] + Truncated;
    }
}
