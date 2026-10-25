namespace ArcaneCore.World.Ops.MapFeed;

/// <summary>
/// Several <see cref="IFeedExtension"/> registrations presented to the feed as one (the action channel, the web login, the public links):
/// an op is served by the first extension that handles it. Immutable after construction; any thread. The ops each extension declares do not overlap
/// (<see cref="MapFeedExtensions.IsDeclared"/>), so the order only decides which one is asked first.
/// </summary>
internal sealed class CompositeFeedExtension(IReadOnlyList<IFeedExtension> parts) : IFeedExtension
{
    private readonly IFeedExtension[] _parts = [.. parts];

    /// <inheritdoc />
    public bool Handles(string op)
    {
        foreach (IFeedExtension part in _parts)
        {
            if (part.Handles(op))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void FeedStarted(WorldMapFeed feed)
    {
        foreach (IFeedExtension part in _parts)
        {
            part.FeedStarted(feed);
        }
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(MapFeedConnection connection, string op, ReadOnlyMemory<byte> line)
    {
        foreach (IFeedExtension part in _parts)
        {
            if (part.Handles(op))
            {
                return part.HandleAsync(connection, op, line);
            }
        }

        throw new InvalidOperationException($"no feed extension serves op '{op}'; the caller checks Handles first");
    }

    /// <inheritdoc />
    public async ValueTask ConnectionEndedAsync(MapFeedConnection connection)
    {
        foreach (IFeedExtension part in _parts)
        {
            await part.ConnectionEndedAsync(connection).ConfigureAwait(false);
        }
    }
}
