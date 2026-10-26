using System.Globalization;
using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Atlas.Host.Public;

/// <summary>
/// Opens the public host's connections to the world over a named pipe: the dedicated web pipe <c>arcanecore-mapfeed-web-&lt;pid&gt;</c> in separate-process
/// mode (its connections are <c>web</c> by the pipe) or the shared pipe <c>arcanecore-mapfeed-&lt;pid&gt;</c> in in-process mode (the pool then binds the
/// role with the <c>role</c> op). Every connection is a <see cref="FeedSession"/> of its own: nothing is shared with the Local <see cref="LiveFeed"/>.
/// </summary>
internal sealed class PipeFeedConnector(PublicProcessMode mode, int pid, LiveOptions live, ILoggerFactory loggers) : IPublicFeedConnector
{
    /// <summary>The prefix of the world's web pipe (<c>WebFeedPipeOptions.PipeNameTemplate</c> in the world).</summary>
    public const string WebPipePrefix = "arcanecore-mapfeed-web-";

    private long _next;

    /// <inheritdoc />
    public async Task<IPublicFeedConnection> OpenAsync(IPublicFeedSink sink, CancellationToken cancellationToken)
    {
        int target = pid != 0 ? pid : FindWorld();
        string pipe = mode == PublicProcessMode.Separate
            ? WebPipePrefix + target.ToString(CultureInfo.InvariantCulture)
            : LiveProtocol.PipeName(target);
        long id = Interlocked.Increment(ref _next);
        var connection = new Connection(id);
        FeedSession session = await FeedSession.OpenAsync(id, pipe, target, live, loggers.CreateLogger("ArcaneCore.Atlas.Public.Feed"), new Adapter(connection, sink), cancellationToken).ConfigureAwait(false);
        connection.Attach(session);
        return connection;
    }

    private int FindWorld()
    {
        IReadOnlyList<int> feeds = live.Discovery.FindFeeds();
        return feeds.Count switch
        {
            1 => feeds[0],
            0 => throw new CommandException(CommandException.Unavailable, "no world daemon with a map feed was found; set Atlas:Public:Feed:Pid."),
            _ => throw new CommandException(CommandException.Failed, $"{feeds.Count} world daemons serve a map feed ({string.Join(", ", feeds)}); set Atlas:Public:Feed:Pid."),
        };
    }

    private sealed class Connection(long id) : IPublicFeedConnection
    {
        private FeedSession? _session;

        public long Id { get; } = id;

        public HelloInfo Hello => Session.Hello ?? throw new InvalidOperationException("the connection has no hello yet");

        public bool IsOpen => _session?.IsOpen == true;

        public long ServerErrors => _session?.Stats.ServerErrors ?? 0;

        public void Attach(FeedSession session) => _session = session;

        public Task<JsonObject> RequestAsync(JsonObject message, TimeSpan timeout, CancellationToken cancellationToken)
            => Session.QueryAsync(message, cancellationToken, timeout);

        public Task SendAsync(JsonObject message, CancellationToken cancellationToken) => Session.SendAsync(message, cancellationToken);

        public void Dispose() => _session?.Dispose();

        private FeedSession Session => _session ?? throw new InvalidOperationException("the connection is not open");
    }

    private sealed class Adapter(Connection connection, IPublicFeedSink sink) : IFeedSink
    {
        public void Frame(FeedSession session, ParsedFrame frame) => sink.Frame(connection, frame);

        public void Closed(FeedSession session, string reason) => sink.Closed(connection, reason);
    }
}
