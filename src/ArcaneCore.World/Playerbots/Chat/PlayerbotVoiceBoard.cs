using ArcaneCore.Game;

namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>What a bot called out (<see cref="PlayerbotVoicePost"/>).</summary>
internal enum PlayerbotVoicePostKind : byte
{
    /// <summary>An LFG or LFM call for a group goal the bot waits on.</summary>
    LookingForGroup,

    /// <summary>A WTS call for an item in its bags.</summary>
    Selling,

    /// <summary>A WTB call for a reagent it lacks.</summary>
    Buying,
}

/// <summary>
/// One call a bot made (<see cref="PlayerbotVoiceBoard"/>), kept so a whisper about it can be answered: the item and price of a WTS (the
/// item's entry to check it is still in the bags), the dungeon or quest and least level of an LFG call.
/// </summary>
internal sealed record PlayerbotVoicePost(
    PlayerbotVoicePostKind Kind,
    long PostedMs,
    string? What = null,
    uint ItemEntry = 0,
    string? Price = null,
    int Count = 0,
    byte MinLevel = 0);

/// <summary>
/// The bot voice's memory on the chat service (<see cref="PlayerbotChat.Voice"/>): each bot's latest call, and the resurrections a bot
/// owes thanks for (noted by the party AI when it accepts one). Times are the chat clock's (<see cref="PlayerbotChat.NowMs"/>). World thread
/// in practice; locked so a test may drive it from anywhere.
/// </summary>
internal sealed class PlayerbotVoiceBoard
{
    /// <summary>How long a resurrection is remembered for thanks (two minutes).</summary>
    internal const long ResurrectionThanksMs = 120_000;

    private const int MaxEntries = 4096;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, PlayerbotVoicePost> _posts = [];
    private readonly Dictionary<Guid, (ObjectGuid Caster, long AtMs)> _resurrections = [];

    /// <summary>Calls on the board (tests and status).</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _posts.Count;
        }
    }

    /// <summary>Remember <paramref name="post"/> as <paramref name="bot"/>'s latest call (replacing the one before).</summary>
    internal void Post(Guid bot, PlayerbotVoicePost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        lock (_gate)
        {
            if (_posts.Count >= MaxEntries && !_posts.ContainsKey(bot)) _posts.Clear();
            _posts[bot] = post;
        }
    }

    /// <summary><paramref name="bot"/>'s call made within <paramref name="lifeMs"/>, or null.</summary>
    internal PlayerbotVoicePost? Find(Guid bot, long nowMs, long lifeMs)
    {
        lock (_gate)
        {
            if (!_posts.TryGetValue(bot, out PlayerbotVoicePost? post)) return null;
            if (nowMs - post.PostedMs < lifeMs) return post;
            _posts.Remove(bot);
            return null;
        }
    }

    /// <summary>Forget <paramref name="bot"/>'s call (it was answered for good, or the bot is gone).</summary>
    internal void Forget(Guid bot)
    {
        lock (_gate) _posts.Remove(bot);
    }

    /// <summary>The party AI accepted <paramref name="caster"/>'s resurrection of <paramref name="bot"/>.</summary>
    internal void NoteResurrection(Guid bot, ObjectGuid caster, long nowMs)
    {
        lock (_gate)
        {
            if (_resurrections.Count >= MaxEntries) _resurrections.Clear();
            _resurrections[bot] = (caster, nowMs);
        }
    }

    /// <summary>Take the resurrection <paramref name="bot"/> owes thanks for (within <see cref="ResurrectionThanksMs"/>), once.</summary>
    internal bool TakeResurrection(Guid bot, long nowMs, out ObjectGuid caster)
    {
        lock (_gate)
        {
            caster = default;
            if (!_resurrections.Remove(bot, out (ObjectGuid Caster, long AtMs) entry) || nowMs - entry.AtMs >= ResurrectionThanksMs) return false;
            caster = entry.Caster;
            return true;
        }
    }

    /// <summary>Whether <paramref name="bot"/> owes thanks for a resurrection (without taking it).</summary>
    internal bool OwesThanks(Guid bot)
    {
        lock (_gate) return _resurrections.ContainsKey(bot);
    }
}
