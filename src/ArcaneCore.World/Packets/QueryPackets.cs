using ArcaneCore.Game;
using ArcaneCore.Protocol;

namespace ArcaneCore.World.Packets;

/// <summary>Query responses (vmangos QueryHandler.cpp / Server/Packets/Query.cpp).</summary>
public static class QueryPackets
{
    /// <summary>
    /// SMSG_NAME_QUERY_RESPONSE for 1.12: u64 GUID, name, realm name (empty: same realm), then
    /// u32 race, u32 gender, u32 class. vmangos Query::NameQueryResponse and gtker
    /// smsg_name_query_response (1.12) agree.
    /// </summary>
    public static byte[] BuildNameQueryResponse(ObjectGuid guid, string name, byte race, byte gender, byte cls)
    {
        var writer = new PacketWriter(32 + name.Length);
        writer.WriteUInt64(guid.Value);
        writer.WriteCString(name);
        writer.WriteCString(string.Empty);
        writer.WriteUInt32(race);
        writer.WriteUInt32(gender);
        writer.WriteUInt32(cls);
        return writer.ToArray();
    }

    /// <summary>SMSG_QUERY_TIME_RESPONSE: u32 Unix time (vmangos Query::QueryTimeResponse).</summary>
    public static byte[] BuildQueryTimeResponse(DateTimeOffset now)
    {
        var writer = new PacketWriter(4);
        writer.WriteUInt32((uint)now.ToUnixTimeSeconds());
        return writer.ToArray();
    }

    /// <summary>SMSG_PLAYED_TIME: u32 total played seconds, u32 seconds at this level (vmangos Misc::PlayedTime).</summary>
    public static byte[] BuildPlayedTime(uint total, uint atLevel)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(total);
        writer.WriteUInt32(atLevel);
        return writer.ToArray();
    }
}
