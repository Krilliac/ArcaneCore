using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// CMSG_SUMMON_RESPONSE (vmangos WorldSession::HandleSummonResponseOpcode, MovementHandler.cpp:981-987; the 1.12 body is the summoner guid only,
/// Server/Packets/Misc.cpp:145-148): accepting a ritual summon teleports a living player out of combat to the summon point while the two minute
/// offer lasts (<see cref="GameObjectSpellEffects.Accept"/>). A short body is ignored.
/// </summary>
public sealed class SummonResponseHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table) => table.OnWorld(WorldOpcode.CmsgSummonResponse, SummonResponse);

    private static void SummonResponse(WorldSession session, Player player, byte[] payload)
    {
        if (payload.Length < 8 || session.Services.GetService<SpellFeature>()?.System is not { } spells)
        {
            return;
        }

        var reader = new PacketReader(payload);
        GameObjectSpellEffects.Accept(player, new ObjectGuid(reader.ReadUInt64()), spells.NowMs, spells.Teleports);
    }
}
