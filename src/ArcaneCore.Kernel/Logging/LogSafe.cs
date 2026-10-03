using System.Globalization;
using System.Text;

namespace ArcaneCore.Kernel.Logging;

/// <summary>
/// Makes client-controlled text safe to put in a log line. Account names, chat command text
/// and similar values arrive straight off the wire; unescaped CR/LF or ESC bytes let a client
/// forge log lines or drive terminal escape sequences (log forging, CWE-117).
/// </summary>
public static class LogSafe
{
    /// <summary>Longest text kept; longer input is truncated with an ellipsis marker.</summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Returns <paramref name="value"/> with every control, format or line-separator character
    /// replaced by a visible escape, truncated to <see cref="MaxLength"/> characters.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLength) + 8);
        int taken = 0;
        foreach (char c in value)
        {
            if (taken >= MaxLength)
            {
                builder.Append("...");
                break;
            }

            switch (c)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    UnicodeCategory category = char.GetUnicodeCategory(c);
                    if (category is UnicodeCategory.Control or UnicodeCategory.Format
                        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                        or UnicodeCategory.OtherNotAssigned)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }

            taken++;
        }

        return builder.ToString();
    }
}
