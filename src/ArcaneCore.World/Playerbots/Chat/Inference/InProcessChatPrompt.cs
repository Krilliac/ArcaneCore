using System.Globalization;
using System.Text;

namespace ArcaneCore.World.Playerbots.Chat.Inference;

/// <summary>
/// The in-process model's prompt and the screen its replies pass before they are said. A small local model is not asked for the
/// JSON answer the remote providers give: it writes the line itself. The system part is short, puts the fixed rules (with the
/// grounding rules) first so every bot's prompt starts with the same tokens (computed once and reused from the cache), then the
/// bot's own facts from <see cref="PlayerbotChatPrompts.DynamicSystem"/>. ChatML: <c>&lt;|im_start|&gt;role\n...&lt;|im_end|&gt;\n</c>,
/// with the special tokens inserted by id, never parsed out of a player's text.
/// </summary>
internal static class InProcessChatPrompt
{
    /// <summary>The fixed rules: identical for every bot and request (nothing variable belongs here).</summary>
    internal const string Rules =
        "You are a player character in World of Warcraft patch 1.12 (2006), typing in the game's chat. " +
        "Write one short, casual chat line as that player: under 20 words, plain text. " +
        "Use only the facts given below about yourself; never make up quests, bosses, items, places, players or events. " +
        "If you do not know something, say so briefly. " +
        "No emojis, no quotes, no lists, no line breaks, no talk of the real world. " +
        "If someone asks whether you are a bot, say you are a bot run by this server.";

    /// <summary>The reminder after the facts (small models follow the nearest instruction best).</summary>
    internal const string Grounding = "Answer only from these facts, in one short line.";

    /// <summary>
    /// The prompt's token ids, or null when even the line alone does not fit <paramref name="maxTokens"/>. Older remembered exchanges
    /// are dropped first when the whole conversation does not fit.
    /// </summary>
    internal static List<int>? Build(BotChatPrompt prompt, BpeTokenizer tokenizer, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(tokenizer);
        int imStart = tokenizer.ImStart ?? throw new InvalidOperationException("the vocabulary has no <|im_start|>");
        int imEnd = tokenizer.ImEnd ?? throw new InvalidOperationException("the vocabulary has no <|im_end|>");
        string system = Rules + "\n\n" + PlayerbotChatPrompts.Clean(prompt.DynamicSystem, 1200) + "\n" + Grounding;
        IReadOnlyList<BotChatTurn> messages = prompt.Messages;
        for (int skip = 0; skip < messages.Count; skip++)
        {
            // Never start the conversation with a bot turn: skip to the next player line.
            if (!messages[skip].FromPlayer && skip < messages.Count - 1) continue;
            var ids = new List<int>(256);
            if (tokenizer.AddBos && tokenizer.Bos is { } bos && bos != imStart) ids.Add(bos);
            Turn(ids, tokenizer, imStart, imEnd, "system", system);
            for (int i = skip; i < messages.Count; i++)
                Turn(ids, tokenizer, imStart, imEnd, messages[i].FromPlayer ? "user" : "assistant", PlayerbotChatPrompts.Clean(messages[i].Text, PlayerbotChatPrompts.MaxLineLength));
            ids.Add(imStart);
            tokenizer.EncodeInto("assistant\n", ids);
            if (ids.Count <= maxTokens) return ids;
        }

        return null;
    }

    private static void Turn(List<int> ids, BpeTokenizer tokenizer, int imStart, int imEnd, string role, string text)
    {
        ids.Add(imStart);
        tokenizer.EncodeInto(role + "\n" + text, ids);
        ids.Add(imEnd);
        tokenizer.EncodeInto("\n", ids);
    }

    private static readonly string[] Leaks =
    [
        "im_start", "im_end", "endoftext", "<|", "|>", "assistant:", "user:", "system:",
        "as an ai", "language model", "openai", "chatgpt", "qwen", "alibaba", "hugging face", "huggingface", "smollm",
        "http", "www.", ".com",
    ];

    /// <summary>
    /// The reply as said, or null when it must not be (the templates answer instead): empty, longer than <paramref name="maxCharacters"/>,
    /// markup or quotes, a leaked role token or model name, a link, an emoji or a non-Latin character. A reply cut off by the token
    /// limit keeps its last whole sentence (or is dropped). A leading "Name:" of the bot itself is removed.
    /// </summary>
    internal static string? Screen(string? raw, string botName, int maxCharacters, bool truncated)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string text = PlayerbotChatPrompts.Clean(raw, 4 * maxCharacters).Trim();
        if (botName.Length > 0 && text.StartsWith(botName + ":", StringComparison.OrdinalIgnoreCase)) text = text[(botName.Length + 1)..].TrimStart();
        text = text.Trim().Trim('"', '\'', '`').Trim();
        if (truncated)
        {
            int end = text.LastIndexOfAny(['.', '!', '?']);
            if (end < 12) return null;
            text = text[..(end + 1)];
        }

        if (text.Length == 0 || text.Length > maxCharacters) return null;
        foreach (char c in text)
        {
            if (c is '<' or '>' or '{' or '}' or '[' or ']' or '*' or '#' or '`' or '|' or '\\' or '~' or '=' or '"' or '_') return null;
            if (char.IsSurrogate(c) || c > 'ɏ') return null; // emoji, symbols, non-Latin scripts
            UnicodeCategory category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.PrivateUse or UnicodeCategory.Format)
                return null;
        }

        string lower = text.ToLowerInvariant();
        foreach (string leak in Leaks)
            if (lower.Contains(leak, StringComparison.Ordinal)) return null;
        if (!text.Any(char.IsLetter)) return null;
        return CollapseSpaces(text);
    }

    private static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (c == ' ')
            {
                if (space) continue;
                space = true;
            }
            else
            {
                space = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
