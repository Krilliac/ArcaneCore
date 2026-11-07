using System.Collections.Immutable;
using System.Text.RegularExpressions;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData.Chat;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Chat;

/// <summary>
/// The compiled <c>chat_word_filter</c> rules (after AscEmu Management/WordFilter.cpp, which censors or blocks chat and names by regex). Immutable; a
/// reload publishes a new instance. A pattern that does not compile is skipped and reported in <see cref="Rejected"/>.
/// <para>
/// A pattern is compiled with <see cref="RegexOptions.NonBacktracking"/> (linear time, so it runs without a timeout) whenever the engine
/// supports it. A pattern that needs backtracking-only features (backreferences, lookarounds, atomic groups, ...), or is too large for it, keeps a match timeout; when it
/// fires, that one rule is skipped for the message, counted as <c>chat.word-filter-timeout</c> in the fault ledger and logged through the ledger's throttle (the first <c>Diagnostics:FaultLogLimit</c> timeouts, then at most once a minute, naming the rule id). A timeout is a property of
/// the machine's load, not of the text, so it never rejects the message. (<see cref="NamePatterns"/> are still treated as a match on timeout by
/// <see cref="ArcaneCore.Data.Content.Names.NameCatalog"/>, which fails closed for names.)
/// </para>
/// </summary>
public sealed class ChatWordFilter
{
    private static readonly TimeSpan DefaultMatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly ImmutableArray<(Regex Regex, ChatWordFilterAction Action, uint Id)> _chat;

    private readonly FaultLedger _ledger;
    private readonly ILogger? _logger;

    /// <param name="rules">The rows to compile.</param>
    /// <param name="logger">Receives the ledger-throttled warning when a rule times out; null for none (the fault ledger still counts it).</param>
    public ChatWordFilter(IEnumerable<ChatWordFilterRule> rules, ILogger? logger = null)
        : this(rules, DefaultMatchTimeout, null, logger)
    {
    }

    /// <param name="rules">The rows to compile.</param>
    /// <param name="matchTimeout">Timeout for patterns that cannot use the non-backtracking engine (tests shorten it).</param>
    /// <param name="ledger">Where a timeout is counted; null for <see cref="FaultLedger.Shared"/>.</param>
    /// <param name="logger">Where the ledger-throttled timeout warning goes; null for none.</param>
    internal ChatWordFilter(IEnumerable<ChatWordFilterRule> rules, TimeSpan matchTimeout, FaultLedger? ledger, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _ledger = ledger ?? FaultLedger.Shared;
        _logger = logger;
        var chat = ImmutableArray.CreateBuilder<(Regex, ChatWordFilterAction, uint)>();
        var names = ImmutableArray.CreateBuilder<Regex>();
        var rejected = ImmutableArray.CreateBuilder<uint>();
        foreach (ChatWordFilterRule rule in rules)
        {
            Regex regex;
            try
            {
                regex = Compile(rule.Pattern, matchTimeout);
            }
            catch (ArgumentException)
            {
                rejected.Add(rule.Id);
                continue;
            }

            if ((rule.Scope & ChatWordFilterScope.Chat) != 0)
            {
                chat.Add((regex, rule.Action, rule.Id));
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

    /// <summary>Linear-time engine when the pattern allows it (it throws <see cref="NotSupportedException"/> otherwise), else backtracking under a timeout. An invalid pattern throws <see cref="ArgumentException"/> either way.</summary>
    private static Regex Compile(string pattern, TimeSpan matchTimeout)
    {
        const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try
        {
            // Linear time in the text, so no timeout: the timeout is wall-clock, and a descheduled thread would trip even a cheap match.
            return new Regex(pattern, Options | RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, Options, matchTimeout);
        }
    }

    public static ChatWordFilter Empty { get; } = new([]);

    public int ChatRuleCount => _chat.Length;

    /// <summary>The name-scope patterns, joined to the name catalog as profane patterns.</summary>
    public ImmutableArray<Regex> NamePatterns { get; }

    /// <summary>Ids of rows whose pattern is not a valid regular expression.</summary>
    public ImmutableArray<uint> Rejected { get; }

    /// <summary>
    /// Applies the chat rules in id order: null when a block rule matches, otherwise the message with every censor match replaced by
    /// asterisks (unchanged when nothing matched). A rule whose match times out is skipped and counted; it never rejects the message.
    /// </summary>
    public string? Apply(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        string text = message;
        foreach ((Regex regex, ChatWordFilterAction action, uint ruleId) in _chat)
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
            catch (RegexMatchTimeoutException ex)
            {
                // Counted either way; this rule is skipped so clean text is not dropped because of a slow pattern or a busy machine. The ledger throttles the
                // warning: the first Diagnostics:FaultLogLimit timeouts, then at most one a minute (with the count suppressed since).
                if (_logger is null)
                {
                    _ledger.Record("chat.word-filter-timeout", ex);
                }
                else
                {
                    _ledger.Log(_logger, LogLevel.Warning, "chat.word-filter-timeout", ex,
                        "chat word filter: a {Action} rule (id {RuleId}) timed out and was skipped for the message", action, ruleId);
                }
            }
        }

        return text;
    }
}
