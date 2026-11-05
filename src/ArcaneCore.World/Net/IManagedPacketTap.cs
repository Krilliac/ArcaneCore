using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// Bot QA's view of a managed playerbot session (docs/areas/playbots-qa.md): every packet the bot receives, and every opcode it sent that the world
/// thread cannot run. Called only for playerbot sessions (<see cref="WorldSession.IsPlayerbot"/>); <see cref="OnPacket"/> runs under the session's send
/// lock on the sending thread for every bot packet, so it must switch on the opcode first, be allocation-free for the opcodes it ignores, and never send.
/// </summary>
internal interface IManagedPacketTap
{
    /// <summary>Whether the tap does anything (read once per session; an inactive tap is replaced by <see cref="NoManagedPacketTap"/>).</summary>
    bool Active { get; }

    void OnPacket(WorldSession session, WorldOpcode opcode, byte[] payload);

    /// <summary>The bot asked for <paramref name="opcode"/> and it has no world handler (<paramref name="sessionOnly"/>: it has a session-task one only).</summary>
    void OnUnhandledOpcode(WorldSession session, WorldOpcode opcode, bool sessionOnly);
}

/// <summary>The tap of a server without bot QA: nothing.</summary>
internal sealed class NoManagedPacketTap : IManagedPacketTap
{
    public static NoManagedPacketTap Instance { get; } = new();

    public bool Active => false;

    public void OnPacket(WorldSession session, WorldOpcode opcode, byte[] payload)
    {
    }

    public void OnUnhandledOpcode(WorldSession session, WorldOpcode opcode, bool sessionOnly)
    {
    }
}
