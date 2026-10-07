using ArcaneCore.Protocol;

namespace ArcaneCore.World.Playerbots.Scenarios;

public enum ScenarioPacketDirection : byte
{
    /// <summary>Server to bot (an SMSG the bot's session captured).</summary>
    Received,

    /// <summary>Bot to server (a CMSG a step ran through the world handler).</summary>
    Sent,
}

/// <summary>One packet a scenario bot sent or received, with a run-wide sequence number and the world time.</summary>
public sealed record ScenarioPacket(long Sequence, uint WorldMs, ScenarioPacketDirection Direction, WorldOpcode Opcode, byte[] Payload)
{
    public override string ToString()
        => FormattableString.Invariant($"#{Sequence} t={WorldMs} {(Direction == ScenarioPacketDirection.Sent ? "->" : "<-")} {Opcode} ({Payload.Length} B) {Hex(Payload)}");

    private static string Hex(byte[] payload)
        => payload.Length <= 24 ? Convert.ToHexString(payload) : Convert.ToHexString(payload.AsSpan(0, 24)) + "...";
}

/// <summary>
/// Bounded, thread-safe record of one bot's traffic. Packets are captured as the session produces them (not from the
/// session's bounded drain queue), so a burst of updates cannot evict a response before a step looks for it.
/// </summary>
public sealed class ScenarioPacketLog(int capacity = 4096)
{
    private static long s_sequence;
    private readonly object _gate = new();
    private readonly Queue<ScenarioPacket> _packets = new();

    /// <summary>Opcodes too frequent to be useful in a failure report (movement and object updates).</summary>
    public static readonly IReadOnlySet<WorldOpcode> Noise = new HashSet<WorldOpcode>
    {
        WorldOpcode.SmsgUpdateObject, WorldOpcode.SmsgCompressedUpdateObject, WorldOpcode.SmsgMonsterMove,
        WorldOpcode.MsgMoveHeartbeat, WorldOpcode.SmsgPong, WorldOpcode.SmsgDestroyObject,
        WorldOpcode.SmsgEmote, WorldOpcode.SmsgPartyMemberStats,
    };

    /// <summary>The sequence number the next recorded packet (of any bot) gets; use it as a "since" mark.</summary>
    public static long NextSequence => Interlocked.Read(ref s_sequence) + 1;

    public void Record(ScenarioPacketDirection direction, uint worldMs, WorldOpcode opcode, byte[] payload)
    {
        var packet = new ScenarioPacket(Interlocked.Increment(ref s_sequence), worldMs, direction, opcode, payload);
        lock (_gate)
        {
            _packets.Enqueue(packet);
            while (_packets.Count > capacity) _packets.Dequeue();
        }
    }

    public IReadOnlyList<ScenarioPacket> Snapshot()
    {
        lock (_gate) return [.. _packets];
    }

    /// <summary>Received packets of <paramref name="opcode"/> with a sequence at or after <paramref name="since"/>.</summary>
    public IReadOnlyList<ScenarioPacket> Received(WorldOpcode opcode, long since = 0)
    {
        lock (_gate)
            return [.. _packets.Where(p => p.Direction == ScenarioPacketDirection.Received && p.Opcode == opcode && p.Sequence >= since)];
    }

    /// <summary>The last <paramref name="count"/> packets that are not <see cref="Noise"/>.</summary>
    public IReadOnlyList<ScenarioPacket> LastRelevant(int count)
    {
        lock (_gate)
            return [.. _packets.Where(p => !Noise.Contains(p.Opcode)).TakeLast(count)];
    }
}
