using ArcaneCore.Protocol;
using ArcaneCore.World.AntiCheat.PacketHeuristics;
using ArcaneCore.World.Gm.Audit;
using ArcaneCore.World.Gm.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.World.Net;

/// <summary>
/// The drop of a stripped developer opcode (<see cref="ArcaneCore.World.Handlers.StrippedDevOpcodes"/>): the 1.12.1 release client never sends one, so whoever does is
/// running a modified client or a tool. The payload is never read and nothing is answered. It is counted (<see cref="DevOpcodeTracker"/>, never in
/// <see cref="UnhandledOpcodeTracker"/>), and then: a tool injection, a bot or a non-staff player is a <c>packet/dev-opcode</c> finding (the heuristics
/// rate-limit it per session and opcode); a staff session is written to the GM audit log with no action. The log line is limited to one per interval
/// for the whole process, so a flooding client cannot flood the log.
/// </summary>
public sealed partial class WorldSession
{
    /// <summary>Drop a stripped developer opcode. Any thread the session dispatches on (the reader, a tool injection, or the world thread for a bot); report only.</summary>
    private void DropDevOpcode(WorldOpcode opcode, int length)
    {
        string name = WorldOpcodeNames.GetName(opcode);
        bool staff = !IsPlayerbot && !HeuristicsToolPacket && (Security > Kernel.Accounts.AccountSecurity.Player || HeuristicsGmMode);
        _devOpcodes.Record(opcode, staff ? PacketTraffic.Staff : HeuristicsToolPacket ? PacketTraffic.Tool : IsPlayerbot ? PacketTraffic.Bot : PacketTraffic.Player);
        if (staff)
        {
            string playerName = Player?.Name is { Length: > 0 } character ? character : AccountName;
            string line = GmCommandLog.DescribeDevOpcode(name, AccountId, playerName, length);
            Services.GetService<GmAuditFeature>()?.RecordCommand(AccountId, playerName, "dev-opcode " + name, line);
            if (_devOpcodes.StaffGate.TryEnter(out int staffSuppressed))
            {
                ILogger log = Services.GetService<ILoggerFactory>()?.CreateLogger(GmCommandLog.Category) ?? _logger;
                log.LogInformation("{Line} ({Suppressed} more audited since the last line)", line, staffSuppressed);
            }

            return;
        }

        _heuristics?.DevOpcode(this, opcode, length);
        if (_devOpcodes.PlayerGate.TryEnter(out int suppressed))
        {
            _logger.LogWarning("[{Endpoint}] client sent the stripped developer opcode {Opcode} (0x{Value:X3}, {Length} bytes); dropped unread, no reply ({Suppressed} more since the last line; metric {Metric}, .server wiring)",
                RemoteEndpoint, name, (ushort)opcode, length, suppressed, DevOpcodeTracker.MetricName);
        }
    }
}
