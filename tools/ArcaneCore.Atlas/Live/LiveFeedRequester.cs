using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Binds the dev UI's daemon seam to the live connection: each request goes out through <see cref="LiveFeed.ExchangeAsync"/>, which adds the
/// id, returns the daemon's answer whole (a refusal is an answer), reports no connection as <c>unavailable</c> and honours cancellation.
/// <para>
/// An <c>action</c> waits far longer than a query, because the world answers it only when the command has settled. The world waits for the
/// invoke on its world thread (<see cref="InvokeSeconds"/>) and then <c>SettleSeconds</c> twice (once for the delegate, once for the command's
/// contexts), so one action takes up to <c>InvokeSeconds + 2 * SettleSeconds</c>. The world also handles one line at a time per connection and
/// the wait starts when the line is sent, so a line queued behind another action (or the 2 Hz AI overlay refresh, which is an action) spends
/// part of its wait in the queue: the wait allows one such line ahead of it. Side effect: while an action settles, a query on the same
/// connection (live insight) waits behind it and may time out at <see cref="LiveOptions.QueryTimeout"/>.
/// </para>
/// <para>
/// <c>SettleSeconds</c> is read from <c>caps</c> once per connection; a world that predates the field gets the maximum it accepts. Any thread;
/// owns nothing (the feed is owned and disposed by its module).
/// </para>
/// </summary>
/// <param name="feed">The live connection.</param>
public sealed class LiveFeedRequester(LiveFeed feed) : IFeedRequester
{
    /// <summary>The world-thread invoke timeout (<c>WorldMapFeed.QueryTimeout</c>, src/ArcaneCore.World/Ops/MapFeed/WorldMapFeed.cs:126).</summary>
    public const int InvokeSeconds = 10;

    /// <summary>The largest <c>World:Control:MapFeed:Actions:SettleSeconds</c> the world accepts (<c>MapFeedActionsOptions</c>, range 1 to 600).</summary>
    public const int MaxSettleSeconds = 600;

    /// <summary>Slack on top of the derived wait.</summary>
    public const int MarginSeconds = 15;

    private readonly Lock _gate = new();
    private long _sessionId;
    private TimeSpan _wait;

    /// <summary>
    /// The wait for one action when the world settles for <paramref name="settleSeconds"/> (null: the world did not say, so the maximum):
    /// the action itself (<see cref="InvokeSeconds"/> + 2 x settle) once for a line ahead of it, once for itself, plus <see cref="MarginSeconds"/>.
    /// </summary>
    public static TimeSpan ActionWaitFor(int? settleSeconds)
    {
        int settle = settleSeconds is { } s and >= 1 and <= MaxSettleSeconds ? s : MaxSettleSeconds;
        return TimeSpan.FromSeconds((2 * (InvokeSeconds + (2 * settle))) + MarginSeconds);
    }

    /// <inheritdoc />
    public async Task<JsonObject> RequestAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        TimeSpan? wait = message["op"] is JsonValue op && op.TryGetValue(out string? name) && name == "action"
            ? await ActionWaitAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return await feed.ExchangeAsync(message, cancellationToken, wait).ConfigureAwait(false);
    }

    /// <summary>
    /// The wait an action gets on the current connection: derived from the <c>settleSeconds</c> the world's <c>caps</c> advertises (asked once
    /// per connection and kept), else from the maximum. Unavailable when not connected.
    /// </summary>
    public async Task<TimeSpan> ActionWaitAsync(CancellationToken cancellationToken)
    {
        long session = feed.ConnectionId;
        lock (_gate)
        {
            if (session != 0 && session == _sessionId)
            {
                return _wait;
            }
        }

        int? settle = null;
        bool known = false;
        try
        {
            JsonObject caps = await feed.ExchangeAsync(new JsonObject { ["op"] = "caps" }, cancellationToken).ConfigureAwait(false);
            known = true;
            if (caps["settleSeconds"] is JsonValue v && v.TryGetValue(out int seconds))
            {
                settle = seconds;
            }
        }
        catch (CommandException e) when (e.Code == CommandException.Failed)
        {
            // caps timed out or errored: use the maximum for this action and ask again next time.
        }

        TimeSpan wait = ActionWaitFor(settle);
        if (known && session != 0)
        {
            lock (_gate)
            {
                _sessionId = session;
                _wait = wait;
            }
        }

        return wait;
    }
}
