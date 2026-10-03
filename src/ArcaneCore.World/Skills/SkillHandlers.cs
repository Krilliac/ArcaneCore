using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Skills;

/// <summary>The skill opcodes (vmangos SkillHandler.cpp). World thread, against the session's player.</summary>
public sealed class SkillHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgUnlearnSkill, HandleUnlearnSkill);

    /// <summary>CMSG_UNLEARN_SKILL: u32 skill (vmangos HandleUnlearnSkillOpcode; gtker cmsg_unlearn_skill.wowm).</summary>
    private static void HandleUnlearnSkill(WorldSession session, Player player, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint skillId = reader.ReadUInt32();
        session.Services.GetRequiredService<SkillsFeature>().TryUnlearnSkill(player, skillId);
    }
}
