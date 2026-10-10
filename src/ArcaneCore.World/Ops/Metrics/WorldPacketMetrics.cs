using ArcaneCore.Kernel.Ops.Metrics;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Ops.Metrics;

/// <summary>
/// The per-opcode traffic table of the world socket (protocol label <c>world</c>). The known set is every
/// <see cref="WorldOpcode"/> (0..827 for build 5875, 826 unassigned: vmangos Opcodes_1_12_1.h:825-828, generated into
/// WorldOpcode.g.cs) under its reference name from <see cref="WorldOpcodeNames"/>; the gap and anything past 827 count in
/// the single <c>unknown</c> bucket. Shared process-wide, like <see cref="ArcaneMeters"/>.
/// </summary>
public static class WorldPacketMetrics
{
    /// <summary>The <c>protocol</c> label value.</summary>
    public const string Protocol = "world";

    /// <summary>The world table; recording is a no-op until the store listens.</summary>
    public static OpcodeTable Table { get; } = ArcaneMeters.Opcodes.Register(
        Protocol,
        Enum.GetValues<WorldOpcode>().Distinct().Select(o => new KeyValuePair<int, string>((int)o, WorldOpcodeNames.GetName(o))));
}
