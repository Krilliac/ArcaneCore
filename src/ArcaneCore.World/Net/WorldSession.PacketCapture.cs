using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Net;

public sealed partial class WorldSession
{
    private PacketCapture? _packetCapture;

    /// <summary>Start a new trace for this authenticated connection, when diagnostics permits it.</summary>
    public bool StartPacketCapture(out string message)
    {
        var options = new PacketCaptureOptions();
        if (Services.GetService(typeof(IConfiguration)) is IConfiguration configuration)
            configuration.GetSection(PacketCaptureOptions.SectionName).Bind(options);
        if (!options.Enabled)
        {
            message = "Packet capture is disabled by Diagnostics:PacketCapture:Enabled.";
            return false;
        }

        if (_state is SessionState.Connected or SessionState.Closed)
        {
            message = "Only an authenticated session can be captured.";
            return false;
        }

        lock (_sendLock)
        {
            if (_state == SessionState.Closed)
            {
                message = "Session already closed.";
                return false;
            }
            if (_packetCapture?.IsOpen == true)
            {
                message = "Packet capture is already on for this session.";
                return false;
            }

            try
            {
                _packetCapture?.Dispose();
                _packetCapture = new PacketCapture(options.Directory, options.MaxBytes);
                message = "Packet capture on: " + _packetCapture.Path;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                message = "Packet capture could not start: " + ex.Message;
                return false;
            }
        }
    }

    public bool StopPacketCapture(out string message)
    {
        lock (_sendLock)
        {
            if (_packetCapture is null)
            {
                message = "Packet capture was not on for this session.";
                return false;
            }

            message = "Packet capture off: " + _packetCapture.Path;
            _packetCapture.Dispose();
            _packetCapture = null;
            return true;
        }
    }

    private void CapturePacket(bool fromClient, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        // Off (the default): no lock on the per-packet path. A start racing this read only misses one packet.
        if (Volatile.Read(ref _packetCapture) is null) return;

        // PKT writer serializes reads and sends. Never let a diagnostic disk error drop a connection.
        lock (_sendLock)
        {
            PacketCapture? capture = _packetCapture;
            if (capture is null) return;
            try { capture.Write(fromClient, (ushort)opcode, payload); }
            catch (IOException) { capture.Dispose(); _packetCapture = null; }
        }
    }
}
