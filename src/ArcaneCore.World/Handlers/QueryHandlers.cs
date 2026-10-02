using ArcaneCore.Game;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters;
using ArcaneCore.World.Net;
using ArcaneCore.World.Packets;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.Handlers;

/// <summary>
/// Name and time queries (vmangos QueryHandler.cpp). Both are answered from thread-safe state
/// on the session task; vmangos registers them STATUS_LOGGEDIN.
/// </summary>
public sealed class QueryHandlers : IOpcodeHandlerGroup
{
    public void Register(OpcodeTable table)
    {
        table.OnSession(WorldOpcode.CmsgNameQuery, SessionStates.LoggedIn, HandleNameQueryAsync);
        table.OnSession(WorldOpcode.CmsgQueryTime, SessionStates.LoggedIn, HandleQueryTimeAsync);
    }

    /// <summary>
    /// CMSG_NAME_QUERY: u64 GUID. Answered from the character directory, which holds every
    /// character — online or not — like vmangos' player cache; an unknown GUID gets no reply,
    /// as in vmangos SendNameQueryOpcodeFromDB.
    /// </summary>
    private static Task HandleNameQueryAsync(WorldSession session, byte[] payload)
    {
        var reader = new PacketReader(payload);
        ulong raw = reader.ReadUInt64();
        if (raw is > 0 and <= int.MaxValue
            && session.Services.GetRequiredService<CharacterDirectory>().Find((int)raw) is { } identity)
        {
            session.Send(WorldOpcode.SmsgNameQueryResponse, QueryPackets.BuildNameQueryResponse(
                ObjectGuid.Player((uint)identity.Id), identity.Name, identity.Race, identity.Gender, identity.Class));
        }

        return Task.CompletedTask;
    }

    /// <summary>CMSG_QUERY_TIME (empty) → SMSG_QUERY_TIME_RESPONSE with the server's Unix time.</summary>
    private static Task HandleQueryTimeAsync(WorldSession session, byte[] payload)
    {
        session.Send(WorldOpcode.SmsgQueryTimeResponse, QueryPackets.BuildQueryTimeResponse(DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }
}
