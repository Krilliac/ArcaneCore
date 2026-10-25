using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Binds the dev UI's daemon seam to the live connection: each request goes out through <see cref="LiveFeed.ExchangeAsync"/>, which adds the
/// id, returns the daemon's answer whole (a refusal is an answer), reports no connection as <c>unavailable</c> and honours cancellation.
/// An <c>action</c> waits far longer than a query: the world answers it only once the command settles. Stateless; any thread. Owns nothing
/// (the feed is owned and disposed by its module).
/// </summary>
/// <param name="feed">The live connection.</param>
public sealed class LiveFeedRequester(LiveFeed feed) : IFeedRequester
{
    /// <summary>
    /// How long an <c>action</c> waits for its answer: the world-thread invoke timeout (10 s, <c>WorldMapFeed</c>) plus the longest settle window
    /// the world accepts (<c>World:Control:MapFeed:Actions:SettleSeconds</c>, 1 to 600, default 30; <c>caps</c> does not advertise it) plus a margin.
    /// The world always answers within that window (<c>settled:false</c> at its end), so this only guards a daemon that has stopped answering.
    /// </summary>
    public static readonly TimeSpan ActionWait = TimeSpan.FromSeconds(10 + 600 + 15);

    /// <inheritdoc />
    public Task<JsonObject> RequestAsync(JsonObject message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        TimeSpan? wait = message["op"] is JsonValue op && op.TryGetValue(out string? name) && name == "action" ? ActionWait : null;
        return feed.ExchangeAsync(message, cancellationToken, wait);
    }
}
