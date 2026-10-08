using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>Where a flag came from: the local filter on the player's line, a provider's moderation step, or the local filter on a model's reply.</summary>
internal enum BotChatFlagSource
{
    Local,
    Moderation,
    Output,
}

/// <summary>
/// One flagged line (<c>.playerbot chat flags</c>): who said it (the character's name and GUID and the account id; never an account
/// name or address), to which bot, the category, and an excerpt (at most 60 characters, emails and phone numbers removed) only when
/// <see cref="PlayerbotChatSafetyOptions.StoreExcerpt"/> is on. For an <see cref="BotChatFlagSource.Output"/> flag the excerpt is the
/// model's reply and the player gets no strike.
/// </summary>
internal sealed record BotChatFlag(
    DateTimeOffset At, ulong PlayerGuid, string PlayerName, int AccountId, string Bot, string Category, BotChatFlagSource Source, string? Excerpt);

/// <summary>An automatic mute the world thread applies (<see cref="PlayerbotChatSafetyOptions.AutoMute"/>).</summary>
internal sealed record BotChatMute(int AccountId, ulong PlayerGuid, string PlayerName, int Minutes);

/// <summary>A player's standing with the bot chat safety (<c>.playerbot chat flags &lt;player&gt;</c>).</summary>
internal sealed record BotChatPlayerSafety(ulong PlayerGuid, string PlayerName, int Strikes, int CutoffSeconds, bool? AiReplies);

/// <summary>The safety line of <c>.playerbot chat status</c>.</summary>
internal sealed record BotChatSafetyStatus(
    bool Screening, long Screened, long Flagged, long ModerationFlagged, long OutputFlagged, long BuiltinOnly, long Cutoffs, int CutOffNow,
    long AutoMutes, int OptedOut, int OptedIn, long Disclosed, int Terms, int Patterns);

/// <summary>
/// The state of bot chat safety (docs/areas/playbots.md, Safety and provider policies): the local filter (the shipped list and the
/// operator's file, re-read when that file changes), flag records in a bounded ring, per-player strikes that decay and the cut-off
/// they lead to, the AI disclosure once per login, and the players' "ai on" / "ai off" choice. All of it lives in this process: a
/// restart clears strikes, cut-offs and choices (no database table). Thread-safe: the world thread and the chat workers both use it.
/// </summary>
internal sealed class PlayerbotChatSafety
{
    /// <summary>The longest excerpt kept.</summary>
    internal const int ExcerptLength = 60;

    /// <summary>Players whose state is kept at most (the ones with nothing to remember are dropped first).</summary>
    internal const int MaxPlayers = 8192;

    /// <summary>How often the operator's term file is checked for a change.</summary>
    internal static readonly TimeSpan TermsFileCheck = TimeSpan.FromSeconds(10);

    private static readonly ConditionalWeakTable<object, StrongBox<long>> Logins = new();
    private static long s_nextLogin;

    private readonly Func<PlayerbotChatOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, PlayerState> _players = [];
    private readonly Queue<BotChatFlag> _flags = new();
    private readonly Dictionary<ulong, long> _disclosed = [];
    private readonly ConcurrentQueue<BotChatMute> _mutes = new();
    private readonly object _filterGate = new();
    private PlayerbotChatSafetyFilter _filter = PlayerbotChatSafetyFilter.Default;
    private (string File, bool Replace, DateTime Written) _filterSource = (string.Empty, false, default);
    private DateTimeOffset _filterCheckedAt = DateTimeOffset.MinValue;
    private long _screened;
    private long _flagged;
    private long _moderationFlagged;
    private long _outputFlagged;
    private long _builtinOnly;
    private long _cutoffs;
    private long _autoMutes;
    private long _disclosures;

    internal PlayerbotChatSafety(Func<PlayerbotChatOptions> options, TimeProvider time, ILogger logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    private PlayerbotChatSafetyOptions Options => _options().Safety;

    /// <summary>
    /// One number per login of a player (world thread): a new <see cref="Game.Entities.Player"/> is made at every login, so the
    /// disclosure is once per login without any login hook.
    /// </summary>
    internal static long LoginOf(object player) => Logins.GetValue(player, _ => new StrongBox<long>(Interlocked.Increment(ref s_nextLogin))).Value;

    // --- the filter -------------------------------------------------------------------------------------------------------

    /// <summary>The filter for the current options: the shipped list, plus or instead of the operator's file (re-read when it changes).</summary>
    internal PlayerbotChatSafetyFilter Filter()
    {
        PlayerbotChatSafetyOptions options = Options;
        string file = options.TermsFile?.Trim() ?? string.Empty;
        bool replace = options.ReplaceDefaultTerms;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_filterGate)
        {
            bool sameKeys = _filterSource.File == file && _filterSource.Replace == replace;
            if (sameKeys && (file.Length == 0 || now - _filterCheckedAt < TermsFileCheck)) return _filter;
            _filterCheckedAt = now;
            DateTime written = file.Length == 0 ? default : WrittenAt(file);
            if (sameKeys && written == _filterSource.Written) return _filter;
            PlayerbotChatSafetyFilter filter = file.Length == 0 ? PlayerbotChatSafetyFilter.Default : PlayerbotChatSafetyFilter.Load(file, replace);
            foreach (string problem in filter.Problems) _logger.LogWarning("Bot chat safety: {Problem}", problem);
            if (file.Length > 0)
                _logger.LogInformation("Bot chat safety: {Terms} terms and {Patterns} patterns loaded ({Source})",
                    filter.Size.Terms, filter.Size.Patterns, replace ? file : "the shipped list and " + file);
            _filter = filter;
            _filterSource = (file, replace, written);
            return filter;
        }
    }

    private static DateTime WrittenAt(string file)
    {
        try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : default; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return default; }
    }

    /// <summary>Screen a player's line (counted in the status).</summary>
    internal SafetyVerdict ScreenInput(string text)
    {
        Interlocked.Increment(ref _screened);
        return Filter().Screen(text, Options.Categories);
    }

    /// <summary>Screen a model's reply.</summary>
    internal SafetyVerdict ScreenOutput(string text) => Filter().Screen(text, Options.Categories);

    // --- who may reach a model provider -----------------------------------------------------------------------------------

    /// <summary>
    /// Whether <paramref name="ask"/>'s speaker may get a model reply: not cut off, not opted out, and opted in when
    /// <see cref="PlayerbotChatSafetyOptions.RequireOptIn"/>. Otherwise the built-in provider answers (counted).
    /// </summary>
    internal bool ModelAllowed(BotChatAsk ask)
    {
        PlayerbotChatSafetyOptions options = Options;
        DateTimeOffset now = _time.GetUtcNow();
        bool allowed;
        lock (_gate)
        {
            PlayerState? state = _players.GetValueOrDefault(ask.SenderGuid.Value);
            bool? choice = state?.AiReplies;
            allowed = !(state is not null && state.CutoffUntil > now) && choice != false && (!options.RequireOptIn || choice == true);
        }

        if (!allowed) Interlocked.Increment(ref _builtinOnly);
        return allowed;
    }

    /// <summary>
    /// A whispered "ai on", "ai off" or "ai" (world thread): the bot's answer, or null when the line is not one of them or the
    /// commands are off (<see cref="PlayerbotChatSafetyOptions.AllowOptOut"/> and <see cref="PlayerbotChatSafetyOptions.RequireOptIn"/> both off).
    /// </summary>
    internal string? TryChoice(BotChatAsk ask)
    {
        PlayerbotChatSafetyOptions options = Options;
        if (ask.Channel != Protocol.ChatType.Whisper || !(options.AllowOptOut || options.RequireOptIn)) return null;
        string line = new string([.. ask.Text.Where(c => char.IsLetter(c) || c == ' ')]).Trim().ToLowerInvariant();
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (line is not ("ai off" or "ai on" or "ai")) return null;
        bool? choice = line == "ai off" ? false : line == "ai on" ? true : null;
        lock (_gate)
        {
            PlayerState state = StateOf(ask);
            if (line == "ai") choice = state.AiReplies;
            else state.AiReplies = choice;
        }

        bool on = choice == true || (choice is null && !options.RequireOptIn);
        if (line == "ai")
            return on ? "Bot replies to you may come from an AI service. Whisper \"ai off\" to get built-in replies only."
                : "Bots answer you with built-in replies only. Whisper \"ai on\" to allow replies from an AI service.";
        return choice == true
            ? "Okay: bot replies to you may now come from an AI service. Whisper \"ai off\" to stop that."
            : "Okay: bots will answer you with built-in replies only, and nothing you say to them goes to an AI service. Whisper \"ai on\" to undo.";
    }

    /// <summary>
    /// The AI disclosure for <paramref name="ask"/>'s speaker, once per login (<paramref name="login"/>, from <see cref="LoginOf"/>),
    /// while a model provider is configured and the player has not opted out; null otherwise.
    /// </summary>
    internal string? TakeDisclosure(BotChatAsk ask, long login, bool modelConfigured)
    {
        PlayerbotChatSafetyOptions options = Options;
        if (!options.Disclosure || !modelConfigured) return null;
        lock (_gate)
        {
            if (_players.GetValueOrDefault(ask.SenderGuid.Value)?.AiReplies == false) return null;
            if (_disclosed.TryGetValue(ask.SenderGuid.Value, out long seen) && seen == login) return null;
            if (_disclosed.Count >= MaxPlayers) _disclosed.Clear();
            _disclosed[ask.SenderGuid.Value] = login;
        }

        Interlocked.Increment(ref _disclosures);
        string hint = options.RequireOptIn ? " Whisper any bot \"ai on\" to allow that; until then bots use built-in replies."
            : options.AllowOptOut ? " Whisper any bot \"ai off\" for built-in replies only." : string.Empty;
        return options.DisclosureText.Trim() + hint;
    }

    // --- flags and strikes ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Record a flag: the ring, a log warning, and for <paramref name="strike"/> a strike that may cut the player off (and, with
    /// <see cref="PlayerbotChatSafetyOptions.AutoMute"/>, queue a mute for the world thread).
    /// </summary>
    internal void Flag(BotChatAsk ask, string category, BotChatFlagSource source, bool strike, string flaggedText)
    {
        PlayerbotChatSafetyOptions options = Options;
        DateTimeOffset now = _time.GetUtcNow();
        string? excerpt = options.StoreExcerpt ? Excerpt(flaggedText) : null;
        var record = new BotChatFlag(now, ask.SenderGuid.Value, ask.SenderName, ask.SenderAccountId, ask.Persona.Name, category, source, excerpt);
        if (source == BotChatFlagSource.Output) Interlocked.Increment(ref _outputFlagged);
        else if (source == BotChatFlagSource.Moderation) Interlocked.Increment(ref _moderationFlagged);
        else Interlocked.Increment(ref _flagged);
        int strikes = 0;
        bool cutOff = false;
        lock (_gate)
        {
            if (options.FlagLogSize > 0)
            {
                _flags.Enqueue(record);
                while (_flags.Count > options.FlagLogSize) _flags.Dequeue();
            }
            else
            {
                _flags.Clear();
            }

            if (strike)
            {
                PlayerState state = StateOf(ask);
                state.Prune(now, TimeSpan.FromMinutes(options.StrikeWindowMinutes));
                if (state.CutoffUntil <= now)
                {
                    state.Strikes.Enqueue(now);
                    strikes = state.Strikes.Count;
                    if (strikes >= options.StrikesBeforeCutoff)
                    {
                        state.CutoffUntil = now + TimeSpan.FromMinutes(options.CutoffMinutes);
                        state.Strikes.Clear();
                        cutOff = true;
                        _cutoffs++;
                        if (options.AutoMute && ask.SenderAccountId > 0)
                        {
                            _mutes.Enqueue(new BotChatMute(ask.SenderAccountId, ask.SenderGuid.Value, ask.SenderName, options.AutoMuteMinutes));
                            _autoMutes++;
                        }
                    }
                }
            }
        }

        string shown = excerpt is null ? string.Empty : $" \"{excerpt}\"";
        _logger.LogWarning("Bot chat safety: {Source} flagged {Category} from {Player} (guid {Guid}, account {Account}) to {Bot}{Excerpt}",
            Words(source), category, ask.SenderName, ask.SenderGuid.Value, ask.SenderAccountId, ask.Persona.Name, shown);
        if (cutOff)
            _logger.LogWarning("Bot chat safety: {Player} (guid {Guid}, account {Account}) is cut off from the model providers for {Minutes} minutes after {Strikes} strikes{Mute}",
                ask.SenderName, ask.SenderGuid.Value, ask.SenderAccountId, options.CutoffMinutes, strikes,
                options.AutoMute && ask.SenderAccountId > 0 ? $"; muted for {options.AutoMuteMinutes} minutes" : string.Empty);
    }

    internal static string Words(BotChatFlagSource source) => source switch
    {
        BotChatFlagSource.Moderation => "moderation",
        BotChatFlagSource.Output => "output screening",
        _ => "local filter",
    };

    /// <summary>At most <see cref="ExcerptLength"/> characters of <paramref name="text"/>, one line, emails and phone numbers removed.</summary>
    internal static string Excerpt(string text)
    {
        string clean = PlayerbotChatSafetyFilter.StripPersonalData(PlayerbotChatPrompts.Clean(text, 1024));
        return clean.Length <= ExcerptLength ? clean : clean[..(ExcerptLength - 3)] + "...";
    }

    /// <summary>The automatic mutes waiting for the world thread.</summary>
    internal IReadOnlyList<BotChatMute> DrainMutes()
    {
        if (_mutes.IsEmpty) return [];
        var mutes = new List<BotChatMute>();
        while (_mutes.TryDequeue(out BotChatMute? mute)) mutes.Add(mute);
        return mutes;
    }

    /// <summary>The flag records, newest first; only <paramref name="player"/>'s (by character name) when given.</summary>
    internal IReadOnlyList<BotChatFlag> Flags(string? player = null)
    {
        lock (_gate)
        {
            return [.. _flags.Reverse().Where(flag => player is null || string.Equals(flag.PlayerName, player, StringComparison.OrdinalIgnoreCase))];
        }
    }

    /// <summary>A player's strikes, cut-off and AI choice (by character name), or null when nothing is held about them.</summary>
    internal BotChatPlayerSafety? Player(string name)
    {
        PlayerbotChatSafetyOptions options = Options;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            foreach ((ulong guid, PlayerState state) in _players)
            {
                if (!string.Equals(state.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                state.Prune(now, TimeSpan.FromMinutes(options.StrikeWindowMinutes));
                int cutoff = state.CutoffUntil > now ? (int)Math.Ceiling((state.CutoffUntil - now).TotalSeconds) : 0;
                return new BotChatPlayerSafety(guid, state.Name, state.Strikes.Count, cutoff, state.AiReplies);
            }
        }

        return null;
    }

    /// <summary>Clear a player's strikes and cut-off (by character name, or GUID when known). False when they had neither.</summary>
    internal bool Pardon(string name, ulong? guid = null)
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool had = false;
        lock (_gate)
        {
            foreach (PlayerState state in _players.Where(pair => pair.Key == guid || string.Equals(pair.Value.Name, name, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Value))
            {
                had |= state.Strikes.Count > 0 || state.CutoffUntil > now;
                state.Strikes.Clear();
                state.CutoffUntil = default;
            }
        }

        if (had) _logger.LogInformation("Bot chat safety: {Player} pardoned (strikes and cut-off cleared)", name);
        return had;
    }

    internal BotChatSafetyStatus Status()
    {
        PlayerbotChatSafetyOptions options = Options;
        DateTimeOffset now = _time.GetUtcNow();
        (int terms, int patterns) = Filter().Size;
        lock (_gate)
        {
            return new BotChatSafetyStatus(options.Enabled, Interlocked.Read(ref _screened), Interlocked.Read(ref _flagged),
                Interlocked.Read(ref _moderationFlagged), Interlocked.Read(ref _outputFlagged), Interlocked.Read(ref _builtinOnly), _cutoffs,
                _players.Values.Count(state => state.CutoffUntil > now), _autoMutes, _players.Values.Count(state => state.AiReplies == false),
                _players.Values.Count(state => state.AiReplies == true), Interlocked.Read(ref _disclosures), terms, patterns);
        }
    }

    /// <summary>Under <see cref="_gate"/>: the speaker's state, made when missing (the oldest idle players dropped past <see cref="MaxPlayers"/>).</summary>
    private PlayerState StateOf(BotChatAsk ask)
    {
        if (!_players.TryGetValue(ask.SenderGuid.Value, out PlayerState? state))
        {
            if (_players.Count >= MaxPlayers)
            {
                DateTimeOffset now = _time.GetUtcNow();
                foreach (ulong idle in _players.Where(pair => pair.Value.Strikes.Count == 0 && pair.Value.CutoffUntil <= now && pair.Value.AiReplies is null)
                    .Select(pair => pair.Key).ToArray())
                    _players.Remove(idle);
            }

            _players[ask.SenderGuid.Value] = state = new PlayerState();
        }

        state.Name = ask.SenderName;
        if (ask.SenderAccountId > 0) state.AccountId = ask.SenderAccountId;
        return state;
    }

    private sealed class PlayerState
    {
        public string Name = string.Empty;
        public int AccountId;
        public Queue<DateTimeOffset> Strikes { get; } = new();
        public DateTimeOffset CutoffUntil;

        /// <summary>The player's "ai on" (true) or "ai off" (false); null: never said.</summary>
        public bool? AiReplies;

        public void Prune(DateTimeOffset now, TimeSpan window)
        {
            while (Strikes.Count > 0 && now - Strikes.Peek() >= window) Strikes.Dequeue();
        }
    }
}
