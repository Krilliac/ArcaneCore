using System.Text.RegularExpressions;

namespace ArcaneCore.Kernel.Ops.ServerConsole;

/// <summary>
/// Masks secrets in a console command line before it is logged. Pure function. Two rules: the account commands that carry
/// a password (every token after the account name, or after "password", becomes <c>***</c>), and the token after any
/// <c>password</c>/<c>pass</c>/<c>secret</c>/<c>pin</c>/<c>totp</c> keyword.
/// </summary>
public static partial class ConsoleCommandRedactor
{
    /// <summary>The replacement for a masked token.</summary>
    public const string Mask = "***";

    [GeneratedRegex(@"^(?<head>\s*[.!]?\s*account\s+(?:create|set-password|setpassword|set\s+password)\s+\S+)(?<tail>\s+\S.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedAccountPassword();

    [GeneratedRegex(@"^(?<head>\s*[.!]?\s*account\s+password)(?<tail>\s+\S.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnAccountPassword();

    [GeneratedRegex(@"(?<=^|\s)(?<key>pass(?:word)?|secret|pin|totp)(?<gap>\s+)\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordSecret();

    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    /// <summary>The line with its secrets masked; the line itself when it carries none.</summary>
    public static string Redact(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        Match named = NamedAccountPassword().Match(line);
        if (named.Success)
        {
            return named.Groups["head"].Value + Token().Replace(named.Groups["tail"].Value, Mask);
        }

        Match own = OwnAccountPassword().Match(line);
        if (own.Success)
        {
            return own.Groups["head"].Value + Token().Replace(own.Groups["tail"].Value, Mask);
        }

        return KeywordSecret().Replace(line, m => m.Groups["key"].Value + m.Groups["gap"].Value + Mask);
    }
}
