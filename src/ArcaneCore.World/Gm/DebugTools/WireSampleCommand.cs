using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Args;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Gm.DebugTools;

/// <summary>
/// <c>.wiresample &lt;name&gt;</c>: send the invoker one sample of a server packet no gameplay flow produces (ArcaneCore only; vmangos has no such command),
/// so a real 1.12.1 client can be watched for what it does with it (docs/areas/protocol-decisions.md lists the check). Names: <c>chat-restricted</c>,
/// <c>play-time-warning</c>, <c>expected-spam-records</c>, <c>spline-swim-start</c>, <c>spline-swim-stop</c>, <c>compressed-moves</c>. The bodies come from
/// <see cref="ProtocolGapPackets"/>. The spline and compressed samples name the invoker's own unit, so the client may show the character swimming or
/// running at 7 yards per second until the next movement update; nothing server-side changes. Administrator only; a console invoker has no client and is refused.
/// </summary>
public sealed class WireSampleCommand : ICommandGroup
{
    private const string Names = "chat-restricted, play-time-warning, expected-spam-records, spline-swim-start, spline-swim-stop, compressed-moves";

    public IReadOnlyList<ChatCommand> Commands { get; } =
    [
        new ChatCommand("wiresample", AccountSecurity.Administrator,
            "Syntax: .wiresample $name\nSend yourself one sample of a server packet no gameplay flow sends, to watch a real client's reaction. Names: " + Names + ".", WireSample),
    ];

    private static bool WireSample(CommandContext context, string text)
    {
        string? name = new CommandArgs(text).ExtractArg();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        ulong guid = context.Player.Guid.Value; // throws ConsoleInvokerException for a console invoker, which the executor answers
        (WorldOpcode Opcode, byte[] Body)? sample = name.ToLowerInvariant() switch
        {
            "chat-restricted" => (WorldOpcode.SmsgChatRestricted, ProtocolGapPackets.ChatRestricted()),
            "play-time-warning" => (WorldOpcode.SmsgPlayTimeWarning, ProtocolGapPackets.PlayTimeWarning(0x1000, 600)), // vmangos PTF_APPROACHING_PARTIAL_PLAY_TIME
            "expected-spam-records" => (WorldOpcode.SmsgExpectedSpamRecords, ProtocolGapPackets.ExpectedSpamRecords(["wiresample"])),
            "spline-swim-start" => (WorldOpcode.SmsgSplineMoveStartSwim, ProtocolGapPackets.SplineMoveSwim(guid)),
            "spline-swim-stop" => (WorldOpcode.SmsgSplineMoveStopSwim, ProtocolGapPackets.SplineMoveSwim(guid)),
            "compressed-moves" => (WorldOpcode.SmsgCompressedMoves, ProtocolGapPackets.CompressedRunSpeedMove(guid, 7f)),
            _ => null,
        };
        if (sample is not { } chosen)
        {
            context.Reply("Unknown sample '" + ArcaneCore.Kernel.Logging.LogSafe.Escape(name) + "'. Names: " + Names + ".");
            return true;
        }

        context.Session.Send(chosen.Opcode, chosen.Body);
        context.Reply("Sent " + WorldOpcodeNames.GetName(chosen.Opcode) + ".");
        return true;
    }
}
