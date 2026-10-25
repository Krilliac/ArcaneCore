using System.Globalization;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>What <see cref="PlayerbotInviteOptOut.Handle"/> made of a line: whether it was a command (and so is not handled further) and the answer to send, if any.</summary>
internal readonly record struct PlayerbotInviteCommandResult(bool Consumed, string? Answer)
{
    /// <summary>The line is not an invite command.</summary>
    internal static PlayerbotInviteCommandResult NotACommand => default;
}

/// <summary>
/// The server-wide store behind the whispered commands "invites off", "invites on" and "invites" (docs/areas/playbots-social.md, Membership): which
/// real players do not want an UNSOLICITED group or guild invitation from a bot. One instance, owned by <see cref="PlayerbotSocialFeature"/>; a
/// choice made through one bot holds for every bot of both factions. A choice follows the account (kept under the account id; the character guid
/// stands in for an account id of 0), so opting out one character opts out all of them. Asking is still consent: a player who whispers "inv" to a
/// bot's group call, or "ginv", is invited whatever this store says, and the bots' own accept policy is not touched. In memory only: a restart
/// clears every choice, and the answers say so.
/// <para>
/// <see cref="Handle"/> parses the line as <see cref="PlayerbotRivalryOptOut.Handle"/> does (letters and spaces, lower case) and answers at most once
/// per player per <see cref="AnswerGapMs"/>; <see cref="PlayerbotSocialInviteOptions.AllowOptOut"/> only decides whether the line is parsed at all, and
/// a choice already recorded is read by <see cref="IsOptedOut"/> whatever that option says. Bounded by <see cref="MaxPlayers"/>: past it the oldest
/// "invites on" is dropped to make room, and an opt-out is never dropped: a table full of opt-outs refuses a new choice (the player is told).
/// Thread-safe (one lock; every call is a few dictionary operations).
/// </para>
/// </summary>
internal sealed class PlayerbotInviteOptOut
{
    /// <summary>The most players whose choice or answer time is kept.</summary>
    internal const int MaxPlayers = 8192;

    /// <summary>The least time, in milliseconds, between two answers to one player; a command line inside the gap is applied and not answered.</summary>
    internal const long AnswerGapMs = 10_000;

    private const string OffAnswer = "Okay: bots will not invite you to a group or guild unless you ask. This lasts until the server restarts; whisper \"invites off\" again after that. Whisper \"invites on\" to undo.";
    private const string OnAnswer = "Okay: bots may invite you again. Whisper \"invites off\" to stop it.";
    private const string FullAnswer = "Sorry: the server cannot record more choices right now, so yours was not saved. Please tell a game master.";
    private const string StatusMayInvite = "Bots may invite you (whisper \"invites off\" to stop it)";
    private const string StatusOptedOut = "Bots will not invite you unless you ask, until the server restarts (whisper \"invites on\" to undo)";

    private readonly object _gate = new();
    private readonly Dictionary<long, Choice> _choices = [];
    private readonly Dictionary<ulong, long> _answered = [];
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

    /// <summary>The choice of the account: false after "invites off", true after "invites on", null when it never chose.</summary>
    internal bool? ChoiceOf(ulong characterGuid, int accountId)
    {
        lock (_gate) return _choices.TryGetValue(KeyOf(characterGuid, accountId), out Choice? choice) ? choice.OptedIn : null;
    }

    /// <summary>Whether the account said "invites off". Read by every unsolicited invitation, whatever <see cref="PlayerbotSocialInviteOptions.AllowOptOut"/> says.</summary>
    internal bool IsOptedOut(ulong characterGuid, int accountId) => ChoiceOf(characterGuid, accountId) == false;

    /// <summary>
    /// A whispered line from a real player. Not a command (or commands are not parsed): <see cref="PlayerbotInviteCommandResult.NotACommand"/>.
    /// Otherwise the choice is applied and the line is consumed; the answer is null when the player was answered less than <see cref="AnswerGapMs"/> ago.
    /// </summary>
    internal PlayerbotInviteCommandResult Handle(ulong characterGuid, int accountId, string text, long nowMs, PlayerbotSocialInviteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.AllowOptOut) return PlayerbotInviteCommandResult.NotACommand;
        string line = Normalise(text);
        if (line is not ("invites off" or "invites on" or "invites")) return PlayerbotInviteCommandResult.NotACommand;
        lock (_gate)
        {
            long key = KeyOf(characterGuid, accountId);
            bool refused = line != "invites" && !Set(key, optedIn: line == "invites on"); // every slot holds an opt-out, which is never dropped
            if (_answered.TryGetValue(characterGuid, out long last) && nowMs - last < AnswerGapMs) return new PlayerbotInviteCommandResult(true, null);
            if (_answered.Count >= MaxPlayers) _answered.Clear();
            _answered[characterGuid] = nowMs;
            bool? choice = _choices.TryGetValue(key, out Choice? current) ? current.OptedIn : null;
            string answer = refused ? FullAnswer
                : line == "invites off" ? OffAnswer
                : line == "invites on" ? OnAnswer
                : choice == false ? StatusOptedOut
                : StatusMayInvite;
            return new PlayerbotInviteCommandResult(true, answer);
        }
    }

    // Letters and spaces only, lower case, the spaces collapsed (the chat safety's "ai off" rule): "Invites Off!" and "  invites    off " are commands.
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

    // Under _gate. Drops the oldest "invites on"; false when every entry is an opt-out.
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
