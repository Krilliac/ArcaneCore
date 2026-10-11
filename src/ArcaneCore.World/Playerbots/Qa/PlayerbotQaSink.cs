using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Options;

namespace ArcaneCore.World.Playerbots.Qa;

/// <summary>Who a direct signal is about: the bot's QA key (never its name) and its class.</summary>
internal readonly record struct QaBot(Guid Key, byte Class);

/// <summary>
/// The direct way to report to bot QA, without a log line: any feature hands a <see cref="QaSignal"/> to <see cref="Report"/>, which does one
/// non-blocking write to the signal queue (the same <see cref="PlayerbotQaSignalBus"/> the log tap feeds). With QA disabled
/// <see cref="Enabled"/> is false and <see cref="Report"/> returns at once; a caller that builds a signal checks <see cref="Enabled"/> first,
/// so a disabled QA costs one property read. Resolve it from DI (<c>GetService&lt;IPlayerbotQaSink&gt;()</c>; null when QA is not
/// registered at all). Thread-safe; never throws for a full queue.
/// </summary>
internal interface IPlayerbotQaSink
{
    /// <summary>Whether reports go anywhere.</summary>
    bool Enabled { get; }

    /// <summary>Queue <paramref name="signal"/> (its <see cref="QaSignal.BotKey"/> and <see cref="QaSignal.BotClass"/> as the caller set them).</summary>
    void Report(QaSignal signal);

    /// <summary>
    /// Queue <paramref name="signal"/> for <paramref name="unit"/> when it is a managed playerbot (its key and class are filled in); a real player,
    /// a creature or a disabled QA reports nothing. True when it was queued.
    /// </summary>
    bool ReportFor(Unit unit, QaSignal signal);
}

/// <summary>The sink of a server without bot QA: it reports nothing.</summary>
internal sealed class NoPlayerbotQaSink : IPlayerbotQaSink
{
    public static NoPlayerbotQaSink Instance { get; } = new();

    public bool Enabled => false;

    public void Report(QaSignal signal)
    {
    }

    public bool ReportFor(Unit unit, QaSignal signal) => false;
}

/// <summary>The production sink (<see cref="IPlayerbotQaSink"/>): the signal bus while QA is enabled, nothing otherwise.</summary>
internal sealed class PlayerbotQaSink : IPlayerbotQaSink
{
    private readonly PlayerbotQaSignalBus? _bus;
    private long _reported;

    public PlayerbotQaSink(PlayerbotQaSignalBus bus, IOptions<PlayerbotQaOptions> options)
        : this(options.Value.Enabled ? bus : null)
    {
    }

    internal PlayerbotQaSink(PlayerbotQaSignalBus? bus) => _bus = bus;

    public bool Enabled => _bus is not null;

    /// <summary>Direct signals queued.</summary>
    internal long Reported => Interlocked.Read(ref _reported);

    public void Report(QaSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (_bus is null) return;
        _bus.TryWrite(signal);
        Interlocked.Increment(ref _reported);
    }

    public bool ReportFor(Unit unit, QaSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (_bus is null || PlayerbotQaBots.Identify(unit) is not { } bot) return false;
        Report(signal with { BotKey = bot.Key, BotClass = bot.Class });
        return true;
    }
}

/// <summary>Which units are managed playerbots, and their QA key. Allocation-free; world thread or any thread (it reads immutable session state).</summary>
internal static class PlayerbotQaBots
{
    /// <summary>
    /// The QA identity of <paramref name="unit"/> when it is a player of a managed playerbot session (not the console's session), otherwise null: a
    /// real player's session is never managed, so nothing a real player does is ever reported.
    /// </summary>
    internal static QaBot? Identify(Unit? unit)
        => unit is Player { Session: WorldSession { IsPlayerbot: true } } player ? new QaBot(KeyOf(player.Guid), (byte)player.Class) : null;

    /// <summary>
    /// The QA key of a bot's character: a guid made from the character guid (a bot's direct signals all share it; its log-line signals carry its
    /// bot id instead, so counts of distinct bots never mix the two kinds of key within one finding). Not a secret and not a name.
    /// </summary>
    internal static Guid KeyOf(ObjectGuid guid)
    {
        ulong v = guid.Value;
        return new Guid(0x0B07_0A11, 0x5afe, 0x0001, (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32), (byte)(v >> 24), (byte)(v >> 16),
            (byte)(v >> 8), (byte)v);
    }
}
