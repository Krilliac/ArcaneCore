namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// What the local filter screens for (<see cref="PlayerbotChatSafetyOptions.Categories"/>; a comma-separated list in configuration).
/// The term and pattern lists of every category but <see cref="PersonalData"/> are in the data file
/// (<c>PlayerbotChatSafetyTerms.json</c>, extended or replaced by <see cref="PlayerbotChatSafetyOptions.TermsFile"/>).
/// </summary>
[Flags]
public enum PlayerbotChatSafetyCategories
{
    None = 0,

    /// <summary>Sexual content involving minors.</summary>
    SexualMinors = 1,

    /// <summary>Self-harm and suicide (no strike: the player gets a short supportive line).</summary>
    SelfHarm = 2,

    /// <summary>Threats of real-world violence (doxxing, swatting, "I know where you live").</summary>
    Threats = 4,

    /// <summary>Slurs and calls for violence against protected groups.</summary>
    Hate = 8,

    /// <summary>Targeted harassment ("kys").</summary>
    Harassment = 16,

    /// <summary>Real-world weapons and illegal drugs (making explosives, buying hard drugs).</summary>
    IllegalGoods = 32,

    /// <summary>Email addresses, phone numbers and street addresses (built into the code; no strike).</summary>
    PersonalData = 64,

    /// <summary>Every category.</summary>
    All = SexualMinors | SelfHarm | Threats | Hate | Harassment | IllegalGoods | PersonalData,
}

/// <summary>What happens to a player's line the screening flagged (it never reaches a model provider either way).</summary>
public enum PlayerbotChatFlaggedAction
{
    /// <summary>The built-in provider's short brush-off (a supportive line for self-harm, a caution for personal data).</summary>
    BuiltinReply,

    /// <summary>No reply.</summary>
    Ignore,
}

/// <summary>What happens to a model reply the local filter flags (it is never said).</summary>
public enum PlayerbotChatOutputAction
{
    /// <summary>The built-in provider answers the player's line instead.</summary>
    BuiltinReply,

    /// <summary>No reply.</summary>
    Drop,
}

/// <summary>
/// <c>World:Playerbots:Chat:Safety</c> (docs/areas/playbots.md, Safety and provider policies): what is screened before a player's
/// line can reach a model provider, how flagged players are limited, the AI disclosure and opt-out, and how little is sent. Every key
/// is live: <c>.reload config</c> changes this object in place.
/// </summary>
public sealed class PlayerbotChatSafetyOptions
{
    /// <summary>The longest <see cref="DisclosureText"/>.</summary>
    public const int MaxDisclosureLength = 200;

    /// <summary>
    /// Screen every player line before any model provider sees it (the local filter, then each provider's optional moderation step),
    /// and every model reply before a bot says it (<see cref="ScreenOutput"/>). On by default. A flagged line never reaches a model.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The local filter's categories (comma-separated; all by default): SexualMinors, SelfHarm, Threats, Hate, Harassment, IllegalGoods, PersonalData.</summary>
    public PlayerbotChatSafetyCategories Categories { get; set; } = PlayerbotChatSafetyCategories.All;

    /// <summary>
    /// An operator's term file (the shipped file's JSON format), relative to the working directory or absolute. Empty: only the shipped
    /// list. Its entries are added to the shipped ones unless <see cref="ReplaceDefaultTerms"/>; it is read again when it changes.
    /// </summary>
    public string TermsFile { get; set; } = string.Empty;

    /// <summary>Use only <see cref="TermsFile"/>'s lists, not the shipped ones (off).</summary>
    public bool ReplaceDefaultTerms { get; set; }

    /// <summary>What a flagged player line gets: <c>BuiltinReply</c> (default) or <c>Ignore</c>.</summary>
    public PlayerbotChatFlaggedAction OnFlagged { get; set; } = PlayerbotChatFlaggedAction.BuiltinReply;

    /// <summary>Screen each model reply with the local filter before the bot says it (on).</summary>
    public bool ScreenOutput { get; set; } = true;

    /// <summary>What replaces a flagged model reply: <c>BuiltinReply</c> (default) or <c>Drop</c>.</summary>
    public PlayerbotChatOutputAction OnOutputFlagged { get; set; } = PlayerbotChatOutputAction.BuiltinReply;

    /// <summary>Strikes within <see cref="StrikeWindowMinutes"/> that cut a player off from the model providers (1..100).</summary>
    public int StrikesBeforeCutoff { get; set; } = 3;

    /// <summary>Minutes a strike counts (1..10080); older strikes fall away.</summary>
    public int StrikeWindowMinutes { get; set; } = 60;

    /// <summary>Minutes a cut-off player gets built-in replies only (1..10080).</summary>
    public int CutoffMinutes { get; set; } = 60;

    /// <summary>Also mute a player's chat (the account mute of <c>.mute</c>) when they are cut off (off).</summary>
    public bool AutoMute { get; set; }

    /// <summary>The automatic mute's length in minutes (1..10080).</summary>
    public int AutoMuteMinutes { get; set; } = 30;

    /// <summary>Flag records kept in memory for <c>.playerbot chat flags</c> (0..10000; the oldest go first).</summary>
    public int FlagLogSize { get; set; } = 200;

    /// <summary>
    /// Keep a short excerpt of each flagged line (at most 60 characters, emails, phone numbers and addresses removed) in the flag
    /// record and the log warning (on). Off: only who, when, which bot and which category.
    /// </summary>
    public bool StoreExcerpt { get; set; } = true;

    /// <summary>
    /// Tell each player once per login, the first time they talk to a bot, that bot replies may come from an AI service (on; only
    /// while a model provider is configured).
    /// </summary>
    public bool Disclosure { get; set; } = true;

    /// <summary>The disclosure (1..200 characters, one line, no '|'); the opt-out or opt-in hint is added after it.</summary>
    public string DisclosureText { get; set; } = "Bot replies on this server may be written by an AI service, which receives what you say to bots.";

    /// <summary>Let a player whisper a bot "ai off" (built-in replies only) or "ai on" (on).</summary>
    public bool AllowOptOut { get; set; } = true;

    /// <summary>Use the model providers only for players who whispered a bot "ai on" (off). Everyone else gets built-in replies.</summary>
    public bool RequireOptIn { get; set; }

    /// <summary>Replace emails, phone numbers and street addresses in what is sent to a model provider with a placeholder (on).</summary>
    public bool StripPersonalData { get; set; } = true;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chat:Safety";
        if (CheckCategories(Categories) is { } categories) throw new InvalidOperationException($"{section}: Categories {categories}");
        if (CheckTermsFile(TermsFile) is { } file) throw new InvalidOperationException($"{section}: TermsFile {file}");
        if (!Enum.IsDefined(OnFlagged)) throw new InvalidOperationException($"{section}: OnFlagged must be BuiltinReply or Ignore.");
        if (!Enum.IsDefined(OnOutputFlagged)) throw new InvalidOperationException($"{section}: OnOutputFlagged must be BuiltinReply or Drop.");
        if (StrikesBeforeCutoff is < 1 or > 100) throw new InvalidOperationException($"{section}: StrikesBeforeCutoff must be 1..100.");
        if (CheckMinutes(StrikeWindowMinutes) is { } window) throw new InvalidOperationException($"{section}: StrikeWindowMinutes {window}");
        if (CheckMinutes(CutoffMinutes) is { } cutoff) throw new InvalidOperationException($"{section}: CutoffMinutes {cutoff}");
        if (CheckMinutes(AutoMuteMinutes) is { } mute) throw new InvalidOperationException($"{section}: AutoMuteMinutes {mute}");
        if (FlagLogSize is < 0 or > 10_000) throw new InvalidOperationException($"{section}: FlagLogSize must be 0..10000.");
        if (CheckDisclosure(DisclosureText) is { } text) throw new InvalidOperationException($"{section}: DisclosureText {text}");
    }

    internal static string? CheckCategories(PlayerbotChatSafetyCategories categories)
        => (categories & ~PlayerbotChatSafetyCategories.All) == 0 ? null
            : "must be a combination of SexualMinors, SelfHarm, Threats, Hate, Harassment, IllegalGoods and PersonalData";

    internal static string? CheckMinutes(int minutes) => minutes is >= 1 and <= 10_080 ? null : "must be 1..10080";

    internal static string? CheckTermsFile(string? path)
        => path is null ? "is missing" : path.Length > 260 || path.Any(char.IsControl) ? "must be a path of at most 260 characters" : null;

    internal static string? CheckDisclosure(string? text)
        => string.IsNullOrWhiteSpace(text) || text.Length > MaxDisclosureLength || text.Any(char.IsControl) || text.Contains('|')
            ? $"must be 1..{MaxDisclosureLength} characters on one line without '|'"
            : null;
}
