using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Security;

/// <summary>
/// A client that stops reading (zero TCP window) parks the writer task inside WriteAsync.
/// Kick() used to cancel only the read side, so RunAsync awaited that writer forever and the
/// socket, DI scope and queued frames leaked. The kick path must tear the stream down.
/// </summary>
public sealed class StalledWriterTests
{
    [Fact]
    public async Task OutboundOverflowKick_CompletesRunAsyncAndDisposesTheStream()
    {
        await using var host = WorldTestHost.Start();
        await using var stream = new StalledStream();
        var options = new WorldSessionOptions
        {
            MaxOutboundBytes = 4096,
            WriterDrainGrace = TimeSpan.FromMilliseconds(300),
        };
        var session = new WorldSession(
            stream, "stalled", host.WorldServices, host.Opcodes, host.World, host.Registry,
            options, NullLogger.Instance);

        Task run = session.RunAsync(CancellationToken.None);

        // The writer is now parked in WriteAsync on the auth challenge; overflow the queue.
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] largeNotification = new byte[8192];
        Array.Fill(largeNotification, (byte)'x');
        largeNotification[^1] = 0; // valid CString, same 8192-byte outbound pressure
        session.Send(WorldOpcode.SmsgNotification, largeNotification);

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stream.Disposed, "the stream must be disposed so the socket is released");
    }

    /// <summary>Reads block until cancelled; writes never complete (but honour cancellation).</summary>
    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
