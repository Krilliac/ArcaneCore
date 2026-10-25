using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Empty client requests vmangos handles that had no handler here (docs/reference/opcodes.md "unhandled"): the helm and cloak toggles
/// (CharacterHandler.cpp:753-761), the mount's special animation (MovementHandler.cpp:974-979), and the opening-cinematic, cinematic and growth-aura notices
/// vmangos accepts without effect (MiscHandler.cpp:914-921, SpellHandler.cpp:434-437). World thread.
/// </summary>
public sealed class AppearanceHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgToggleHelm, HandleToggleHelm);
        table.OnWorld(WorldOpcode.CmsgToggleCloak, HandleToggleCloak);
        table.OnWorld(WorldOpcode.CmsgMountspecialAnim, HandleMountSpecialAnim);
        table.OnWorld(WorldOpcode.CmsgNextCinematicCamera, static (_, _, _) => { });
        table.OnWorld(WorldOpcode.CmsgCompleteCinematic, static (_, _, _) => { }); // vmangos CinematicEnd: no server cinematic state here
        table.OnWorld(WorldOpcode.CmsgCancelGrowthAura, static (_, _, _) => { }); // vmangos: "nothing do"
        table.OnWorld(WorldOpcode.CmsgOpeningCinematic, static (_, _, _) => { }); // vmangos Opcodes.cpp:330 maps it to Handle_NULL: the client's opening-cinematic notice has no server effect; registered so it leaves the unhandled-opcode counter
    }

    /// <summary>CMSG_TOGGLE_HELM (empty): ToggleFlag(PLAYER_FLAGS, PLAYER_FLAGS_HIDE_HELM).</summary>
    internal static void HandleToggleHelm(WorldSession session, Player player, byte[] payload) => player.Flags ^= PlayerFlags.HideHelm;

    /// <summary>CMSG_TOGGLE_CLOAK (empty): ToggleFlag(PLAYER_FLAGS, PLAYER_FLAGS_HIDE_CLOAK).</summary>
    internal static void HandleToggleCloak(WorldSession session, Player player, byte[] payload) => player.Flags ^= PlayerFlags.HideCloak;

    /// <summary>CMSG_MOUNTSPECIAL_ANIM (empty) → SMSG_MOUNTSPECIAL_ANIM (u64 guid) to the players who see this one, not to itself.</summary>
    internal static void HandleMountSpecialAnim(WorldSession session, Player player, byte[] payload)
        => player.Map?.BroadcastToObservers(player, WorldOpcode.SmsgMountspecialAnim, BitConverter.GetBytes(player.Guid.Value), player);
}
