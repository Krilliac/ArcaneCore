using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Ranged;

/// <summary>
/// CMSG_CANCEL_AUTO_REPEAT_SPELL (621, empty payload; wow_messages cmsg_cancel_auto_repeat_spell.wowm): the player toggled Auto Shot or
/// Shoot off. vmangos HandleCancelAutoRepeatSpellOpcode (SpellHandler.cpp:439-444) interrupts the auto-repeat slot of the player's mover,
/// which sends SMSG_CANCEL_AUTO_REPEAT (668, empty) back and cancels the spell.
/// </summary>
public sealed class AutoRepeatHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgCancelAutoRepeatSpell, HandleCancelAutoRepeat);

    private static void HandleCancelAutoRepeat(WorldSession session, Player player, byte[] payload)
        => session.Services.GetRequiredService<SpellFeature>().System.CancelAutoRepeat(player);
}
