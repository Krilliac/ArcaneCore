using System.Text;
using System.Text.RegularExpressions;

namespace ArcaneCore.Data.Content.Import;

/// <summary>
/// Keeps database passwords out of anything the importer prints or writes. A connection string
/// is never echoed; where one has to be described, <see cref="Redact"/> replaces the value of
/// every secret key, and <see cref="Scrub"/> removes the secrets from free text such as a
/// driver's error message.
/// </summary>
public static partial class ConnectionStringRedactor
{
    private const string Mask = "***";

    private static readonly HashSet<string> s_secretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "pwd", "passwd", "secret", "token", "accesstoken", "access token",
    };

    /// <summary>The connection string with the value of each secret key (Password, Pwd, …) replaced by <c>***</c>.</summary>
    public static string Redact(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var result = new StringBuilder();
        foreach ((string key, string? value, string segment) in Split(connectionString))
        {
            if (result.Length > 0)
            {
                result.Append(';');
            }

            result.Append(value is not null && s_secretKeys.Contains(key) ? key + "=" + Mask : segment);
        }

        return result.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> with every secret value of <paramref name="connectionString"/>, and
    /// any <c>Password=…</c>/<c>Pwd=…</c> it quotes, replaced by <c>***</c>.
    /// </summary>
    public static string Scrub(string text, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(connectionString);
        foreach ((string key, string? value, _) in Split(connectionString))
        {
            if (value is { Length: > 0 } && s_secretKeys.Contains(key))
            {
                text = text.Replace(value, Mask, StringComparison.Ordinal);
            }
        }

        return SecretPairPattern().Replace(text, "$1=" + Mask);
    }

    [GeneratedRegex(@"(?i)\b(password|pwd|passwd)\s*=\s*(?:'(?:[^']|'')*'|""(?:[^""]|"""")*""|[^;\s]*)")]
    private static partial Regex SecretPairPattern();

    /// <summary>Segments of a connection string split on <c>;</c> outside quotes: key, unquoted value, original text.</summary>
    private static IEnumerable<(string Key, string? Value, string Segment)> Split(string connectionString)
    {
        var current = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < connectionString.Length; i++)
        {
            char c = connectionString[i];
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote)
                {
                    if (i + 1 < connectionString.Length && connectionString[i + 1] == quote)
                    {
                        // A doubled quote is an escaped quote character inside the value.
                        current.Append(quote);
                        i++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (c is '\'' or '"' && StartsValue(current))
            {
                quote = c;
                current.Append(c);
            }
            else if (c == ';')
            {
                if (current.Length > 0)
                {
                    yield return Parse(current.ToString());
                }

                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return Parse(current.ToString());
        }
    }

    /// <summary>
    /// True when nothing but white space follows the <c>=</c> of <paramref name="segment"/>: only then does a quote
    /// open a quoted value (as in <c>DbConnectionStringBuilder</c>). An apostrophe inside <c>O'Brien</c> is data.
    /// </summary>
    private static bool StartsValue(StringBuilder segment)
    {
        bool seenEquals = false;
        for (int i = 0; i < segment.Length; i++)
        {
            if (seenEquals)
            {
                if (!char.IsWhiteSpace(segment[i]))
                {
                    return false;
                }
            }
            else if (segment[i] == '=')
            {
                seenEquals = true;
            }
        }

        return seenEquals;
    }

    private static (string Key, string? Value, string Segment) Parse(string segment)
    {
        int equals = segment.IndexOf('=', StringComparison.Ordinal);
        if (equals < 0)
        {
            return (segment.Trim(), null, segment);
        }

        string value = segment[(equals + 1)..].Trim();
        if (value.Length >= 2 && value[0] is '\'' or '"' && value[^1] == value[0])
        {
            char quote = value[0];
            value = value[1..^1].Replace(new string(quote, 2), new string(quote, 1), StringComparison.Ordinal);
        }

        return (segment[..equals].Trim(), value, segment);
    }
}
