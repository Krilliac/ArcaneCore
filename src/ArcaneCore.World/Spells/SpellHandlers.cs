using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Spells;

/// <summary>
/// The spell opcodes (vmangos SpellHandler.cpp). All run on the world thread against the
/// session's player and hand off to <see cref="SpellFeature.System"/>.
/// </summary>
public sealed class SpellHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnWorld(WorldOpcode.CmsgCastSpell, HandleCastSpell);
        table.OnWorld(WorldOpcode.CmsgCancelCast, HandleCancelCast);
        table.OnWorld(WorldOpcode.CmsgCancelAura, HandleCancelAura);
        table.OnWorld(WorldOpcode.CmsgCancelChannelling, HandleCancelChannelling);
    }

    private static SpellSystem Spells(WorldSession session) => session.Services.GetRequiredService<SpellFeature>().System;

    /// <summary>CMSG_CAST_SPELL: u32 spell, SpellCastTargets (vmangos HandleCastSpellOpcode; gtker cmsg_cast_spell.wowm).</summary>
    private static void HandleCastSpell(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint spellId = reader.ReadUInt32();
        SpellCastTargets targets = SpellCastTargets.Read(ref reader);
        Spells(session).HandleCastRequest(player, spellId, targets);
    }

    /// <summary>CMSG_CANCEL_CAST: u32 spell (vmangos HandleCancelCastOpcode).</summary>
    private static void HandleCancelCast(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        Spells(session).CancelCast(player, reader.ReadUInt32());
    }

    /// <summary>CMSG_CANCEL_AURA: u32 spell (vmangos HandleCancelAuraOpcode).</summary>
    private static void HandleCancelAura(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        Spells(session).CancelAura(player, reader.ReadUInt32());
    }

    /// <summary>
    /// CMSG_CANCEL_CHANNELLING: u32 spell (vmangos HandleCancelChanneling ignores the id and
    /// interrupts the current channeled spell).
    /// </summary>
    private static void HandleCancelChannelling(WorldSession session, Player player, byte[] payload)
        => Spells(session).CancelChannel(player);
}
