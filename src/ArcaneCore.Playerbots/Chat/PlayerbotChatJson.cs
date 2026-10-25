using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// Pulls the JSON object out of a model's answer. Small models wrap it in code fences, add text after it, or send it escaped
/// (<c>{\"reply\": ...}</c>); this undoes those, and as a last resort reads the <c>reply</c>/<c>intent</c> string fields of a single-quoted or unquoted-key object by pattern. Pure: no state, any thread.
/// </summary>
internal static class PlayerbotChatJson
{
    /// <summary>
    /// The first top-level JSON object in <paramref name="answer"/>, parsed, or null with <paramref name="error"/> set. The caller owns
    /// (disposes) the document. Fences are stripped; text before and after the object is ignored; an answer that starts <c>{\"</c> and
    /// does not parse is unescaped once and read again. Input that cannot be an object (no <c>{</c> followed by a quote or <c>}</c>, or no
    /// closing brace) is rejected without a parse, so the usual bad answers throw nothing; a parse failure is caught here.
    /// </summary>
    internal static JsonDocument? TryParseObject(string? answer, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(answer))
        {
            error = "empty answer";
            return null;
        }

        string text = StripFences(answer);
        if (TryParseFirstObject(text, out JsonDocument? document, out error)) return document;
        if (LooksEscaped(text) && Unescape(text) is { } unescaped && TryParseFirstObject(unescaped, out document, out _))
        {
            error = null;
            return document;
        }

        if (Salvage(text) is { } salvaged)
        {
            error = null;
            return salvaged;
        }

        return null;
    }

    private static readonly Regex FieldPattern = new(
        """(?<![A-Za-z0-9_])["']?(?<key>reply|intent)["']?\s*:\s*(?:"(?<dq>(?:[^"\\]|\\.)*)"|'(?<sq>(?:[^'\\]|\\.)*)')""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50));

    /// <summary>
    /// Last resort for an object that is not JSON but plainly meant to be one (single-quoted or unquoted keys, single-quoted values): the
    /// <c>reply</c> and <c>intent</c> string fields only, read by pattern, as a new object. Null when no <c>reply</c> field is found.
    /// </summary>
    private static JsonDocument? Salvage(string text)
    {
        string? candidate = FirstObject(text);
        if (candidate is null) return null;
        JsonObject result = [];
        try
        {
            foreach (Match match in FieldPattern.Matches(candidate))
            {
                string key = match.Groups["key"].Value.ToLowerInvariant();
                if (result.ContainsKey(key)) continue;
                result[key] = Decode(match.Groups["dq"].Success ? match.Groups["dq"].Value : match.Groups["sq"].Value);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        return result.ContainsKey("reply") ? JsonDocument.Parse(result.ToJsonString()) : null;
    }

    private static string Decode(string raw)
    {
        if (raw.IndexOf('\\', StringComparison.Ordinal) < 0) return raw;
        var text = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\' || i + 1 >= raw.Length) text.Append(raw[i]);
            else
            {
                char next = raw[++i];
                text.Append(next switch { 'n' or 't' or 'r' => ' ', _ => next });
            }
        }

        return text.ToString();
    }

    /// <summary>The text inside the first code fence (<c>```json ... ```</c>), or the text itself when it has none. An unclosed fence runs to the end.</summary>
    internal static string StripFences(string text)
    {
        int fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence < 0) return text;
        if (text.AsSpan(0, fence).Contains('{')) return text[..fence]; // the object comes first and a closing fence trails it
        int start = fence + 3;
        int newline = text.IndexOf('\n', start);
        int brace = text.IndexOf('{', start);
        // the language tag ("json") runs to the end of the fence line; a fence that opens on the object itself has no tag
        if (newline >= 0 && (brace < 0 || newline < brace)) start = newline + 1;
        int end = text.IndexOf("```", start, StringComparison.Ordinal);
        return end < 0 ? text[start..] : text[start..end];
    }

    /// <summary>The first balanced <c>{...}</c> in <paramref name="text"/> (string-aware), or null when there is none or it is cut off.</summary>
    internal static string? FirstObject(string text)
    {
        int open = text.IndexOf('{', StringComparison.Ordinal);
        if (open < 0) return null;
        int depth = 0;
        bool inString = false;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[open..(i + 1)];
        }

        return null;
    }

    private static bool LooksEscaped(string text) => text.AsSpan().TrimStart().StartsWith("{\\\"", StringComparison.Ordinal);

    /// <summary>The text with its JSON string escapes undone once (<c>\"</c> to <c>"</c>, <c>\\</c> to <c>\</c>, <c>\n</c> to a newline), or null when it is not a valid escaped string.</summary>
    private static string? Unescape(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Contains('"', StringComparison.Ordinal) && !HasOnlyEscapedQuotes(trimmed)) return null;
        try
        {
            using JsonDocument wrapped = JsonDocument.Parse("\"" + trimmed + "\"");
            return wrapped.RootElement.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether every <c>"</c> in the text follows a backslash (so wrapping it in quotes makes one valid JSON string).</summary>
    private static bool HasOnlyEscapedQuotes(string text)
    {
        bool escaped = false;
        foreach (char c in text)
        {
            if (escaped) escaped = false;
            else if (c == '\\') escaped = true;
            else if (c == '"') return false;
        }

        return !escaped;
    }

    private static bool TryParseFirstObject(string text, out JsonDocument? document, out string? error)
    {
        document = null;
        string? candidate = FirstObject(text);
        if (candidate is null)
        {
            error = "no complete JSON object";
            return false;
        }

        // an object body starts with a quoted key or closes at once: anything else (single quotes, a backslash) is not JSON, whatever the parser says
        int i = 1;
        while (i < candidate.Length && char.IsWhiteSpace(candidate[i])) i++;
        if (i >= candidate.Length || candidate[i] is not ('"' or '}'))
        {
            error = $"'{candidate[Math.Min(i, candidate.Length - 1)]}' is not a valid start of a property name";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(candidate);
            error = null;
            return true;
        }
        catch (JsonException parseError)
        {
            error = parseError.Message;
            return false;
        }
    }
}
