using ArcaneCore.Sniff;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

public sealed class PacketCaptureTests
{
    [Fact]
    public async Task Pkt31RoundTripDecodesBothDirectionsWithoutEncryptedHeaders()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "arcane-pkt-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            using (var capture = new PacketCapture(directory, 1024))
            {
                path = capture.Path;
                capture.Write(false, 0x01EC, BitConverter.GetBytes(123u)); // SMSG_AUTH_CHALLENGE
                capture.Write(true, 0x01DC, BitConverter.GetBytes(123u)); // CMSG_PING: deliberately truncated to exercise error offset
            }
            using var text = new StringWriter();
            Assert.Equal(1, await SniffDecoder.DecodeAsync(path, text));
            Assert.Contains("SMSG_AUTH_CHALLENGE", text.ToString());
            Assert.Contains("decode failed at byte", text.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SizeBoundClosesCaptureBeforeWritingAnOversizeRecord()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "arcane-pkt-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var capture = new PacketCapture(directory, 100);
            capture.Write(false, 0x01EC, new byte[100]);
            Assert.False(capture.IsOpen);
            Assert.Equal(66, new FileInfo(capture.Path).Length);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TruncatedRecordReportsFileByteOffset()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "arcane-pkt-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            using (var capture = new PacketCapture(directory, 1024)) path = capture.Path;
            await using (var file = new FileStream(path, FileMode.Append, FileAccess.Write))
                await file.WriteAsync(new byte[] { (byte)'S' });
            using var output = new StringWriter();
            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => SniffDecoder.DecodeAsync(path, output));
            Assert.Contains("decode failed at byte 66", error.Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Stop_ReturnsWhileTheWriterIsStillDraining_AndTheFileCompletesAfterwards()
    {
        var stream = new GatedStream();
        var capture = new PacketCapture(stream, "gated.pkt", 1024 * 1024);
        capture.Write(false, 0x01EC, new byte[16]);
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The writer is stuck in the disk write. Stop must not wait for it (the session calls it under its send lock).
        var stopped = Task.Run(capture.Stop);
        await stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(capture.IsOpen);
        capture.Write(true, 0x01DC, new byte[16]); // after Stop: dropped, not queued

        stream.Release.SetResult();
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(capture.Faulted);
        Assert.Equal(66 + 24 + 16, stream.ToArray().Length);
    }

    [Fact]
    public async Task WriterDiskError_FaultsAndClosesTheCapture()
    {
        long faultsBefore = CaptureFaults();
        var stream = new GatedStream { Fail = true };
        var capture = new PacketCapture(stream, "failing.pkt", 1024 * 1024);
        capture.Write(false, 0x01EC, new byte[16]);
        stream.Release.SetResult();
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(capture.Faulted);
        Assert.False(capture.IsOpen);
        Assert.Equal(1, CaptureFaults() - faultsBefore);
    }

    [Fact]
    public async Task WriterThatCannotCloseItsStream_RecordsTheFaultAndDisposeDoesNotThrow()
    {
        long faultsBefore = CaptureFaults();
        var stream = new GatedStream { Fail = true, FailDispose = true };
        var capture = new PacketCapture(stream, "failing-close.pkt", 1024 * 1024);
        capture.Write(false, 0x01EC, new byte[16]);
        stream.Release.SetResult();
        await stream.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Exception? thrown = Record.Exception(capture.Dispose); // waits for the writer task

        Assert.Null(thrown);
        Assert.True(capture.Faulted);
        Assert.Equal(2, CaptureFaults() - faultsBefore); // the failed write and the failed close
    }

    private static long CaptureFaults()
        => ArcaneCore.Kernel.Diagnostics.FaultLedger.Shared.Snapshot().Where(s => s.Site == "net.packet-capture").Sum(s => s.Count);

    /// <summary>Header bytes pass through; the first asynchronous write waits for <see cref="Release"/> and then fails or completes.</summary>
    private sealed class GatedStream : MemoryStream
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public bool FailDispose { get; init; }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            if (Fail) throw new IOException("disk full");
            Write(buffer.Span);
        }

        public override ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            if (FailDispose) throw new IOException("close failed");
            return base.DisposeAsync();
        }
    }
}
