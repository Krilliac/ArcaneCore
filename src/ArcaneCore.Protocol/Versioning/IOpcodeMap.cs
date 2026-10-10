namespace ArcaneCore.Protocol.Versioning;

/// <summary>
/// Maps the server's logical opcodes (<see cref="WorldOpcode"/>) to and from one client build's wire values
/// (multi-version design §3.2; AscEmu world/Version/OpcodeTable.cpp is the prior art). Build 5875 is the identity,
/// because <see cref="WorldOpcode"/> was generated from the 5875 table.
/// </summary>
public interface IOpcodeMap
{
    /// <summary>The wire value of <paramref name="opcode"/> for this build.</summary>
    ushort ToWire(WorldOpcode opcode);

    /// <summary>The logical opcode of a received wire value. Unknown values pass through unchanged so the dispatcher can log and reject them as it does today.</summary>
    WorldOpcode FromWire(uint wire);
}

/// <summary>Build 5875: logical and wire opcodes are the same numbers.</summary>
public sealed class IdentityOpcodeMap : IOpcodeMap
{
    public static readonly IdentityOpcodeMap Instance = new();

    private IdentityOpcodeMap()
    {
    }

    public ushort ToWire(WorldOpcode opcode) => (ushort)opcode;

    public WorldOpcode FromWire(uint wire) => (WorldOpcode)wire;
}
