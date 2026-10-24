using ArcaneCore.Kernel.Logging;
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
/// rate-limit it per session and opcode; with the heuristics off, which is the shipped default, the counter and the log line naming the account are all
/// that is left); a staff session is written to the GM audit log with no action. Both the log line and the staff audit entry are limited to one per interval
/// per session (and, for staff, per opcode), so a flooding client cannot flood the log or push real GM command lines out of the audit tail, and cannot
/// hide another client's attribution.
/// <para>
/// Thread affinity: any thread the session dispatches on (the reader, a tool injection, or the world thread for a bot). The staff branch reads
/// <see cref="Player"/> (owned by the world thread) from whichever thread dispatches: one atomic reference read, used for display only.
/// </para>
/// </summary>
public sealed partial class WorldSession
{
    private LogGate? _devPlayerGate;
    private readonly object _devStaffGateLock = new();
    private Dictionary<WorldOpcode, LogGate>? _devStaffGates;
    private ILogger? _devAuditLog;

    /// <summary>Drop a stripped developer opcode. Any thread the session dispatches on (the reader, a tool injection, or the world thread for a bot); report only.</summary>
    private void DropDevOpcode(WorldOpcode opcode, int length)
    {
        string name = WorldOpcodeNames.GetName(opcode);
        // The same predicate PacketHeuristicsFeature.Admit uses, so the audit branch and the ledger traffic class never disagree.
        bool staff = IsHeuristicsStaff;
        _devOpcodes.Record(opcode, staff ? PacketTraffic.Staff : HeuristicsToolPacket ? PacketTraffic.Tool : IsPlayerbot ? PacketTraffic.Bot : PacketTraffic.Player);
        if (staff)
        {
            if (!StaffDevGate(opcode).TryEnter(out int coalesced))
            {
                return; // repeats of the same opcode within the interval are counted, not audited again
            }

            string playerName = Player?.Name is { Length: > 0 } character ? character : AccountName;
            string line = GmCommandLog.DescribeDevOpcode(name, AccountId, playerName, length);
            if (coalesced > 0)
            {
                line += $" (+{coalesced} more coalesced)";
            }

            Services.GetService<GmAuditFeature>()?.RecordCommand(AccountId, playerName, "dev-opcode " + name, line);
            // Benign race: two dispatch threads may each create a logger for the same category; either is valid and the loser is dropped.
            _devAuditLog ??= Services.GetService<ILoggerFactory>()?.CreateLogger(GmCommandLog.Category) ?? _logger;
            _devAuditLog.LogInformation("{Line}", line);
            return;
        }

        _heuristics?.DevOpcode(this, opcode, length);
        LogGate gate = _devPlayerGate ?? Interlocked.CompareExchange(ref _devPlayerGate, _devOpcodes.NewGate(), null) ?? _devPlayerGate!;
        if (gate.TryEnter(out int suppressed))
        {
            _logger.LogWarning("[{Endpoint}] account {Account}: client sent the stripped developer opcode {Opcode} (0x{Value:X3}, {Length} bytes); dropped unread, no reply ({Suppressed} more since the last line; metric {Metric}, .server wiring)",
                RemoteEndpoint, AccountId, name, (ushort)opcode, length, suppressed, DevOpcodeTracker.MetricName);
        }
    }

    private LogGate StaffDevGate(WorldOpcode opcode)
    {
        lock (_devStaffGateLock)
        {
            _devStaffGates ??= [];
            if (!_devStaffGates.TryGetValue(opcode, out LogGate? gate))
            {
                gate = _devOpcodes.NewGate();
                _devStaffGates.Add(opcode, gate);
            }

            return gate;
        }
    }
}
