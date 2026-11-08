using ArcaneCore.Protocol;

namespace ArcaneCore.World.Net;

/// <summary>
/// Bot QA's view of a managed playerbot session (docs/areas/playbots-qa.md): the packets the bot receives that matter to it (<see cref="WantsOpcode"/>),
/// and every opcode it sent that the world thread cannot run. Called only for playerbot sessions (<see cref="WorldSession.IsPlayerbot"/>). A lean bot
/// session (World:Playerbots:ClientFidelity = Lean) hands the tap only the <see cref="WantsOpcode"/> packets (copied for those alone) and drops the rest;
/// a full session hands it every packet, under the session's send lock. So <see cref="OnPacket"/> must switch on the opcode first, be allocation-free for
/// the opcodes it ignores, and never send.
/// </summary>
internal interface IManagedPacketTap
{
    /// <summary>The opcodes the QA tap looks at: a lean session still delivers these (quest kill and item credit, item push, inventory failure).</summary>
    static bool WantsOpcode(WorldOpcode opcode)
        => opcode is WorldOpcode.SmsgQuestupdateAddKill or WorldOpcode.SmsgQuestupdateAddItem or WorldOpcode.SmsgItemPushResult
            or WorldOpcode.SmsgInventoryChangeFailure;

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
