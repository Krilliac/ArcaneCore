using ArcaneCore.Protocol;
using ArcaneCore.World.Packets;

namespace ArcaneCore.World.Net;

/// <summary>
/// The movement packets waiting to become one SMSG_COMPRESSED_MOVES for a client (vmangos <c>MovementData</c>, UpdateData.cpp:190-225): per packet a u8 size
/// (opcode and body), the u16 opcode and the body. Not thread-safe: <see cref="WorldSession"/> guards it with its movement lock. One buffer per session
/// that ever batched, reset at each flush.
/// </summary>
internal sealed class MovementPacketBatch
{
    /// <summary>The size byte holds opcode plus body, so a packet over this is never batched (vmangos CanAddPacket: <c>wpos + 2 &gt; 0xFF</c>).</summary>
    public const int MaxEntryBytes = 0xFF;

    /// <summary>The client crashes on a larger batch (vmangos logs "[CRASH-CLIENT] Too large packet size" at 900000).</summary>
    public const int MaxBatchBytes = 900_000;

    private readonly PacketWriter _entries = new(1024);

    public bool HasData => _entries.Length != 0;

    /// <summary>
    /// The opcodes vmangos relays through <c>SendMovementMessageToSet</c> (Object.cpp:2312-2325; Unit.cpp:526, 9840, 10656; MovementHandler.cpp:403-1017;
    /// MovementPacketSender.cpp): the client movement relays (MSG_MOVE_*), SMSG_MONSTER_MOVE and its transport form, and the spline speed and state changes.
    /// </summary>
    public static bool IsBatchable(WorldOpcode opcode) => (ushort)opcode < Batchable.Length && Batchable[(ushort)opcode];

    private static readonly bool[] Batchable = BuildBatchable();

    private static bool[] BuildBatchable()
    {
        WorldOpcode[] all = Enum.GetValues<WorldOpcode>();
        var table = new bool[all.Max(o => (ushort)o) + 1];
        foreach (WorldOpcode opcode in all)
        {
            string name = opcode.ToString();
            table[(ushort)opcode] = name.StartsWith("MsgMove", StringComparison.Ordinal)
                || name.StartsWith("SmsgMonsterMove", StringComparison.Ordinal)
                || name.StartsWith("SmsgSplineMove", StringComparison.Ordinal)
                || name.StartsWith("SmsgSplineSet", StringComparison.Ordinal);
        }

        return table;
    }

    public bool CanAdd(int bodyLength) => bodyLength + 2 <= MaxEntryBytes && _entries.Length + bodyLength + 2 < MaxBatchBytes;

    public void Add(WorldOpcode opcode, ReadOnlySpan<byte> body)
    {
        _entries.WriteByte((byte)(body.Length + 2));
        _entries.WriteUInt16((ushort)opcode);
        _entries.WriteBytes(body);
    }

    /// <summary>The SMSG_COMPRESSED_MOVES body for what was added.</summary>
    public byte[] Build() => ProtocolGapPackets.CompressedMoves(_entries.AsSpan());

    public void Clear() => _entries.Reset(); // keeps the buffer: Build copies the entries out, nothing holds a view of it
}
