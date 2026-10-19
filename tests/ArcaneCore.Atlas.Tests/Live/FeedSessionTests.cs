using System.Text.Json.Nodes;
using ArcaneCore.Atlas.Core.Commands;
using ArcaneCore.Atlas.Live;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Live;

/// <summary>One connection on its own, without <see cref="LiveFeed"/> around it: what a stuck or cancelled write does to the line.</summary>
public sealed class FeedSessionTests
{
    private sealed class RecordingSink : IFeedSink
    {
        public TaskCompletionSource<string> ClosedReason { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Frame(FeedSession session, ParsedFrame frame)
        {
        }

        public void Closed(FeedSession session, string reason) => ClosedReason.TrySetResult(reason);
    }

    /// <summary>A session that finished its hello exchange with a fake daemon, closed together.</summary>
    private sealed class Connected(FakeFeedServer server, FeedSession session, RecordingSink sink) : IAsyncDisposable
    {
        public FakeFeedServer Server { get; } = server;

        public FeedSession Session { get; } = session;

        public RecordingSink Sink { get; } = sink;

        public async ValueTask DisposeAsync()
        {
            Session.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static async Task<Connected> OpenAsync(TimeSpan writeTimeout)
    {
        string name = FakeFeedServer.NewName();
        var server = new FakeFeedServer(name);
        var sink = new RecordingSink();
        var options = new LiveOptions
        {
            PipeNameFor = _ => name,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            HelloTimeout = TimeSpan.FromSeconds(10),
            WriteTimeout = writeTimeout,
        };
        Task accept = server.AcceptAsync();
        Task<FeedSession> open = FeedSession.OpenAsync(1, name, 4242, options, NullLogger.Instance, sink, CancellationToken.None);
        await accept;
        await server.AnswerHelloAsync(Wire.Hello(pid: 4242));
        FeedSession session = await open.WaitAsync(FakeFeedServer.Patience);
        return new Connected(server, session, sink);
    }

    /// <summary>A message far larger than the pipe's buffers: it can only be written while the other side reads.</summary>
    private static JsonObject Oversized() => new() { ["op"] = "query", ["pad"] = new string('x', 8 * 1024 * 1024) };

    [Fact]
    public async Task AWriteTheServerNeverReads_TimesOut_AndClosesTheConnectionInsteadOfLeavingAHalfLine()
    {
        await using Connected c = await OpenAsync(TimeSpan.FromMilliseconds(400));

        CommandException e = await Assert.ThrowsAsync<CommandException>(() => c.Session.SendAsync(Oversized(), CancellationToken.None));
        string reason = await c.Sink.ClosedReason.Task.WaitAsync(FakeFeedServer.Patience);

        Assert.Equal(CommandException.Unavailable, e.Code);
        Assert.False(c.Session.IsOpen);
        Assert.Contains("stopped accepting data", reason, StringComparison.Ordinal);

        // A later write is refused; it is never appended to the line that was cut off.
        CommandException later = await Assert.ThrowsAsync<CommandException>(
            () => c.Session.SendAsync(new JsonObject { ["op"] = "unsubscribe" }, CancellationToken.None));
        Assert.Equal(CommandException.Unavailable, later.Code);
    }

    [Fact]
    public async Task AWriteTheCallerCancelsPartWay_ClosesTheConnectionToo_AndStillReportsTheCancellation()
    {
        await using Connected c = await OpenAsync(TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource();

        Task write = c.Session.SendAsync(Oversized(), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        string reason = await c.Sink.ClosedReason.Task.WaitAsync(FakeFeedServer.Patience);
        Assert.False(c.Session.IsOpen);
        Assert.Contains("cancelled part way", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerCancelledBeforeTheWriteStarts_LeavesTheConnectionOpen()
    {
        await using Connected c = await OpenAsync(TimeSpan.FromSeconds(30));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.Session.SendAsync(new JsonObject { ["op"] = "unsubscribe" }, cancelled.Token));

        Assert.True(c.Session.IsOpen);
        Assert.False(c.Sink.ClosedReason.Task.IsCompleted);
        await c.Session.SendAsync(new JsonObject { ["op"] = "unsubscribe" }, CancellationToken.None);
        Assert.Equal("unsubscribe", (await c.Server.ReadAsync())["op"]!.GetValue<string>());
    }
}
