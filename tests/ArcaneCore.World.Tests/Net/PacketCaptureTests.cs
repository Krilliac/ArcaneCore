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
}
