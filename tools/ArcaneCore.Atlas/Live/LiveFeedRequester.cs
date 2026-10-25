using System.Text.Json.Nodes;

namespace ArcaneCore.Atlas.Live;

/// <summary>
/// Binds the dev UI's daemon seam to the live connection: each request goes out through <see cref="LiveFeed.ExchangeAsync"/>, which adds the
/// id, returns the daemon's answer whole (a refusal is an answer), reports no connection as <c>unavailable</c> and honours cancellation.
/// Stateless; any thread. Owns nothing (the feed is owned and disposed by its module).
/// </summary>
/// <param name="feed">The live connection.</param>
public sealed class LiveFeedRequester(LiveFeed feed) : IFeedRequester
{
    /// <inheritdoc />
    public Task<JsonObject> RequestAsync(JsonObject message, CancellationToken cancellationToken)
        => feed.ExchangeAsync(message, cancellationToken);
}
