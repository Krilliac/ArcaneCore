using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Handlers;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Creatures;

/// <summary>
/// CMSG_CREATURE_QUERY (vmangos QueryHandler.cpp HandleCreatureQueryOpcode, STATUS_LOGGEDIN).
/// Answered on the session task: <see cref="CreatureWorldFeature.Content"/> is immutable, so no
/// world-thread hop is needed.
/// </summary>
public sealed class CreatureHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
        => table.OnSession(WorldOpcode.CmsgCreatureQuery, SessionStates.LoggedIn, HandleCreatureQueryAsync);

    /// <summary>CMSG_CREATURE_QUERY: u32 entry, u64 guid (vmangos QueryCreature::Read; gtker agrees).</summary>
    private static Task HandleCreatureQueryAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        uint entry = reader.ReadUInt32();
        _ = reader.Remaining >= 8 ? reader.ReadUInt64() : 0; // guid, informational only

        CreatureWorldFeature? feature = session.Services.GetService<CreatureWorldFeature>();
        CreatureTemplate? template = feature?.Content.FindTemplate(entry);
        session.Send(WorldOpcode.SmsgCreatureQueryResponse, feature is null
            ? CreaturePackets.BuildCreatureQueryResponse(entry, template)
            : feature.QueryCache.Get(entry, template, CreaturePackets.BuildCreatureQueryResponse));
        return Task.CompletedTask;
    }
}
