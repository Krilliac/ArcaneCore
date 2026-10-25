using System.Globalization;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What <see cref="PlayerbotRivalryOptOut.Handle"/> made of a line: whether it was a command (and so is not handled further) and the answer to send, if any.</summary>
internal readonly record struct PlayerbotRivalryCommandResult(bool Consumed, string? Answer)
{
    /// <summary>The line is not a rivalry command.</summary>
    internal static PlayerbotRivalryCommandResult NotACommand => default;
}

/// <summary>
/// The server-wide store behind the whispered commands "rivals off", "rivals on" and "rivals" (docs/areas/playbots-social.md, Rivalry): which real
/// players do not want bot taunts. One instance, owned by <see cref="PlayerbotSocialFeature"/>; a choice made through one bot holds for every
/// bot of both factions. A choice follows the account (it is kept under the account id; the character guid stands in for an account id of 0), so
/// opting out one character opts out all of them. In memory only: a restart clears every choice, and the answers say so.
/// <para>
/// <see cref="Handle"/> parses the line as <c>PlayerbotChatSafety.TryChoice</c> parses "ai off" (letters and spaces, lower case) and answers at most
/// once per player per <see cref="AnswerGapMs"/>; <see cref="PlayerbotSocialRivalryOptions.AllowOptOut"/> only decides whether the line is parsed at
/// all, and a choice already recorded is read by <see cref="IsOptedOut"/> whatever that option says. Bounded by <see cref="MaxPlayers"/>: past it the
/// oldest "rivals on" is dropped to make room, and an opt-out is never dropped: a table full of opt-outs refuses a new choice (the player is told,
/// and the meter counts a "refused" change). Thread-safe (one lock; every call is a few dictionary operations).
/// </para>
/// </summary>
internal sealed class PlayerbotRivalryOptOut
{
    /// <summary>The most players whose choice, answer time or disclosure is kept (as the chat safety's table).</summary>
    internal const int MaxPlayers = 8192;

    /// <summary>The least time, in milliseconds, between two answers to one player; a command line inside the gap is applied and not answered.</summary>
    internal const long AnswerGapMs = 10_000;

    private const string OffAnswer = "Okay: bots will not taunt you. This lasts until the server restarts; whisper \"rivals off\" again after that. Whisper \"rivals on\" to undo.";
    private const string OnAnswer = "Okay: bots may taunt you again. Whisper \"rivals off\" to stop it.";
    private const string FullAnswer = "Sorry: the server cannot record more choices right now, so yours was not saved. Please tell a game master.";
    private const string StatusMayTaunt = "Bots may taunt you (whisper \"rivals off\" to stop it)";
    private const string StatusOptedOut = "Bots will not taunt you until the server restarts (whisper \"rivals on\" to undo)";
    private const string StatusNotUnlessOn = "Bots will not taunt you unless you whisper \"rivals on\" (this lasts until the server restarts)";
    private const string Disclosure = "A bot rival taunted you. Whisper \"rivals off\" to any bot of your faction to stop bot taunts.";

    private readonly object _gate = new();
    private readonly Dictionary<long, Choice> _choices = [];
    private readonly Dictionary<ulong, long> _answered = [];
    private readonly Dictionary<ulong, long> _disclosed = [];
    private long _sequence;

    private sealed class Choice
    {
        public bool OptedIn;
        public long Sequence;
    }

    /// <summary>Players with a recorded choice.</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _choices.Count;
        }
    }

    /// <summary>The choice of the account: false after "rivals off", true after "rivals on", null when it never chose.</summary>
    internal bool? ChoiceOf(ulong characterGuid, int accountId)
    {
        lock (_gate) return _choices.TryGetValue(KeyOf(characterGuid, accountId), out Choice? choice) ? choice.OptedIn : null;
    }

    /// <summary>Whether the account said "rivals off". Read by every outward act, whatever <see cref="PlayerbotSocialRivalryOptions.AllowOptOut"/> says.</summary>
    internal bool IsOptedOut(ulong characterGuid, int accountId) => ChoiceOf(characterGuid, accountId) == false;

    /// <summary>
    /// A whispered line from a real player. Not a command (or commands are not parsed): <see cref="PlayerbotRivalryCommandResult.NotACommand"/>.
    /// Otherwise the choice is applied and the line is consumed; the answer is null when the player was answered less than <see cref="AnswerGapMs"/> ago.
    /// </summary>
    internal PlayerbotRivalryCommandResult Handle(ulong characterGuid, int accountId, string text, long nowMs, PlayerbotSocialRivalryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!(options.AllowOptOut || options.Emotes.RudeRequiresOptIn)) return PlayerbotRivalryCommandResult.NotACommand;
        string line = Normalise(text);
        if (line is not ("rivals off" or "rivals on" or "rivals")) return PlayerbotRivalryCommandResult.NotACommand;
        lock (_gate)
        {
            long key = KeyOf(characterGuid, accountId);
            bool refused = false;
            if (line != "rivals")
            {
                if (Set(key, optedIn: line == "rivals on")) PlayerbotRivalryMeters.CountOptOut(line == "rivals off" ? "off" : "on");
                else
                {
                    // Every slot holds an opt-out, and an opt-out is never dropped: the new choice is refused and the player is told.
                    PlayerbotRivalryMeters.CountOptOut("refused");
                    refused = true;
                }
            }

            if (_answered.TryGetValue(characterGuid, out long last) && nowMs - last < AnswerGapMs) return new PlayerbotRivalryCommandResult(true, null);
            if (_answered.Count >= MaxPlayers) _answered.Clear();
            _answered[characterGuid] = nowMs;
            bool? choice = _choices.TryGetValue(key, out Choice? current) ? current.OptedIn : null;
            string answer = refused ? FullAnswer
                : line == "rivals off" ? OffAnswer
                : line == "rivals on" ? OnAnswer
                : choice == false ? StatusOptedOut
                : options.Emotes.RudeRequiresOptIn && choice != true ? StatusNotUnlessOn
                : StatusMayTaunt;
            return new PlayerbotRivalryCommandResult(true, answer);
        }
    }

    /// <summary>
    /// The notice that follows the first rude emote a player receives in a login (<paramref name="login"/>, from <c>PlayerbotChatSafety.LoginOf</c>); null
    /// when it has been given for this login or the player opted out.
    /// </summary>
    internal string? TakeDisclosure(ulong characterGuid, int accountId, long login)
    {
        lock (_gate)
        {
            if (_choices.TryGetValue(KeyOf(characterGuid, accountId), out Choice? choice) && !choice.OptedIn) return null;
            if (_disclosed.TryGetValue(characterGuid, out long seen) && seen == login) return null;
            if (_disclosed.Count >= MaxPlayers) _disclosed.Clear();
            _disclosed[characterGuid] = login;
            return Disclosure;
        }
    }

    // Letters and spaces only, lower case, the spaces collapsed (the chat safety's "ai off" rule): "Rivals Off!" and "  rivals    off " are commands.
    private static string Normalise(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string letters = new([.. text.Where(c => char.IsLetter(c) || c == ' ')]);
        return string.Join(' ', letters.ToLower(CultureInfo.InvariantCulture).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // The account id when there is one; the character guid, kept apart from every account id, otherwise.
    private static long KeyOf(ulong characterGuid, int accountId) => accountId > 0 ? accountId : -1 - (long)(characterGuid & 0x3FFF_FFFF_FFFF_FFFFUL);

    // Under _gate. False when the choice could not be kept: the table is full of opt-outs, which are never dropped.
    private bool Set(long key, bool optedIn)
    {
        if (!_choices.TryGetValue(key, out Choice? choice))
        {
            if (_choices.Count >= MaxPlayers && !EvictOptedIn()) return false;
            _choices[key] = choice = new Choice();
        }

        choice.OptedIn = optedIn;
        choice.Sequence = ++_sequence;
        return true;
    }

    // Under _gate. Drops the oldest "rivals on" (the default for everyone but a RudeRequiresOptIn world); false when every entry is an opt-out.
    private bool EvictOptedIn()
    {
        long victim = 0;
        bool found = false;
        long best = long.MaxValue;
        foreach (KeyValuePair<long, Choice> entry in _choices)
        {
            if (!entry.Value.OptedIn || entry.Value.Sequence >= best) continue;
            found = true;
            victim = entry.Key;
            best = entry.Value.Sequence;
        }

        return found && _choices.Remove(victim);
    }
}
