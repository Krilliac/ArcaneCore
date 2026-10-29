using ArcaneCore.Game;
using ArcaneCore.Game.Economy;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>Which driver owns a bot's one trade slot.</summary>
internal enum PlayerbotTradeOwner : byte
{
    None,

    /// <summary>The trade desk (a fixed-price deal).</summary>
    Desk,

    /// <summary>A gratitude tip.</summary>
    Tip,

    /// <summary>A mage fee (the fee leg paying, or the mage serving it).</summary>
    Fee,

    /// <summary>The party AI's gift rules (a trusted partner's window, or the bot's own "trade me").</summary>
    Gift,
}

/// <summary>The owner of a bot's trade slot, the partner it trades with, and the live <see cref="TradeSession"/> once it was seen (null until then).</summary>
internal readonly record struct PlayerbotTradeClaim(PlayerbotTradeOwner Owner, ObjectGuid Partner, TradeSession? Session);

/// <summary>
/// The registry of who owns each bot's single trade slot. Ownership is recorded once, by the driver that sends CMSG_INITIATE_TRADE or CMSG_BEGIN_TRADE (it claims the
/// slot BEFORE it sends), and a driver sets gold, accepts or cancels only on a trade it owns. A driver that wants the slot while another one owns it defers, bounded by
/// its own timeout; it never cancels another driver's window or an unowned (stranger's) one. A claim heals itself: it binds to the <see cref="TradeSession"/> once seen;
/// an unbound claim with no session clears after <see cref="PendingGraceMs"/>; a bound claim whose session has been gone <see cref="GoneGraceMs"/> without a terminal
/// status clears too. One per feature, keyed by the bot's character guid. World thread in practice; one lock so a test may read it from anywhere. A session without the
/// playerbot feature has no registry (<see cref="Of(WorldSession)"/> is null): its drivers each behave as sole owner.
/// </summary>
internal sealed class PlayerbotTradeOwners
{
    /// <summary>How long a claim with no session yet is kept (the initiate or begin creates the session in the same step).</summary>
    internal const long PendingGraceMs = 2_000;

    /// <summary>How long a bound claim whose trade is gone waits for its terminal status before it clears.</summary>
    internal const long GoneGraceMs = 10_000;

    private readonly object _gate = new();
    private readonly Dictionary<ulong, Record> _records = [];

    /// <summary>Test seam: the registry's clock (milliseconds).</summary>
    internal Func<long> Clock { get; set; } = static () => Environment.TickCount64;

    /// <summary>The registry of the playerbot feature this session belongs to, or null.</summary>
    internal static PlayerbotTradeOwners? Of(WorldSession session) => session.Services.GetService<ManagedPlayerbotFeature>()?.TradeOwners;

    /// <summary>
    /// Claim the slot for <paramref name="owner"/> trading with <paramref name="partner"/>. Succeeds when there is no live claim (or it is the same owner and partner, or
    /// it is stale), and either <paramref name="live"/> is null (the driver is about to send an initiate) or it is the partner's own unopened request to this bot (the
    /// driver is about to send a begin). A claim made with a live trade binds to it.
    /// </summary>
    internal bool TryClaim(Player bot, PlayerbotTradeOwner owner, ObjectGuid partner, TradeSession? live)
    {
        ArgumentNullException.ThrowIfNull(bot);
        lock (_gate)
        {
            long now = Clock();
            PlayerbotTradeClaim current = Read(bot, live, now);
            if (current.Owner != PlayerbotTradeOwner.None && !(current.Owner == owner && current.Partner == partner)) return false;
            if (live is not null && !(!live.Opened && ReferenceEquals(live.Target.Player, bot) && live.OtherSide(bot).Player.Guid == partner)) return false;
            _records[bot.Guid.Value] = new Record { Owner = owner, Partner = partner, Session = live, ClaimedMs = now };
            return true;
        }
    }

    /// <summary>The claim on the bot's slot given its live trade (null: none), healing a stale record first. <see cref="PlayerbotTradeOwner.None"/> when nobody owns it.</summary>
    internal PlayerbotTradeClaim Of(Player bot, TradeSession? live)
    {
        ArgumentNullException.ThrowIfNull(bot);
        lock (_gate) return Read(bot, live, Clock());
    }

    /// <summary>Whether <paramref name="owner"/> owns the slot for a trade with <paramref name="partner"/>.</summary>
    internal bool Is(Player bot, PlayerbotTradeOwner owner, ObjectGuid partner, TradeSession? live)
    {
        PlayerbotTradeClaim claim = Of(bot, live);
        return claim.Owner == owner && claim.Partner == partner;
    }

    /// <summary>Release the slot, only if <paramref name="owner"/> holds it.</summary>
    internal void Release(Player bot, PlayerbotTradeOwner owner) => Release(bot.Guid, owner);

    /// <summary>Release the slot of the bot with this character guid, only if <paramref name="owner"/> holds it (the desk's failure and close paths have no player).</summary>
    internal void Release(ObjectGuid bot, PlayerbotTradeOwner owner)
    {
        lock (_gate)
            if (_records.TryGetValue(bot.Value, out Record? record) && record.Owner == owner) _records.Remove(bot.Value);
    }

    /// <summary>
    /// A terminal trade status reached the bot (the party AI): release the claim whose window it closed, whoever holds it. The status arrives after the economy closed
    /// the trade and carries no session, so the closed one cannot be named; the rule is built so that it can never clear a claim made after the close:
    /// <list type="bullet">
    /// <item>a trade is still open (<paramref name="live"/> is not null): the status is a stale one of an earlier trade, nothing is released;</item>
    /// <item>no trade is open: only a claim already bound to a session is released (that session is the one that just closed); an unbound claim is a fresh request whose
    /// initiate has not made a session yet and stays, and it heals itself after <see cref="PendingGraceMs"/> if no session ever comes (a refused request).</item>
    /// </list>
    /// </summary>
    internal void ReleaseClosed(Player bot, TradeSession? live)
    {
        ArgumentNullException.ThrowIfNull(bot);
        if (live is not null) return;
        lock (_gate)
            if (_records.TryGetValue(bot.Guid.Value, out Record? record) && record.Session is not null) _records.Remove(bot.Guid.Value);
    }

    /// <summary>Release the slot whoever holds it (tests, and a driver that knows its own window closed).</summary>
    internal void Release(Player bot)
    {
        ArgumentNullException.ThrowIfNull(bot);
        lock (_gate) _records.Remove(bot.Guid.Value);
    }

    /// <summary>The bot left the feature, or the feature stopped: its claim goes.</summary>
    internal void Forget(ObjectGuid bot)
    {
        lock (_gate) _records.Remove(bot.Value);
    }

    /// <summary>Claims held (tests).</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _records.Count;
        }
    }

    private PlayerbotTradeClaim Read(Player bot, TradeSession? live, long now)
    {
        ulong key = bot.Guid.Value;
        if (!_records.TryGetValue(key, out Record? record)) return default;
        if (record.Session is null)
        {
            if (live is null)
            {
                if (now - record.ClaimedMs > PendingGraceMs)
                {
                    _records.Remove(key);
                    return default;
                }
            }
            else if (now - record.ClaimedMs > PendingGraceMs || live.OtherSide(bot).Player.Guid != record.Partner)
            {
                // Too old to be the window its initiate opened (that binds at once), or another partner's: a claim never adopts a later window.
                _records.Remove(key);
                return default;
            }
            else record.Session = live;
        }
        else if (live is null)
        {
            if (record.GoneMs == 0) record.GoneMs = now;
            if (now - record.GoneMs > GoneGraceMs)
            {
                _records.Remove(key);
                return default;
            }
        }
        else if (ReferenceEquals(live, record.Session)) record.GoneMs = 0;
        else
        {
            _records.Remove(key);
            return default;
        }

        return new PlayerbotTradeClaim(record.Owner, record.Partner, record.Session);
    }

    private sealed class Record
    {
        public PlayerbotTradeOwner Owner;
        public ObjectGuid Partner;
        public TradeSession? Session;
        public long ClaimedMs;
        public long GoneMs;
    }
}
