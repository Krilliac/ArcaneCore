using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ArcaneCore.Kernel.WorldData.Chat;

namespace ArcaneCore.World.Chat;

/// <summary>
/// The compiled <c>chat_word_filter</c> rules (after AscEmu Management/WordFilter.cpp, which censors or blocks chat and names by regex). Immutable; a
/// reload publishes a new instance. A pattern that does not compile is skipped and reported in <see cref="Rejected"/>; a match that times out
/// counts as a match, as <see cref="ArcaneCore.Data.Content.Names.NameCatalog"/> treats its DBC patterns.
/// </summary>
public sealed class ChatWordFilter
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly ImmutableArray<(Regex Regex, ChatWordFilterAction Action)> _chat;

    public ChatWordFilter(IEnumerable<ChatWordFilterRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var chat = ImmutableArray.CreateBuilder<(Regex, ChatWordFilterAction)>();
        var names = ImmutableArray.CreateBuilder<Regex>();
        var rejected = ImmutableArray.CreateBuilder<uint>();
        foreach (ChatWordFilterRule rule in rules)
        {
            Regex regex;
            try
            {
                regex = new Regex(rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            }
            catch (ArgumentException)
            {
                rejected.Add(rule.Id);
                continue;
            }

            if ((rule.Scope & ChatWordFilterScope.Chat) != 0)
            {
                chat.Add((regex, rule.Action));
            }

            if ((rule.Scope & ChatWordFilterScope.Names) != 0)
            {
                names.Add(regex);
            }
        }

        _chat = chat.ToImmutable();
        NamePatterns = names.ToImmutable();
        Rejected = rejected.ToImmutable();
    }

    public static ChatWordFilter Empty { get; } = new([]);

    public int ChatRuleCount => _chat.Length;

    /// <summary>The name-scope patterns, joined to the name catalog as profane patterns.</summary>
    public ImmutableArray<Regex> NamePatterns { get; }

    /// <summary>Ids of rows whose pattern is not a valid regular expression.</summary>
    public ImmutableArray<uint> Rejected { get; }

    /// <summary>
    /// Applies the chat rules in id order: null when a block rule matches (or times out), otherwise the message with every censor match
    /// replaced by asterisks (unchanged when nothing matched).
    /// </summary>
    public string? Apply(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string text = message;
        foreach ((Regex regex, ChatWordFilterAction action) in _chat)
        {
            try
            {
                if (action == ChatWordFilterAction.Block)
                {
                    if (regex.IsMatch(text))
                    {
                        return null;
                    }
                }
                else
                {
                    text = regex.Replace(text, static m => new string('*', m.Length));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }

        return text;
    }
}
