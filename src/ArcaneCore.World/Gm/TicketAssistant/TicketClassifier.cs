using System.Text;
using System.Text.Json;

namespace ArcaneCore.World.Gm.TicketAssistant;

/// <summary>
/// Reads a model's classification strictly and classifies by keywords when there is no model. The ticket text is untrusted: the
/// result is only ever a <see cref="TicketCategory"/> and a short sanitized summary; whatever else a reply says is ignored.
/// </summary>
public static class TicketClassifier
{
    /// <summary>The longest summary kept.</summary>
    public const int MaxSummary = 120;

    /// <summary>
    /// Parse <c>{"category": "...", "summary": "..."}</c>. The category must be exactly one of the names of <see cref="TicketCategory"/>
    /// (a number, another word, a missing or non-string value is not accepted); anything unparsable is <see cref="TicketCategory.Other"/>.
    /// </summary>
    public static TicketClassification Parse(string? raw)
    {
        var unparsable = new TicketClassification(TicketCategory.Other, string.Empty, "unparsable");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return unparsable;
        }

        string text = raw.Trim();
        int open = text.IndexOf('{');
        int close = text.LastIndexOf('}');
        if (open < 0 || close <= open)
        {
            return unparsable;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text[open..(close + 1)]);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("category", out JsonElement category)
                || category.ValueKind != JsonValueKind.String
                || CategoryOf(category.GetString()) is not { } parsed)
            {
                return unparsable;
            }

            string summary = root.TryGetProperty("summary", out JsonElement s) && s.ValueKind == JsonValueKind.String ? Clean(s.GetString()) : string.Empty;
            return new TicketClassification(parsed, summary, "model");
        }
        catch (JsonException)
        {
            return unparsable;
        }
    }

    private static TicketCategory? CategoryOf(string? name) => name?.Trim() switch
    {
        "Stuck" => TicketCategory.Stuck,
        "DeadCantRes" => TicketCategory.DeadCantRes,
        "QuestBroken" => TicketCategory.QuestBroken,
        "ItemLost" => TicketCategory.ItemLost,
        "BugReport" => TicketCategory.BugReport,
        "Harassment" => TicketCategory.Harassment,
        "Question" => TicketCategory.Question,
        "Other" => TicketCategory.Other,
        _ => null,
    };

    /// <summary>One line, no control characters or chat escapes, at most <see cref="MaxSummary"/> characters.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxSummary));
        foreach (char c in text)
        {
            if (builder.Length >= MaxSummary)
            {
                break;
            }

            builder.Append(char.IsControl(c) || c == '|' ? ' ' : c);
        }

        return builder.ToString().Trim();
    }

    private static readonly (TicketCategory Category, string[] Words)[] Keywords =
    [
        (TicketCategory.Harassment, ["harass", "stalk", "threat", "racist", "insult", "abuse", "bullying", "scammed", "scammer", "doxx"]),
        (TicketCategory.Stuck, ["stuck", "fell through", "under the map", "under the world", "underground", "can't move", "cant move", "cannot move", "trapped", "falling forever"]),
        (TicketCategory.DeadCantRes, ["can't res", "cant res", "cannot res", "can't resurrect", "cant resurrect", "cannot resurrect", "no graveyard", "spirit healer", "corpse"]),
        (TicketCategory.QuestBroken, ["quest"]),
        (TicketCategory.ItemLost, ["lost my", "missing item", "item is gone", "item disappeared", "vanished", "my item", "my gold"]),
        (TicketCategory.BugReport, ["bug", "glitch", "broken", "doesn't work", "does not work", "not working", "crash", "exploit"]),
        (TicketCategory.Question, ["how do i", "how to", "how can i", "what is", "where can i", "where do i", "is it possible", "can i ", "what are the rules"]),
    ];

    /// <summary>
    /// The fallback classifier: the first category (in the order harassment, stuck, dead, quest, item, bug, question) whose keyword
    /// appears; a text ending in a question mark with no other match is a question; everything else is <see cref="TicketCategory.Other"/>
    /// (which escalates).
    /// </summary>
    public static TicketClassification ByKeywords(string? ticketText)
    {
        string text = (ticketText ?? string.Empty).ToLowerInvariant();
        foreach ((TicketCategory category, string[] words) in Keywords)
        {
            foreach (string word in words)
            {
                if (text.Contains(word, StringComparison.Ordinal))
                {
                    return new TicketClassification(category, string.Empty, "keywords");
                }
            }
        }

        return new TicketClassification(text.TrimEnd().EndsWith('?') ? TicketCategory.Question : TicketCategory.Other, string.Empty, "keywords");
    }
}
