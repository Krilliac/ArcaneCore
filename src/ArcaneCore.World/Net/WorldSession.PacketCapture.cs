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
                _packetCapture?.Stop(); // a finished or dead trace: its writer closes the file in the background
                _packetCapture = new PacketCapture(options.Directory, options.MaxBytes, _logger);
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
            _packetCapture.Stop(); // the writer drains what is queued and closes the file; the lock is not held for the disk
            _packetCapture = null;
            return true;
        }
    }

    private void CapturePacket(bool fromClient, WorldOpcode opcode, ReadOnlySpan<byte> payload)
    {
        // GM session trace (WorldSession.SessionTrace.cs): every reader path calls this for an inbound packet; untraced is one volatile read.
        if (fromClient && Volatile.Read(ref _sessionTrace) is { } sessionTrace)
        {
            sessionTrace.Inbound(opcode, payload);
        }

        // Packet-tools taps (WorldSession.PacketTools.cs): none by default, one array read on the per-packet path.
        ISessionPacketTap[] taps = Volatile.Read(ref _taps);
        if (taps.Length != 0)
        {
            OfferToTaps(taps, fromClient, opcode, payload);
        }

        // Off (the default): no lock on the per-packet path. A start racing this read only misses one packet.
        if (Volatile.Read(ref _packetCapture) is null) return;

        // Write only queues the record (disk errors surface in the capture's background writer), so the send lock is held briefly.
        lock (_sendLock)
        {
            PacketCapture? capture = _packetCapture;
            if (capture is null) return;
            if (capture.Faulted)
            {
                _packetCapture = null; // the writer already recorded the disk error; drop the dead trace so .debug capture reports it off
                return;
            }

            capture.Write(fromClient, (ushort)opcode, payload);
        }
    }
}
